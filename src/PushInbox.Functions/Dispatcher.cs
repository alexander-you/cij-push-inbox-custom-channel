using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace PushInbox.Functions;

public sealed class Store(CosmosClient client, string database)
{
    public Container Dispatches { get; } = client.GetContainer(database, "dispatches");
    public Container Inbox { get; } = client.GetContainer(database, "inboxItems");
}

/// <summary>
/// Service Bus "dispatch" queue trigger: writes the inbox item, sends the Web Push through Notification Hubs,
/// and reports Delivered / NotDelivered back to CIJ when the send has a RequestId.
/// </summary>
public sealed class Dispatcher(Store store, NotificationHubRest hub, DataverseClient dataverse, ILogger<Dispatcher> log)
{
    [Function("Dispatcher")]
    public async Task Run([ServiceBusTrigger("%DISPATCH_QUEUE%", Connection = "ServiceBus")] string body, CancellationToken ct)
    {
        var message = JsonConvert.DeserializeObject<DispatchMessage>(body)!;
        var dir = Keys.Direction(message.Title, message.Body);
        var stored = false;
        string? reason = null, detail = null;
        var pushed = 0;

        if (message.Mode != DeliveryMode.AlertOnly)
        {
            var now = DateTimeOffset.UtcNow;
            var item = new InboxItem
            {
                Id = Keys.InboxItemId(message.AppKey, message.ReplaceKey, message.MessageId),
                RecipientKey = message.RecipientKey,
                AppKey = message.AppKey,
                MessageId = message.MessageId,
                RequestId = message.RequestId,
                ChannelDefinitionId = message.ChannelDefinitionId,
                CustomerJourneyId = message.CustomerJourneyId,
                Title = message.Title,
                Body = message.Body,
                ImageUrl = message.ImageUrl,
                ActionUrl = message.ActionUrl,
                ReplaceKey = message.ReplaceKey,
                Dir = dir,
                CreatedOn = now,
                ExpiresOn = now.AddDays(message.ExpiryDays),
                Ttl = message.ExpiryDays * 86400,
            };
            await store.Inbox.UpsertItemAsync(item, new PartitionKey(item.RecipientKey), cancellationToken: ct);
            stored = true;
            log.LogInformation("Inbox item {ItemId} stored for recipient {RecipientLogId}, ttl {Ttl}s", item.Id, Keys.LogId(item.RecipientKey), item.Ttl);
        }

        if (message.Mode != DeliveryMode.InboxOnly)
        {
            var payload = JsonConvert.SerializeObject(new
            {
                title = message.Title,
                body = message.Body,
                image = message.ImageUrl,
                url = message.ActionUrl,
                messageId = message.MessageId,
                dir,
                lang = dir == "rtl" ? "he" : "en",
            });
            // Transitional: devices registered before tags were hashed still carry the plain rk: tag until they re-register.
            var tags = $"({Keys.RecipientTag(message.RecipientKey)} || rk:{message.RecipientKey}) && app:{message.AppKey}";
            var outcome = await hub.SendBrowserAsync(payload, tags, message.IsTestSend, ct);
            log.LogInformation("Notification Hubs send {Status} test {Test} success {Success} failure {Failure} results [{Results}] id {NotificationId}",
                outcome.HttpStatus, message.IsTestSend, outcome.Success, outcome.Failure, string.Join(",", outcome.Results), outcome.NotificationId);

            if (outcome.HttpStatus is < 200 or >= 300)
            {
                reason = "PushServiceError";
                detail = $"Notification Hubs returned {outcome.HttpStatus}: {Truncate(outcome.Raw, 300)}";
                if (outcome.HttpStatus >= 500 || outcome.HttpStatus == 429) throw new InvalidOperationException(detail);
            }
            else if (message.IsTestSend)
            {
                pushed = outcome.Success;
                if (pushed == 0) { reason = "NoDevice"; detail = "No registered browser accepted the notification for this recipient."; }
            }
            else
            {
                // Standard sends: Notification Hubs accepted the request; per-device results need the Standard tier.
                pushed = 1;
            }
        }

        var delivered = stored || pushed > 0;
        await UpdateDispatchAsync(message, delivered ? "Delivered" : "NotDelivered", detail, ct);
        await ReportAsync(message, delivered, reason, detail, ct);
    }

    private async Task UpdateDispatchAsync(DispatchMessage message, string status, string? detail, CancellationToken ct)
    {
        await store.Dispatches.PatchItemAsync<DispatchRecord>(message.IdempotencyId, new PartitionKey(message.IdempotencyId), new[]
        {
            PatchOperation.Set("/status", status),
            PatchOperation.Set("/statusDetail", detail),
            PatchOperation.Set("/updatedOn", DateTimeOffset.UtcNow),
        }, cancellationToken: ct);
    }

    private async Task ReportAsync(DispatchMessage message, bool delivered, string? reason, string? detail, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.RequestId))
        {
            log.LogInformation("Delivery report skipped for {MessageId}: the CIJ request has no RequestId (test send).", message.MessageId);
            return;
        }
        if (!dataverse.IsConfigured)
        {
            log.LogWarning("Delivery report skipped for {MessageId}: DATAVERSE_URL is not set.", message.MessageId);
            return;
        }
        var result = await dataverse.SendDeliveryReportAsync(message, delivered, reason, detail, ct);
        log.LogInformation("Delivery report {Status} sent for request {RequestId}: {Result}", delivered ? "Delivered" : "NotDelivered", message.RequestId, Truncate(result, 300));
    }

    private static string Truncate(string? text, int max) => text == null || text.Length <= max ? text ?? "" : text[..max];
}
