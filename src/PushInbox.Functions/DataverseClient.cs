using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Newtonsoft.Json;

namespace PushInbox.Functions;

/// <summary>
/// Calls the solution's delivery-report custom API (alex_PushDeliveryReport) with the Function App's managed identity,
/// which is registered as a Dataverse application user. The plug-in relays to msdyn_D365ChannelsNotification:
/// https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api
/// </summary>
public sealed class DataverseClient
{
    private readonly HttpClient _http;
    private readonly string? _url;
    private readonly TokenCredential _credential = new DefaultAzureCredential();

    public DataverseClient(HttpClient http, string? url)
    {
        _http = http;
        _url = string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
    }

    public bool IsConfigured => _url != null;

    public async Task<string> SendDeliveryReportAsync(DispatchMessage message, bool delivered, string? reason, string? detail, CancellationToken ct)
    {
        var report = new
        {
            ChannelDefinitionId = message.ChannelDefinitionId,
            RequestId = message.RequestId,
            MessageId = message.MessageId,
            From = message.From,
            Status = delivered ? "Delivered" : "NotDelivered",
            Details = delivered ? null : new { Reason = reason, Message = detail },
        };
        var body = JsonConvert.SerializeObject(new { payload = JsonConvert.SerializeObject(report) });
        var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { _url + "/.default" }), ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, _url + "/api/data/v9.2/alex_PushDeliveryReport")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Delivery report failed: {(int)response.StatusCode} {text}");
        }
        return text;
    }

    /// <summary>
    /// Upserts one alex_pushmessagelog row (elastic table) by primary key + partitionid (= message ID).
    /// https://learn.microsoft.com/power-apps/developer/data-platform/use-elastic-tables
    /// </summary>
    public async Task UpsertLogAsync(LogEvent e, Guid? contactId, int ttlSeconds, CancellationToken ct)
    {
        var row = new Dictionary<string, object?>
        {
            ["alex_name"] = $"{Label(e.Type)} – {e.MessageId[..Math.Min(8, e.MessageId.Length)]}",
            ["alex_messageid"] = e.MessageId,
            ["alex_requestid"] = Cut(e.RequestId, 100),
            ["alex_idempotencyid"] = Cut(e.IdempotencyId, 100),
            ["alex_eventtype"] = (int)e.Type,
            ["alex_recipientlogid"] = Keys.LogId(e.RecipientKey),
            ["alex_appkey"] = Cut(e.AppKey, 100),
            ["alex_istestsend"] = e.IsTestSend,
            ["alex_detail"] = Cut(e.Detail, 500),
            ["alex_eventon"] = e.EventOn.UtcDateTime,
            ["alex_customerjourneyid"] = Cut(e.CustomerJourneyId, 100),
            ["alex_channeldefinitionid"] = e.ChannelDefinitionId?.ToString(),
            ["ttlinseconds"] = ttlSeconds,
        };
        if (e.Mode is { } mode) row["alex_deliverymode"] = 626209999 + (int)mode;
        if (e.PnsSuccess is { } ok) row["alex_pnssuccesscount"] = ok;
        if (e.PnsFailure is { } failed) row["alex_pnsfailurecount"] = failed;
        if (contactId is { } c) row["alex_ContactId@odata.bind"] = $"/contacts({c})";

        var key = $"alex_pushmessagelogid={e.EventId},partitionid='{Uri.EscapeDataString(e.MessageId)}'";
        var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { _url + "/.default" }), ct);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{_url}/api/data/v9.2/alex_pushmessagelogs({key})")
        {
            Content = new StringContent(JsonConvert.SerializeObject(row), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new DataverseException((int)response.StatusCode, $"Message log upsert failed: {(int)response.StatusCode} {(text.Length > 400 ? text[..400] : text)}");
        }
    }

    private static string Label(LogEventType type) => type == LogEventType.NotDelivered ? "Not delivered" : type.ToString();

    private static string? Cut(string? value, int max) => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}

public sealed class DataverseException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
