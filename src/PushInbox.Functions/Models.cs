using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace PushInbox.Functions;

/// <summary>Queue message from the Channel API to the Dispatcher.</summary>
public sealed class DispatchMessage
{
    public string MessageId { get; set; } = "";
    public string IdempotencyId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public Guid ChannelDefinitionId { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string AppKey { get; set; } = "";
    public string RecipientKey { get; set; } = "";
    public string? CustomerJourneyId { get; set; }
    public bool IsTestSend { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? ImageUrl { get; set; }
    public string? ActionUrl { get; set; }
    public DeliveryMode Mode { get; set; }
    public int ExpiryDays { get; set; }
    public string? ReplaceKey { get; set; }
}

public enum DeliveryMode { AlertAndInbox = 1, InboxOnly = 2, AlertOnly = 3 }

/// <summary>Cosmos container "dispatches" (partition key /idempotencyId).</summary>
public sealed class DispatchRecord
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("idempotencyId")] public string IdempotencyId { get; set; } = "";
    [JsonProperty("messageId")] public string MessageId { get; set; } = "";
    [JsonProperty("requestId")] public string RequestId { get; set; } = "";
    [JsonProperty("recipientKey")] public string RecipientKey { get; set; } = "";
    [JsonProperty("appKey")] public string AppKey { get; set; } = "";
    [JsonProperty("isTestSend")] public bool IsTestSend { get; set; }
    /// <summary>Pending (created, enqueue not confirmed), Enqueued, then Delivered / NotDelivered. Older records may say Accepted.</summary>
    [JsonProperty("status")] public string Status { get; set; } = DispatchStatus.Pending;
    [JsonProperty("statusDetail")] public string? StatusDetail { get; set; }
    // Step markers written by the Dispatcher so a Service Bus redelivery repeats only the steps that didn't finish.
    [JsonProperty("storedOn")] public DateTimeOffset? StoredOn { get; set; }
    [JsonProperty("pushedOn")] public DateTimeOffset? PushedOn { get; set; }
    [JsonProperty("pushedCount")] public int PushedCount { get; set; }
    [JsonProperty("failureReason")] public string? FailureReason { get; set; }
    [JsonProperty("reportedOn")] public DateTimeOffset? ReportedOn { get; set; }
    [JsonProperty("reportError")] public string? ReportError { get; set; }
    [JsonProperty("createdOn")] public DateTimeOffset CreatedOn { get; set; }
    [JsonProperty("updatedOn")] public DateTimeOffset? UpdatedOn { get; set; }
}

public static class DispatchStatus
{
    public const string Pending = "Pending";
    public const string Enqueued = "Enqueued";
}

/// <summary>Cosmos container "inboxItems" (partition key /recipientKey).</summary>
public sealed class InboxItem
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("recipientKey")] public string RecipientKey { get; set; } = "";
    [JsonProperty("appKey")] public string AppKey { get; set; } = "";
    [JsonProperty("messageId")] public string MessageId { get; set; } = "";
    [JsonProperty("requestId")] public string RequestId { get; set; } = "";
    [JsonProperty("channelDefinitionId")] public Guid ChannelDefinitionId { get; set; }
    [JsonProperty("customerJourneyId")] public string? CustomerJourneyId { get; set; }
    /// <summary>Null on items stored before the message log existed.</summary>
    [JsonProperty("isTestSend")] public bool? IsTestSend { get; set; }
    [JsonProperty("title")] public string Title { get; set; } = "";
    [JsonProperty("body")] public string Body { get; set; } = "";
    [JsonProperty("imageUrl")] public string? ImageUrl { get; set; }
    [JsonProperty("actionUrl")] public string? ActionUrl { get; set; }
    [JsonProperty("replaceKey")] public string? ReplaceKey { get; set; }
    [JsonProperty("dir")] public string Dir { get; set; } = "ltr";
    [JsonProperty("createdOn")] public DateTimeOffset CreatedOn { get; set; }
    [JsonProperty("expiresOn")] public DateTimeOffset ExpiresOn { get; set; }
    [JsonProperty("readOn")] public DateTimeOffset? ReadOn { get; set; }
    [JsonProperty("ttl")] public int Ttl { get; set; }
}

