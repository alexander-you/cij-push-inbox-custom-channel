using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace PushInbox.Functions;

/// <summary>
/// Puts message-log events on their own Service Bus queue. Best effort: a failure is logged and swallowed,
/// so the log can never fail or delay the CIJ send, the push or the inbox API.
/// Disabled when MESSAGE_LOG_QUEUE is not set.
/// </summary>
public sealed class MessageLog(ServiceBusSender? sender, ILogger<MessageLog> log)
{
    public async Task EmitAsync(LogEvent e)
    {
        if (sender == null || string.IsNullOrEmpty(e.MessageId)) return;
        try
        {
            await sender.SendMessageAsync(new ServiceBusMessage(JsonConvert.SerializeObject(e))
            {
                MessageId = e.EventId.ToString(),
                ContentType = "application/json",
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning("Message log event {Type} for {MessageId} not queued: {Error}", e.Type, e.MessageId, ex.Message);
        }
    }
}

/// <summary>
/// Service Bus "messagelog" queue trigger: writes one alex_pushmessagelog row per event (Upsert by primary key + partitionid).
/// Failures are retried by Service Bus and dead-lettered after maxDeliveryCount; the send path is never involved.
/// </summary>
public sealed class LogWriter(DataverseClient dataverse, ILogger<LogWriter> log)
{
    private static readonly int TtlDays = Days("MESSAGE_LOG_TTL_DAYS", 180);
    private static readonly int TestTtlDays = Days("MESSAGE_LOG_TEST_TTL_DAYS", 7);

    [Function("LogWriter")]
    public async Task Run([ServiceBusTrigger("%MESSAGE_LOG_QUEUE%", Connection = "ServiceBus")] string body, CancellationToken ct)
    {
        var e = JsonConvert.DeserializeObject<LogEvent>(body)!;
        if (!dataverse.IsConfigured)
        {
            log.LogWarning("Message log event {Type} for {MessageId} dropped: DATAVERSE_URL is not set.", e.Type, e.MessageId);
            return;
        }
        var ttl = (e.IsTestSend || string.IsNullOrEmpty(e.RequestId) ? TestTtlDays : TtlDays) * 86400;
        var contactId = ContactId(e.RecipientKey);
        try
        {
            await dataverse.UpsertLogAsync(e, contactId, ttl, ct);
        }
        catch (DataverseException ex) when (contactId != null && ex.Status is 400 or 403 or 404)
        {
            // The contact may be deleted or not linkable; keep the event without the lookup.
            log.LogWarning("Message log row {EventId}: contact link failed ({Status}); writing without contact", e.EventId, ex.Status);
            await dataverse.UpsertLogAsync(e, null, ttl, ct);
        }
        log.LogInformation("Message log row {EventId} {Type} written for {MessageId}", e.EventId, e.Type, e.MessageId);
    }

    /// <summary>contact_&lt;guid&gt; recipient keys (journey sends) identify the contact; anything else has no contact.</summary>
    internal static Guid? ContactId(string? recipientKey) =>
        recipientKey != null && recipientKey.StartsWith("contact_", StringComparison.Ordinal) && Guid.TryParse(recipientKey[8..], out var id) ? id : null;

    private static int Days(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var d) && d > 0 ? d : fallback;
}
