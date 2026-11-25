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
/// Service Bus "dispatch" queue trigger: writes the inbox item, sends the push through Notification Hubs to every platform
/// (browser Web Push, FCM v1, APNs),
/// and reports Delivered / NotDelivered back to CIJ when the send has a RequestId.
/// Each finished step is recorded on the dispatch record (storedOn, pushedOn, reportedOn). A Service Bus redelivery,
/// or a second enqueue of the same IdempotencyId, repeats only the unfinished steps, so a failed delivery report
/// is retried without pushing again.
/// </summary>
public sealed class Dispatcher(Store store, NotificationHubRest hub, DataverseClient dataverse, MessageLog messageLog, ILogger<Dispatcher> log)
{
    /// <summary>Platforms to send to (app setting PUSH_PLATFORMS, default browser,fcmv1,apns). A platform without hub credentials is skipped.</summary>
    private static readonly IReadOnlyList<PushPlatform> Platforms = PushPlatform.Parse(Environment.GetEnvironmentVariable("PUSH_PLATFORMS"));

    [Function("Dispatcher")]
    public async Task Run([ServiceBusTrigger("%DISPATCH_QUEUE%", Connection = "ServiceBus")] string body, CancellationToken ct)
    {
        var message = JsonConvert.DeserializeObject<DispatchMessage>(body)!;
        var record = (await store.Dispatches.ReadItemAsync<DispatchRecord>(message.IdempotencyId, new PartitionKey(message.IdempotencyId), cancellationToken: ct)).Resource;
        var dir = Keys.Direction(message.Title, message.Body);
        var dispatched = false;
        int? pnsSuccess = null, pnsFailure = null;

        if (message.Mode != DeliveryMode.AlertOnly && record.StoredOn == null)
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
                IsTestSend = message.IsTestSend,
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
            log.LogInformation("Inbox item {ItemId} stored for recipient {RecipientLogId}, ttl {Ttl}s", item.Id, Keys.LogId(item.RecipientKey), item.Ttl);
            record.StoredOn = now;
            dispatched = true;
            await PatchAsync(message, ct, PatchOperation.Set("/storedOn", now));
        }
        else if (record.StoredOn != null)
        {
            log.LogInformation("Inbox step already done for {MessageId}; skipping", message.MessageId);
        }

        if (message.Mode != DeliveryMode.InboxOnly && record.PushedOn == null)
        {
            var content = new PushContent(message.Title, message.Body, message.ImageUrl, message.ActionUrl, message.MessageId,
                Keys.InboxItemId(message.AppKey, message.ReplaceKey, message.MessageId), dir);
            // Transitional: devices registered before tags were hashed still carry the plain rk: tag until they re-register.
            var tags = $"({Keys.RecipientTag(message.RecipientKey)} || rk:{message.RecipientKey}) && app:{message.AppKey}";
            var results = record.PlatformResults ?? new Dictionary<string, PlatformResult>();
            foreach (var platform in Platforms.Where(p => !results.ContainsKey(p.Name)))
            {
                var outcome = await hub.SendAsync(platform.Format, platform.BuildPayload(content), tags, message.IsTestSend, platform.Headers, platform.ApiVersion, ct);
                log.LogInformation("Notification Hubs {Platform} send {Status} test {Test} success {Success} failure {Failure} results [{Results}] id {NotificationId}",
                    platform.Name, outcome.HttpStatus, message.IsTestSend, outcome.Success, outcome.Failure, string.Join(",", outcome.Results), outcome.NotificationId);
                // Transient: nothing was pushed on this platform. Platforms already sent are recorded, so the redelivery sends only the rest.
                if (PushOutcome.IsTransient(outcome)) throw new InvalidOperationException($"Notification Hubs {platform.Name} send returned {outcome.HttpStatus}: {Truncate(outcome.Raw, 300)}");
                results[platform.Name] = PushOutcome.Classify(outcome, message.IsTestSend);
                record.PlatformResults = results;
                await PatchAsync(message, ct, PatchOperation.Set("/platformResults", results));
            }

            dispatched = true;
            if (message.IsTestSend) (pnsSuccess, pnsFailure) = (results.Values.Sum(r => r.Success ?? 0), results.Values.Sum(r => r.Failure ?? 0));
            var (pushed, reason, detail) = PushOutcome.Aggregate(results);
            detail = Join(PushOutcome.Summary(results), detail);

            // Recorded straight after the send so that nothing that fails later (status update, delivery report) causes a second push.
            var pushedOn = DateTimeOffset.UtcNow;
            (record.PushedOn, record.PushedCount, record.FailureReason, record.StatusDetail) = (pushedOn, pushed, reason, detail);
            await PatchAsync(message, ct,
                PatchOperation.Set("/pushedOn", pushedOn),
                PatchOperation.Set("/pushedCount", pushed),
                PatchOperation.Set("/failureReason", reason),
                PatchOperation.Set("/statusDetail", detail));
        }
        else if (record.PushedOn != null)
        {
            log.LogInformation("Push step already done for {MessageId} on {PushedOn}; not pushing again", message.MessageId, record.PushedOn);
        }

