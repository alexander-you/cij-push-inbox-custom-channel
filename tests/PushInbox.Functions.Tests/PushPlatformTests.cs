using Newtonsoft.Json.Linq;
using PushInbox.Functions;
using Xunit;

public class PushPlatformTests
{
    private static readonly PushContent Content = new("Title", "Body", "https://img.example/a.png", "https://app.example/?x=1", "msg-1", "r-abc", "ltr");

    [Fact]
    public void FcmV1_payload_has_message_notification_and_string_data()
    {
        var j = JObject.Parse(PushPlatform.FcmV1Payload(Content));
        Assert.Equal("Title", (string?)j["message"]!["notification"]!["title"]);
        Assert.Equal("https://img.example/a.png", (string?)j["message"]!["notification"]!["image"]);
        var data = (JObject)j["message"]!["data"]!;
        Assert.All(data.Properties(), p => Assert.Equal(JTokenType.String, p.Value.Type));
        Assert.Equal("r-abc", (string?)data["inboxItemId"]);
        Assert.Equal("https://app.example/?x=1", (string?)data["url"]);
    }

    [Fact]
    public void FcmV1_payload_omits_missing_image_and_url()
    {
        var j = JObject.Parse(PushPlatform.FcmV1Payload(Content with { ImageUrl = null, ActionUrl = null }));
        Assert.Null(j["message"]!["notification"]!["image"]);
        Assert.Null(j["message"]!["data"]!["url"]);
        Assert.Null(j["message"]!["data"]!["image"]);
    }

    [Fact]
    public void Apns_payload_has_alert_custom_keys_and_mutable_content_only_with_image()
    {
        var j = JObject.Parse(PushPlatform.ApnsPayload(Content));
        Assert.Equal("Body", (string?)j["aps"]!["alert"]!["body"]);
        Assert.Equal(1, (int?)j["aps"]!["mutable-content"]);
        Assert.Equal("msg-1", (string?)j["messageId"]);
        Assert.Null(JObject.Parse(PushPlatform.ApnsPayload(Content with { ImageUrl = null }))["aps"]!["mutable-content"]);
    }

    [Fact]
    public void Native_alert_text_is_shortened_but_browser_is_not()
    {
        var longBody = new string('א', 3000);
        var c = Content with { Body = longBody };
        Assert.Equal(PushPlatform.NativeBodyMax, ((string)JObject.Parse(PushPlatform.ApnsPayload(c))["aps"]!["alert"]!["body"]!).Length);
        Assert.Equal(3000, ((string)JObject.Parse(PushPlatform.BrowserPayload(c))["body"]!).Length);
    }

    [Fact]
    public void Apns_sends_alert_push_type_header()
    {
        Assert.Equal("apple", PushPlatform.ApnsPlatform.Format);
        Assert.Equal("alert", PushPlatform.ApnsPlatform.Headers["apns-push-type"]);
        Assert.Equal("fcmV1", PushPlatform.FcmV1Platform.Format);
    }

    [Theory]
    [InlineData(null, "browser,fcmv1,apns")]
    [InlineData("", "browser,fcmv1,apns")]
    [InlineData(" Browser , apns ", "browser,apns")]
    public void Parse_platform_setting(string? setting, string expected) =>
        Assert.Equal(expected, string.Join(",", PushPlatform.Parse(setting).Select(p => p.Name)));

    [Fact]
    public void Parse_rejects_unknown_platform() => Assert.Throws<InvalidOperationException>(() => PushPlatform.Parse("browser,gcm"));
}

public class PushOutcomeTests
{
    private const string NoTarget = "<Error><Code>400</Code><Detail>The notification has no target applications. The notification format is fcmV1.</Detail></Error>";

    [Fact]
    public void Missing_credentials_is_not_configured_not_error()
    {
        var r = PushOutcome.Classify(new SendOutcome { HttpStatus = 400, Raw = NoTarget }, test: false);
        Assert.Equal(PlatformStatus.NotConfigured, r.Status);
        Assert.Equal(0, r.Pushed);
    }

