using Newtonsoft.Json;

namespace PushInbox.Functions;

/// <summary>What every platform payload carries, so any client can show the alert and open the inbox item.</summary>
public sealed record PushContent(string Title, string Body, string? ImageUrl, string? ActionUrl, string MessageId, string InboxItemId, string Dir);

/// <summary>
/// One Notification Hubs native format. The hub routes a native send only to installations of the same platform,
/// so the Dispatcher sends once per platform with the same tag expression.
/// Formats and headers: https://learn.microsoft.com/rest/api/notificationhubs/send-apns-native-notification,
/// https://learn.microsoft.com/azure/notification-hubs/firebase-migration-rest,
/// https://learn.microsoft.com/azure/notification-hubs/push-notification-updates-ios-13 (apns-push-type).
/// </summary>
public sealed class PushPlatform
{
    public const string Browser = "browser";
    public const string FcmV1 = "fcmv1";
    public const string Apns = "apns";

    // Native alert payloads are limited to 4 KB by FCM and APNs; long CIJ texts are shortened for the alert only (the inbox item keeps the full text).
    public const int NativeTitleMax = 200;
    public const int NativeBodyMax = 1000;

    public string Name { get; }
    /// <summary>ServiceBusNotification-Format header value.</summary>
    public string Format { get; }
    public string? ApiVersion { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    private readonly Func<PushContent, string> _build;

    private PushPlatform(string name, string format, string? apiVersion, IReadOnlyDictionary<string, string> headers, Func<PushContent, string> build) =>
        (Name, Format, ApiVersion, Headers, _build) = (name, format, apiVersion, headers, build);

    public string BuildPayload(PushContent c) => _build(c);

    public static readonly PushPlatform BrowserPlatform = new(Browser, "browser", null, new Dictionary<string, string>(), BrowserPayload);
    // FCM v1 sends use the api-version documented in the FCM v1 migration guide.
    public static readonly PushPlatform FcmV1Platform = new(FcmV1, "fcmV1", "2023-10-01-preview", new Dictionary<string, string>(), FcmV1Payload);
    public static readonly PushPlatform ApnsPlatform = new(Apns, "apple", null,
        new Dictionary<string, string> { ["apns-push-type"] = "alert", ["apns-priority"] = "10" }, ApnsPayload);

    public static readonly IReadOnlyList<PushPlatform> All = [BrowserPlatform, FcmV1Platform, ApnsPlatform];

    /// <summary>Parses the PUSH_PLATFORMS app setting (comma-separated). Empty or missing means all platforms.</summary>
    public static IReadOnlyList<PushPlatform> Parse(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return All;
        var names = setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant()).ToHashSet();
        var unknown = names.Except(All.Select(p => p.Name)).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"PUSH_PLATFORMS has unknown values: {string.Join(", ", unknown)}. Use browser, fcmv1, apns.");
        return All.Where(p => names.Contains(p.Name)).ToList();
    }

    public static string BrowserPayload(PushContent c) => JsonConvert.SerializeObject(new
    {
        title = c.Title,
        body = c.Body,
        image = c.ImageUrl,
        url = c.ActionUrl,
        messageId = c.MessageId,
        inboxItemId = c.InboxItemId,
        dir = c.Dir,
        lang = c.Dir == "rtl" ? "he" : "en",
    });

    /// <summary>FCM HTTP v1 message: https://firebase.google.com/docs/reference/fcm/rest/v1/projects.messages ("data" is string-to-string only).</summary>
    public static string FcmV1Payload(PushContent c)
    {
        var notification = new Dictionary<string, string> { ["title"] = Cut(c.Title, NativeTitleMax), ["body"] = Cut(c.Body, NativeBodyMax) };
        if (!string.IsNullOrEmpty(c.ImageUrl)) notification["image"] = c.ImageUrl;
        return JsonConvert.SerializeObject(new { message = new { notification, data = Data(c) } });
    }

    /// <summary>
    /// APNs alert: https://developer.apple.com/documentation/usernotifications/generating-a-remote-notification.
    /// Custom keys sit beside "aps"; mutable-content lets a Notification Service Extension attach the image.
    /// </summary>
    public static string ApnsPayload(PushContent c)
    {
        var aps = new Dictionary<string, object> { ["alert"] = new { title = Cut(c.Title, NativeTitleMax), body = Cut(c.Body, NativeBodyMax) } };
        if (!string.IsNullOrEmpty(c.ImageUrl)) aps["mutable-content"] = 1;
        var root = new Dictionary<string, object> { ["aps"] = aps };
        foreach (var (k, v) in Data(c)) root[k] = v;
        return JsonConvert.SerializeObject(root);
    }

    private static Dictionary<string, string> Data(PushContent c)
    {
        var data = new Dictionary<string, string> { ["messageId"] = c.MessageId, ["inboxItemId"] = c.InboxItemId, ["dir"] = c.Dir };
        if (!string.IsNullOrEmpty(c.ActionUrl)) data["url"] = c.ActionUrl;
        if (!string.IsNullOrEmpty(c.ImageUrl)) data["image"] = c.ImageUrl;
        return data;
    }

    private static string Cut(string? text, int max) => text == null ? "" : text.Length <= max ? text : text[..(max - 1)] + "…";
}

