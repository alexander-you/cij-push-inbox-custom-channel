using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace PushInbox.Functions;

/// <summary>
/// Minimal Azure Notification Hubs REST client for installations and native sends (browser Web Push, FCM v1, APNs).
/// The .NET SDK (Microsoft.Azure.NotificationHubs 4.2.0) has no browser types, so this follows the REST contract:
/// https://learn.microsoft.com/azure/notification-hubs/browser-push,
/// https://learn.microsoft.com/rest/api/notificationhubs/create-overwrite-installation and
/// https://learn.microsoft.com/rest/api/notificationhubs/common-concepts (SAS authentication).
/// </summary>
public sealed class NotificationHubRest
{
    private const string ApiVersion = "2020-06";
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _keyName;
    private readonly string _key;

    public NotificationHubRest(HttpClient http, string connectionString, string hubName)
    {
        _http = http;
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        var endpoint = parts["Endpoint"].Replace("sb://", "https://").TrimEnd('/');
        _baseUrl = $"{endpoint}/{hubName}";
        _keyName = parts["SharedAccessKeyName"];
        _key = parts["SharedAccessKey"];
    }

    public async Task UpsertBrowserInstallationAsync(string installationId, string endpoint, string p256dh, string auth, IEnumerable<string> tags, CancellationToken ct)
    {
        var body = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            installationId,
            platform = "browser",
            pushChannel = new { endpoint, p256dh, auth },
            tags,
        });
        using var request = Create(HttpMethod.Put, $"installations/{Uri.EscapeDataString(installationId)}", body);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Notification Hubs installation failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    /// <summary>FCM v1 (platform "fcmv1", pushChannel = FCM registration token) or APNs (platform "apns", pushChannel = device token).</summary>
    public async Task UpsertNativeInstallationAsync(string installationId, string platform, string pushChannel, IEnumerable<string> tags, CancellationToken ct)
    {
        var body = Newtonsoft.Json.JsonConvert.SerializeObject(new { installationId, platform, pushChannel, tags });
        using var request = Create(HttpMethod.Put, $"installations/{Uri.EscapeDataString(installationId)}", body);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Notification Hubs installation failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    public async Task DeleteInstallationAsync(string installationId, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Delete, $"installations/{Uri.EscapeDataString(installationId)}", null);
        using var response = await _http.SendAsync(request, ct);
    }

    /// <summary>
    /// Sends a browser notification to a tag expression. With <paramref name="test"/> = true the hub performs a
    /// debug send (at most 10 devices) and returns per-device outcomes, which we use for test sends.
    /// </summary>
    public Task<SendOutcome> SendBrowserAsync(string payloadJson, string tagExpression, bool test, CancellationToken ct) =>
        SendAsync(PushPlatform.BrowserPlatform.Format, payloadJson, tagExpression, test, null, null, ct);

    /// <summary>Native send in one <paramref name="format"/> (browser, fcmV1, apple) to a tag expression.</summary>
    public async Task<SendOutcome> SendAsync(string format, string payloadJson, string tagExpression, bool test,
        IReadOnlyDictionary<string, string>? headers, string? apiVersion, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, "messages/" + (test ? "?test" : ""), payloadJson, apiVersion);
        request.Headers.Add("ServiceBusNotification-Format", format);
        request.Headers.Add("ServiceBusNotification-Tags", tagExpression);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var outcome = new SendOutcome { HttpStatus = (int)response.StatusCode, Raw = text };
        if (response.Headers.Location != null) outcome.NotificationId = response.Headers.Location.Segments.LastOrDefault()?.Split('?')[0];
        if (test && response.IsSuccessStatusCode && text.TrimStart().StartsWith('<'))
        {
            var xml = XDocument.Parse(text);
            XNamespace ns = xml.Root!.Name.Namespace;
            outcome.Success = (int?)xml.Root.Element(ns + "Success") ?? 0;
            outcome.Failure = (int?)xml.Root.Element(ns + "Failure") ?? 0;
            outcome.Results = xml.Descendants(ns + "RegistrationResult")
                .Select(r => (string?)r.Element(ns + "Outcome") ?? "").ToList();
        }
        return outcome;
    }

    private HttpRequestMessage Create(HttpMethod method, string path, string? json, string? apiVersion = null)
    {
        var separator = path.Contains('?') ? "&" : "?";
        var url = $"{_baseUrl}/{path}{separator}api-version={apiVersion ?? ApiVersion}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", SasToken(_baseUrl));
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private string SasToken(string resourceUri)
    {
        var encoded = WebUtility.UrlEncode(resourceUri.ToLowerInvariant());
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_key));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(encoded + "\n" + expiry)));
        return $"SharedAccessSignature sr={encoded}&sig={WebUtility.UrlEncode(signature)}&se={expiry}&skn={_keyName}";
    }
}

public sealed class SendOutcome
{
    public int HttpStatus { get; set; }
    public string Raw { get; set; } = "";
    public string? NotificationId { get; set; }
    public int Success { get; set; }
    public int Failure { get; set; }
    public List<string> Results { get; set; } = new();
}