    [Fact]
    public void Other_4xx_is_error_and_5xx_or_429_is_transient()
    {
        Assert.Equal(PlatformStatus.Error, PushOutcome.Classify(new SendOutcome { HttpStatus = 403, Raw = "quota" }, false).Status);
        Assert.True(PushOutcome.IsTransient(new SendOutcome { HttpStatus = 503 }));
        Assert.True(PushOutcome.IsTransient(new SendOutcome { HttpStatus = 429 }));
        Assert.False(PushOutcome.IsTransient(new SendOutcome { HttpStatus = 400 }));
    }

    [Fact]
    public void Test_send_uses_device_counts()
    {
        Assert.Equal(PlatformStatus.NoDevice, PushOutcome.Classify(new SendOutcome { HttpStatus = 201 }, test: true).Status);
        var r = PushOutcome.Classify(new SendOutcome { HttpStatus = 201, Success = 2, Failure = 1 }, test: true);
        Assert.Equal((PlatformStatus.Sent, 2), (r.Status, r.Pushed));
    }

    private static Dictionary<string, PlatformResult> R(params (string p, string s, int pushed)[] x) =>
        x.ToDictionary(i => i.p, i => new PlatformResult { Status = i.s, Pushed = i.pushed, Detail = i.s == PlatformStatus.Error ? "boom" : null });

    [Fact]
    public void Browser_sent_with_unconfigured_natives_is_delivered()
    {
        var (pushed, reason, detail) = PushOutcome.Aggregate(R(("browser", PlatformStatus.Sent, 1), ("fcmv1", PlatformStatus.NotConfigured, 0), ("apns", PlatformStatus.NotConfigured, 0)));
        Assert.Equal((1, null, null), (pushed, reason, detail));
    }

    [Fact]
    public void Partial_error_still_delivers_and_keeps_detail()
    {
        var (pushed, reason, detail) = PushOutcome.Aggregate(R(("browser", PlatformStatus.Error, 0), ("fcmv1", PlatformStatus.Sent, 1)));
        Assert.Equal(1, pushed);
        Assert.Null(reason);
        Assert.Contains("browser: boom", detail);
    }

    [Theory]
    [InlineData(PlatformStatus.Error, "PushServiceError")]
    [InlineData(PlatformStatus.NoDevice, "NoDevice")]
    [InlineData(PlatformStatus.NotConfigured, "NoPushPlatform")]
    public void Nothing_pushed_gives_reason(string status, string expected) =>
        Assert.Equal(expected, PushOutcome.Aggregate(R(("browser", status, 0), ("apns", PlatformStatus.NotConfigured, 0))).Reason);
}

public class DeviceRegistrationTests
{
    private static InboxApi.DeviceRegistration Reg(string? platform, string? token = null) => new()
    {
        Platform = platform, InstallationId = "inst-1", PushToken = token,
        Endpoint = "https://fcm.googleapis.com/fcm/send/x", P256dh = "k", Auth = "a",
    };

    [Theory]
    [InlineData(null, null, "browser")]
    [InlineData("BROWSER", null, "browser")]
    [InlineData("fcmv1", "dGVzdDpBUEE5MWJ", "fcmv1")]
    [InlineData("fcmv1", "", null)]
    [InlineData("fcmv1", "has space", null)]
    [InlineData("apns", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789ABCDEF", "apns")]
    [InlineData("apns", "not-hex", null)]
    [InlineData("gcm", "x", null)]
    public void Validates_per_platform(string? platform, string? token, string? expected) =>
        Assert.Equal(expected, InboxApi.DeviceRegistration.Validate(Reg(platform, token)));

    [Fact]
    public void Browser_requires_https_subscription()
    {
        var r = Reg("browser");
        r.Endpoint = "http://insecure";
        Assert.Null(InboxApi.DeviceRegistration.Validate(r));
    }
}
