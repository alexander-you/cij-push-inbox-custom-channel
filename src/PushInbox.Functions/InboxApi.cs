using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace PushInbox.Functions;

/// <summary>
/// Inbox API used by the reference PWA: Web Push device registration and inbox list / read / delete.
/// TEST HARNESS ONLY: the caller identifies itself with appKey + recipientKey and no sign-in.
/// Production apps must authenticate the end user and derive recipientKey from a verified token (see docs/known-limitations.md).
/// </summary>
public sealed class InboxApi(Store store, NotificationHubRest hub, ILogger<InboxApi> log)
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
        if (r == null || appKey.Length == 0 || recipientKey.Length == 0 || string.IsNullOrEmpty(r.InstallationId)
            || !Uri.TryCreate(r.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(r.P256dh) || string.IsNullOrEmpty(r.Auth))
        {
            return new BadRequestObjectResult(new { reason = "InvalidRegistration" });
        }

        // Tags are set here, never by the client.
        var tags = new[] { Keys.RecipientTag(recipientKey), $"app:{appKey}" };
        await hub.UpsertBrowserInstallationAsync(Keys.Recipient(r.InstallationId), r.Endpoint!, r.P256dh!, r.Auth!, tags, ct);
        log.LogInformation("Browser installation {InstallationLogId} registered for recipient {RecipientLogId} app {AppKey}", Keys.LogId(r.InstallationId), Keys.LogId(recipientKey), appKey);
        return new OkObjectResult(new { recipientKey, appKey, tags });
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
    public async Task<IActionResult> MarkRead(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "inbox/{id}/read")] HttpRequest req, string id, CancellationToken ct)
    {
        var (appKey, recipientKey) = Identity(req);
        try
        {
            var item = await store.Inbox.ReadItemAsync<InboxItem>(id, new PartitionKey(recipientKey), cancellationToken: ct);
            if (item.Resource.AppKey != appKey) return new NotFoundResult();
            await store.Inbox.PatchItemAsync<InboxItem>(id, new PartitionKey(recipientKey), new[] { PatchOperation.Set("/readOn", DateTimeOffset.UtcNow) }, cancellationToken: ct);
            return new NoContentResult();
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundResult();
        }
    }

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

    private sealed class DeviceRegistration
    {
        public string? AppKey { get; set; }
        public string? RecipientKey { get; set; }
        public string? InstallationId { get; set; }
        public string? Endpoint { get; set; }
        public string? P256dh { get; set; }
        public string? Auth { get; set; }
    }
}
