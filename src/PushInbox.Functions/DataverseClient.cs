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
}