public static class PlatformStatus
{
    public const string Sent = "Sent";
    public const string NoDevice = "NoDevice";
    /// <summary>The hub has no credentials for this platform (Notification Hubs returns 400 "no target applications").</summary>
    public const string NotConfigured = "NotConfigured";
    public const string Error = "Error";
}

/// <summary>Result of one platform send, stored on the dispatch record so a redelivery never pushes that platform again.</summary>
public sealed class PlatformResult
{
    [JsonProperty("status")] public string Status { get; set; } = "";
    [JsonProperty("pushed")] public int Pushed { get; set; }
    [JsonProperty("success")] public int? Success { get; set; }
    [JsonProperty("failure")] public int? Failure { get; set; }
    [JsonProperty("httpStatus")] public int HttpStatus { get; set; }
    [JsonProperty("detail")] public string? Detail { get; set; }
    [JsonProperty("notificationId")] public string? NotificationId { get; set; }
}

public static class PushOutcome
{
    /// <summary>True when Service Bus should redeliver (nothing was pushed on this platform).</summary>
    public static bool IsTransient(SendOutcome o) => o.HttpStatus >= 500 || o.HttpStatus == 429;

    public static PlatformResult Classify(SendOutcome o, bool test)
    {
        var r = new PlatformResult { HttpStatus = o.HttpStatus, NotificationId = o.NotificationId };
        if (o.HttpStatus == 400 && o.Raw.Contains("no target applications", StringComparison.OrdinalIgnoreCase))
        {
            r.Status = PlatformStatus.NotConfigured;
        }
        else if (o.HttpStatus is < 200 or >= 300)
        {
            r.Status = PlatformStatus.Error;
            r.Detail = $"Notification Hubs returned {o.HttpStatus}: {(o.Raw.Length > 300 ? o.Raw[..300] : o.Raw)}";
        }
        else if (test)
        {
            (r.Success, r.Failure, r.Pushed) = (o.Success, o.Failure, o.Success);
            r.Status = o.Success > 0 ? PlatformStatus.Sent : PlatformStatus.NoDevice;
        }
        else
        {
            // Standard sends: the hub accepted the request; per-device results need the Standard tier.
            (r.Pushed, r.Status) = (1, PlatformStatus.Sent);
        }
        return r;
    }

    /// <summary>Combines per-platform results into the dispatch outcome: pushed count, failure reason and detail.</summary>
    public static (int Pushed, string? Reason, string? Detail) Aggregate(IReadOnlyDictionary<string, PlatformResult> results)
    {
        var pushed = results.Values.Sum(r => r.Pushed);
        var errors = results.Where(r => r.Value.Status == PlatformStatus.Error).Select(r => $"{r.Key}: {r.Value.Detail}").ToList();
        if (pushed > 0) return (pushed, null, errors.Count > 0 ? string.Join(" | ", errors) : null);
        if (errors.Count > 0) return (0, "PushServiceError", string.Join(" | ", errors));
        if (results.Count > 0 && results.Values.All(r => r.Status == PlatformStatus.NotConfigured))
            return (0, "NoPushPlatform", "No push platform is configured on the notification hub.");
        return (0, "NoDevice", "No registered device accepted the notification for this recipient.");
    }

    public static string Summary(IReadOnlyDictionary<string, PlatformResult> results) =>
        string.Join(" ", results.Select(r => $"{r.Key}:{r.Value.Status}" + (r.Value.Success is { } s ? $"({s}/{s + (r.Value.Failure ?? 0)})" : "")));
}