        if (dispatched)
        {
            var e = LogEvent.For(message, LogEventType.Dispatched);
            (e.PnsSuccess, e.PnsFailure) = (pnsSuccess, pnsFailure);
            e.Detail = Join(record.FailureReason, record.StatusDetail);
            await messageLog.EmitAsync(e);
        }

        var delivered = record.StoredOn != null || record.PushedCount > 0;
        await PatchAsync(message, ct,
            PatchOperation.Set("/status", delivered ? "Delivered" : "NotDelivered"),
            PatchOperation.Set("/updatedOn", DateTimeOffset.UtcNow));

        if (record.ReportedOn != null)
        {
            log.LogInformation("Delivery report already sent for {MessageId}", message.MessageId);
            return;
        }
        try
        {
            var reported = await ReportAsync(message, delivered, record.FailureReason, record.StatusDetail, ct);
            if (reported)
            {
                await PatchAsync(message, ct, PatchOperation.Set("/reportedOn", DateTimeOffset.UtcNow), PatchOperation.Set("/reportError", (string?)null));
            }
            var outcomeEvent = LogEvent.For(message, delivered ? LogEventType.Delivered : LogEventType.NotDelivered);
            outcomeEvent.Detail = Join(delivered ? null : record.FailureReason, delivered ? null : record.StatusDetail,
                reported ? null : "Not reported to Customer Insights - Journeys (test send or no Dataverse URL)");
            await messageLog.EmitAsync(outcomeEvent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The push and inbox steps are recorded, so the Service Bus redelivery only retries this report.
            // After maxDeliveryCount the message moves to the dead-letter queue with the error below.
            log.LogWarning(ex, "Delivery report failed for {MessageId} request {RequestId}; Service Bus will retry the report only", message.MessageId, message.RequestId);
            await PatchAsync(message, ct, PatchOperation.Set("/reportError", Truncate(ex.Message, 300)));
            throw;
        }
    }

    private const int ReportAttempts = 3;

    private Task PatchAsync(DispatchMessage message, CancellationToken ct, params PatchOperation[] operations) =>
        store.Dispatches.PatchItemAsync<DispatchRecord>(message.IdempotencyId, new PartitionKey(message.IdempotencyId), operations, cancellationToken: ct);

    /// <summary>Returns true when a report was sent; false when there is nothing to report (test send or no Dataverse URL).</summary>
    private async Task<bool> ReportAsync(DispatchMessage message, bool delivered, string? reason, string? detail, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.RequestId))
        {
            log.LogInformation("Delivery report skipped for {MessageId}: the CIJ request has no RequestId (test send).", message.MessageId);
            return false;
        }
        if (!dataverse.IsConfigured)
        {
            log.LogWarning("Delivery report skipped for {MessageId}: DATAVERSE_URL is not set.", message.MessageId);
            return false;
        }
        // Service Bus redelivers a failed message almost immediately, so back off in-process before giving up on this delivery.
        string result;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                result = await dataverse.SendDeliveryReportAsync(message, delivered, reason, detail, ct);
                break;
            }
            catch (Exception ex) when (attempt < ReportAttempts && ex is not OperationCanceledException)
            {
                log.LogWarning("Delivery report attempt {Attempt} failed for {MessageId}: {Error}", attempt, message.MessageId, Truncate(ex.Message, 200));
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt * attempt), ct);
            }
        }
        log.LogInformation("Delivery report {Status} sent for request {RequestId}: {Result}", delivered ? "Delivered" : "NotDelivered", message.RequestId, Truncate(result, 300));
        return true;
    }

    private static string? Join(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrEmpty(p)) is var some && some.Any() ? string.Join(" | ", some) : null;

    private static string Truncate(string? text, int max) => text == null || text.Length <= max ? text ?? "" : text[..max];
}
