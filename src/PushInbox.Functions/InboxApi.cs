using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace PushInbox.Functions;

/// <summary>
/// Inbox API used by the reference PWA and native apps: device registration (browser Web Push, FCM v1, APNs) and inbox list / read / click / delete.
/// TEST HARNESS ONLY: the caller identifies itself with appKey + recipientKey and no sign-in.
/// Production apps must authenticate the end user and derive recipientKey from a verified token (see docs/known-limitations.md).
/// </summary>
public sealed class InboxApi(Store store, NotificationHubRest hub, MessageLog messageLog, ILogger<InboxApi> log)
{
    [Function("Config")]
    public IActionResult Config([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config")] HttpRequest req) =>
        new OkObjectResult(new { vapidPublicKey = Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY") });

    [Function("RegisterDevice")]
    public async Task<IActionResult> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "devices")] HttpRequest req, CancellationToken ct)
    {
        var r = JsonConvert.DeserializeObject<DeviceRegistration>(await new StreamReader(req.Body).ReadToEndAsync(ct));
        var appKey = Keys.App(r?.AppKey);
        var recipientKey = Keys.Recipient(r?.RecipientKey);
        var platform = DeviceRegistration.Validate(r);
        if (r == null || platform == null || appKey.Length == 0 || recipientKey.Length == 0)
        {
            return new BadRequestObjectResult(new { reason = "InvalidRegistration" });
        }

        // Tags are set here, never by the client.
        var tags = new[] { Keys.RecipientTag(recipientKey), $"app:{appKey}" };
        var installationId = Keys.Recipient(r.InstallationId);
        if (platform == PushPlatform.Browser)
            await hub.UpsertBrowserInstallationAsync(installationId, r.Endpoint!, r.P256dh!, r.Auth!, tags, ct);
        else
            await hub.UpsertNativeInstallationAsync(installationId, platform, r.PushToken!.Trim(), tags, ct);
        log.LogInformation("{Platform} installation {InstallationLogId} registered for recipient {RecipientLogId} app {AppKey}", platform, Keys.LogId(r.InstallationId), Keys.LogId(recipientKey), appKey);
        return new OkObjectResult(new { recipientKey, appKey, platform, tags });
    }

    [Function("ListInbox")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inbox")] HttpRequest req, CancellationToken ct)
    {
        var (appKey, recipientKey) = Identity(req);
        if (recipientKey.Length == 0) return new BadRequestObjectResult(new { reason = "MissingRecipient" });

        var query = new QueryDefinition("SELECT * FROM c WHERE c.appKey = @app ORDER BY c.createdOn DESC").WithParameter("@app", appKey);
        var items = new List<InboxItem>();
        using var it = store.Inbox.GetItemQueryIterator<InboxItem>(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(recipientKey), MaxItemCount = 100 });
        while (it.HasMoreResults && items.Count < 100) items.AddRange(await it.ReadNextAsync(ct));
        return new OkObjectResult(new { recipientKey, unread = items.Count(i => i.ReadOn == null), items });
    }

    [Function("MarkRead")]
    public Task<IActionResult> MarkRead(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "inbox/{id}/read")] HttpRequest req, string id, CancellationToken ct) =>
        Interact(req, id, clicked: false, ct);

    /// <summary>The app was opened from this message's tracked link (msdynmkt_trackingcontext = RequestId). Logs Clicked and marks the item read.</summary>
    [Function("ReportClick")]
    public Task<IActionResult> ReportClick(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "inbox/{id}/click")] HttpRequest req, string id, CancellationToken ct) =>
        Interact(req, id, clicked: true, ct);

    private async Task<IActionResult> Interact(HttpRequest req, string id, bool clicked, CancellationToken ct)
    {
        var (appKey, recipientKey) = Identity(req);
        try
        {
            var item = (await store.Inbox.ReadItemAsync<InboxItem>(id, new PartitionKey(recipientKey), cancellationToken: ct)).Resource;
            if (item.AppKey != appKey) return new NotFoundResult();
            if (clicked) await messageLog.EmitAsync(Event(item, LogEventType.Clicked, Guid.NewGuid()));
            if (item.ReadOn == null)
            {
                await store.Inbox.PatchItemAsync<InboxItem>(id, new PartitionKey(recipientKey), new[] { PatchOperation.Set("/readOn", DateTimeOffset.UtcNow) }, cancellationToken: ct);
                await messageLog.EmitAsync(Event(item, LogEventType.Read, LogEvent.OnceId(item.MessageId, LogEventType.Read)));
            }
            return new NoContentResult();
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundResult();
        }
    }

    private static LogEvent Event(InboxItem item, LogEventType type, Guid eventId) => new()
    {
        EventId = eventId,
        Type = type,
        MessageId = item.MessageId,
        RequestId = item.RequestId,
        RecipientKey = item.RecipientKey,
        AppKey = item.AppKey,
        IsTestSend = item.IsTestSend ?? string.IsNullOrEmpty(item.RequestId),
        CustomerJourneyId = item.CustomerJourneyId,
        ChannelDefinitionId = item.ChannelDefinitionId == Guid.Empty ? null : item.ChannelDefinitionId,
    };

    [Function("DeleteInboxItem")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "inbox/{id}")] HttpRequest req, string id, CancellationToken ct)
    {
        var (appKey, recipientKey) = Identity(req);
        try
        {
            var item = await store.Inbox.ReadItemAsync<InboxItem>(id, new PartitionKey(recipientKey), cancellationToken: ct);
            if (item.Resource.AppKey != appKey) return new NotFoundResult();
            await store.Inbox.DeleteItemAsync<InboxItem>(id, new PartitionKey(recipientKey), cancellationToken: ct);
            return new NoContentResult();
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundResult();
        }
    }

    private static (string appKey, string recipientKey) Identity(HttpRequest req) =>
        (Keys.App(req.Query["appKey"]), Keys.Recipient(req.Query["recipientKey"]));

    public sealed class DeviceRegistration
    {
        /// <summary>browser (default), fcmv1 or apns.</summary>
        public string? Platform { get; set; }
        public string? AppKey { get; set; }
        public string? RecipientKey { get; set; }
        public string? InstallationId { get; set; }
        // Browser: the PushSubscription fields.
        public string? Endpoint { get; set; }
        public string? P256dh { get; set; }
        public string? Auth { get; set; }
        /// <summary>Native: the FCM registration token (fcmv1) or the APNs device token as hex (apns).</summary>
        public string? PushToken { get; set; }

        private static readonly System.Text.RegularExpressions.Regex ApnsToken = new("^[0-9a-fA-F]{64,200}$");

        /// <summary>Returns the normalised platform name, or null when the registration is invalid. Doesn't check appKey / recipientKey.</summary>
        public static string? Validate(DeviceRegistration? r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.InstallationId) || Keys.Recipient(r.InstallationId).Length == 0) return null;
            var platform = string.IsNullOrWhiteSpace(r.Platform) ? PushPlatform.Browser : r.Platform.Trim().ToLowerInvariant();
            var token = r.PushToken?.Trim() ?? "";
            return platform switch
            {
                PushPlatform.Browser when Uri.TryCreate(r.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps
                    && !string.IsNullOrEmpty(r.P256dh) && !string.IsNullOrEmpty(r.Auth) => platform,
                PushPlatform.FcmV1 when token.Length is > 0 and <= 4096 && !token.Any(char.IsWhiteSpace) => platform,
                PushPlatform.Apns when ApnsToken.IsMatch(token) => platform,
                _ => null,
            };
        }
    }
}
