using System.Net;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PushInbox.Functions;

/// <summary>
/// POST /api/messages - called by the alex_PushOutbound plug-in with the unchanged CIJ outbound payload.
/// Validates, de-duplicates on IdempotencyId, enqueues and answers within the 20-second CIJ limit.
/// A retry whose earlier attempt never confirmed the enqueue (record still Pending) is enqueued again.
/// </summary>
public sealed class ChannelApi(Store store, ServiceBusSender sender, ILogger<ChannelApi> log)
{
    private const int DefaultExpiryDays = 30;
    private const int MaxExpiryDays = 180;

    [Function("Messages")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "messages")] HttpRequest req, CancellationToken ct)
    {
        JObject payload;
        try
        {
            payload = JObject.Parse(await new StreamReader(req.Body).ReadToEndAsync(ct));
        }
        catch (JsonException)
        {
            return Problem(HttpStatusCode.BadRequest, "InvalidPayload", "The body is not JSON.");
        }

        var appKey = Keys.App(req.Headers["x-push-app-key"].ToString());
        if (appKey.Length == 0) return Problem(HttpStatusCode.BadRequest, "MissingAppKey", "The channel instance has no app key.");

        var message = payload["Message"] as JObject ?? new JObject();
        var context = payload["MarketingAppContext"] as JObject;
        var title = (string?)message["title"] ?? "";
        var body = (string?)message["body"] ?? "";
        if (title.Length == 0 && body.Length == 0) return Problem(HttpStatusCode.BadRequest, "EmptyMessage", "Title and body are both empty.");

        if (!TryParseMode((string?)message["deliveryMode"], out var mode))
        {
            return Problem(HttpStatusCode.BadRequest, "InvalidDeliveryMode", $"Unknown delivery mode '{message["deliveryMode"]}'.");
        }

        // The outbound plug-in resolves the asset-library image (imageId part) to its public CDN URL and sends it in this header.
        var imageUrl = Https(req.Headers["x-push-image-url"].ToString()) ?? Https((string?)message["imageId"]);
        var to = (string?)payload["To"] ?? "";
        var userId = (string?)context?["UserId"];
        var userType = (string?)context?["UserEntityType"];
        var recipientKey = !string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(userType)
            ? Keys.Recipient($"{userType}_{userId}")
            : Keys.Recipient(to);
        if (recipientKey.Length == 0) return Problem(HttpStatusCode.BadRequest, "MissingRecipient", "Neither MarketingAppContext.UserId nor To identifies a recipient.");

        // Test sends carry empty RequestId and IdempotencyId (observed); generate a unique key so each test send is processed.
        var requestId = (string?)payload["RequestId"] ?? "";
        var idempotencyId = (string?)payload["IdempotencyId"];
        if (string.IsNullOrWhiteSpace(idempotencyId)) idempotencyId = "gen-" + Guid.NewGuid().ToString("n");

        var record = new DispatchRecord
        {
            Id = idempotencyId,
            IdempotencyId = idempotencyId,
            MessageId = Guid.NewGuid().ToString(),
            RequestId = requestId,
            RecipientKey = recipientKey,
            AppKey = appKey,
            IsTestSend = (bool?)context?["IsTestSend"] ?? false,
            CreatedOn = DateTimeOffset.UtcNow,
        };

        var reEnqueue = false;
        try
        {
            await store.Dispatches.CreateItemAsync(record, new PartitionKey(idempotencyId), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = (await store.Dispatches.ReadItemAsync<DispatchRecord>(idempotencyId, new PartitionKey(idempotencyId), cancellationToken: ct)).Resource;
            if (existing.Status != DispatchStatus.Pending)
            {
                log.LogInformation("Duplicate IdempotencyId {IdempotencyId}; returning MessageId {MessageId}", idempotencyId, existing.MessageId);
                return new ObjectResult(new { messageId = existing.MessageId, duplicate = true }) { StatusCode = 202 };
            }
            // The earlier attempt created the record but never confirmed the enqueue (for example Service Bus failed).
            // Re-enqueue from this retry's payload under the original MessageId; the Dispatcher's step markers absorb a double enqueue.
            log.LogWarning("IdempotencyId {IdempotencyId} is still Pending; re-enqueuing MessageId {MessageId}", idempotencyId, existing.MessageId);
            record.MessageId = existing.MessageId;
            reEnqueue = true;
        }

        var dispatch = new DispatchMessage
        {
            MessageId = record.MessageId,
            IdempotencyId = idempotencyId,
            RequestId = requestId,
            ChannelDefinitionId = (Guid?)payload["ChannelDefinitionId"] ?? Guid.Empty,
            From = (string?)payload["From"] ?? "",
            To = to,
            AppKey = appKey,
            RecipientKey = recipientKey,
            CustomerJourneyId = (string?)context?["CustomerJourneyId"],
            IsTestSend = record.IsTestSend,
            Title = title,
            Body = body,
            ImageUrl = imageUrl,
            ActionUrl = (string?)message["actionUrl"] is { Length: > 0 } a ? a : null,
            Mode = mode,
            ExpiryDays = ExpiryDays((string?)message["inboxExpiryDays"], req.Headers["x-push-default-expiry-days"].ToString()),
            ReplaceKey = (string?)message["replaceKey"] is { Length: > 0 } r ? r : null,
        };

        await sender.SendMessageAsync(new ServiceBusMessage(JsonConvert.SerializeObject(dispatch))
        {
            MessageId = idempotencyId,
            CorrelationId = requestId,
            ContentType = "application/json",
        }, ct);

        await MarkEnqueuedAsync(idempotencyId, ct);

        log.LogInformation("Accepted {MessageId} for recipient {RecipientLogId} mode {Mode} test {IsTestSend} request {RequestId} reEnqueue {ReEnqueue}",
            dispatch.MessageId, Keys.LogId(recipientKey), mode, dispatch.IsTestSend, requestId, reEnqueue);
        return new ObjectResult(new { messageId = dispatch.MessageId }) { StatusCode = 202 };
    }

    /// <summary>Pending -> Enqueued, only if the Dispatcher hasn't already moved the record on. Best effort: the message is already queued.</summary>
    private async Task MarkEnqueuedAsync(string idempotencyId, CancellationToken ct)
    {
        try
        {
            await store.Dispatches.PatchItemAsync<DispatchRecord>(idempotencyId, new PartitionKey(idempotencyId),
                new[] { PatchOperation.Set("/status", DispatchStatus.Enqueued), PatchOperation.Set("/updatedOn", DateTimeOffset.UtcNow) },
                new PatchItemRequestOptions { FilterPredicate = $"FROM c WHERE c.status = '{DispatchStatus.Pending}'" }, ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Already Delivered / NotDelivered.
        }
        catch (CosmosException ex)
        {
            log.LogWarning("Could not mark {IdempotencyId} Enqueued: {Status}. A CIJ retry would enqueue it again; the Dispatcher de-duplicates.", idempotencyId, ex.StatusCode);
        }
    }

    // The option value arrives as a string ("1"), observed in a CIJ test send; labels are accepted defensively.
    internal static bool TryParseMode(string? value, out DeliveryMode mode)
    {
        mode = DeliveryMode.AlertAndInbox;
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "" or "1" or "alert + inbox": mode = DeliveryMode.AlertAndInbox; return true;
            case "2" or "inbox only": mode = DeliveryMode.InboxOnly; return true;
            case "3" or "alert only": mode = DeliveryMode.AlertOnly; return true;
            default: return false;
        }
    }

    internal static int ExpiryDays(string? fromMessage, string? channelDefault)
    {
        var days = int.TryParse(fromMessage, out var m) && m > 0 ? m
            : int.TryParse(channelDefault, out var d) && d > 0 ? d
            : DefaultExpiryDays;
        return Math.Clamp(days, 1, MaxExpiryDays);
    }

    private static string? Https(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;

    private static ObjectResult Problem(HttpStatusCode status, string reason, string detail) =>
        new(new { reason, detail }) { StatusCode = (int)status };
}