/// <summary>Option values of alex_pushmessagelog.alex_eventtype (publisher option prefix 62621).</summary>
public enum LogEventType
{
    Accepted = 626210000,
    Rejected = 626210001,
    Dispatched = 626210002,
    Delivered = 626210003,
    NotDelivered = 626210004,
    Read = 626210005,
    Clicked = 626210006,
}

/// <summary>Queue message for the message-log queue. One event becomes one alex_pushmessagelog row.</summary>
public sealed class LogEvent
{
    /// <summary>Row primary key. Deterministic for once-per-message events, so a repeated event updates the same row.</summary>
    public Guid EventId { get; set; }
    public LogEventType Type { get; set; }
    public string MessageId { get; set; } = "";
    public string? RequestId { get; set; }
    public string? IdempotencyId { get; set; }
    public string? RecipientKey { get; set; }
    public string? AppKey { get; set; }
    public bool IsTestSend { get; set; }
    public DeliveryMode? Mode { get; set; }
    public int? PnsSuccess { get; set; }
    public int? PnsFailure { get; set; }
    public string? Detail { get; set; }
    public string? CustomerJourneyId { get; set; }
    public Guid? ChannelDefinitionId { get; set; }
    public DateTimeOffset EventOn { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Name-based GUID (SHA-256 of message ID and event type) for events that happen once per message.</summary>
    public static Guid OnceId(string messageId, LogEventType type)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{messageId}|{(int)type}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    public static LogEvent For(DispatchMessage m, LogEventType type) => new()
    {
        EventId = OnceId(m.MessageId, type),
        Type = type,
        MessageId = m.MessageId,
        RequestId = m.RequestId,
        IdempotencyId = m.IdempotencyId,
        RecipientKey = m.RecipientKey,
        AppKey = m.AppKey,
        IsTestSend = m.IsTestSend,
        Mode = m.Mode,
        CustomerJourneyId = m.CustomerJourneyId,
        ChannelDefinitionId = m.ChannelDefinitionId == Guid.Empty ? null : m.ChannelDefinitionId,
    };
}

public static class Keys
{
    private static readonly Regex Disallowed = new("[^a-z0-9_@#.:-]", RegexOptions.Compiled);
    private static readonly Regex Rtl = new("[\u0590-\u05FF\u0600-\u06FF]", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a recipient key to the Notification Hubs tag alphabet (alphanumeric and _ @ # . : -).
    /// The same function runs at device registration and at send, so "+972 54123" and "97254123" match.
    /// Hyphens are kept, so senders and apps must use the same phone format.
    /// </summary>
    public static string Recipient(string? value)
    {
        var key = Disallowed.Replace((value ?? "").Trim().ToLowerInvariant(), "");
        return key.Length > 100 ? key[..100] : key;
    }

    public static string App(string? value) => Recipient(value);

    public static string InboxItemId(string appKey, string? replaceKey, string messageId) =>
        string.IsNullOrWhiteSpace(replaceKey)
            ? messageId
            : "r-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appKey + "|" + replaceKey))).ToLowerInvariant()[..40];

    /// <summary>Pseudonymous id for logs: the first 12 hex characters of SHA-256, so recipient keys never appear in telemetry.</summary>
    public static string LogId(string? value) =>
        string.IsNullOrEmpty(value) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12];

    /// <summary>Notification Hubs recipient tag. Hubs don't encrypt tags at rest, so the recipient key is stored as a SHA-256 hash.</summary>
    public static string RecipientTag(string recipientKey) =>
        "rk:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipientKey))).ToLowerInvariant();

    /// <summary>"rtl" when the text contains Hebrew or Arabic letters, otherwise "ltr".</summary>
    public static string Direction(params string?[] texts) => texts.Any(t => t != null && Rtl.IsMatch(t)) ? "rtl" : "ltr";
}
