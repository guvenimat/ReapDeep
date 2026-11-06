using System.Net;
using Microsoft.Extensions.Configuration;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Alerts;

namespace ReapDeep.UnitTests;

public class AlertTests
{
    static AlertWebhookPayload Payload(string state = "firing") => new(7, "Revenue <drop>", 1, state, "below", 100, "day", 80, 120,
        "80 is below 100", new AlertWebhookInsight(3, "Revenue & orders"), new DateTime(2026, 11, 6, 12, 30, 0, DateTimeKind.Utc));

    [Fact]
    public void Slack_messages_are_escaped_text_payloads()
    {
        var json = System.Text.Json.JsonDocument.Parse(AlertMessages.Render(AlertChannels.Slack, Payload())).RootElement;
        var text = json.GetProperty("text").GetString()!;
        Assert.StartsWith("*Revenue &lt;drop&gt;* is firing: 80 is below 100", text);
        Assert.Contains("Trend: Revenue &amp; orders", text);
        Assert.Single(json.EnumerateObject());
    }

    [Fact]
    public void Email_messages_have_a_subject_and_a_plain_text_body()
    {
        var json = System.Text.Json.JsonDocument.Parse(AlertMessages.Render(AlertChannels.Email, Payload("resolved"))).RootElement;
        Assert.Equal("[ReapDeep] Revenue <drop> is resolved", json.GetProperty("subject").GetString());
        var body = json.GetProperty("text").GetString()!;
        Assert.Contains("Previous window: 120", body);
        Assert.Contains("Checked: 2026-11-06 12:30 UTC", body);
    }

    [Fact]
    public void Webhook_payload_keeps_the_signed_json_contract()
    {
        var json = System.Text.Json.JsonDocument.Parse(AlertMessages.Render(AlertChannels.Webhook, Payload())).RootElement;
        Assert.Equal(7, json.GetProperty("alert_id").GetInt64());
        Assert.Equal("Revenue & orders", json.GetProperty("insight").GetProperty("name").GetString());
    }

    [Fact]
    public void Email_recipients_are_validated_and_deduplicated()
    {
        Assert.Equal(["ops@example.com", "team@example.org"], EmailSender.ParseRecipients([" ops@example.com", "team@example.org", "OPS@example.com"]));
        Assert.Throws<ValidationException>(() => EmailSender.ParseRecipients([]));
        Assert.Throws<ValidationException>(() => EmailSender.ParseRecipients(["not an address"]));
        Assert.Throws<ValidationException>(() => EmailSender.ParseRecipients(Enumerable.Range(0, 11).Select(i => $"u{i}@example.com").ToList()));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(410, false)]
    public void Only_network_errors_timeouts_throttling_and_server_errors_are_retried(int? status, bool retry) =>
        Assert.Equal(retry, AlertRules.Retryable(status));

    [Fact]
    public void Retries_back_off_and_stop_after_the_last_delay()
    {
        var delays = AlertRules.DefaultRetryDelays;
        Assert.Equal(TimeSpan.FromMinutes(1), AlertRules.RetryAfter(1, delays));
        Assert.Equal(TimeSpan.FromHours(3), AlertRules.RetryAfter(delays.Length, delays));
        Assert.Null(AlertRules.RetryAfter(delays.Length + 1, delays));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], AlertRules.ParseDelays("2, 4"));
        Assert.Same(AlertRules.DefaultRetryDelays, AlertRules.ParseDelays(null));
    }

    [Theory]
    [InlineData("above", 2, 3, null, true)]
    [InlineData("above", 3, 3, null, false)]
    [InlineData("below", 25, 20, null, true)]
    [InlineData("increase", 50, 150, 100.0, true)]
    [InlineData("increase", 50, 149, 100.0, false)]
    [InlineData("decrease", 30, 70, 100.0, true)]
    [InlineData("decrease", 30, 71, 100.0, false)]
    [InlineData("increase", 50, 3, 0.0, true)]
    [InlineData("decrease", 50, 0, 0.0, false)]
    public void Conditions_compare_the_value_or_the_change_in_percent(string condition, double threshold, double value, double? previous, bool firing) =>
        Assert.Equal(firing, AlertRules.Evaluate(condition, threshold, value, previous).Firing);

    [Theory]
    [InlineData("pending", true, "fired")]
    [InlineData("ok", true, "fired")]
    [InlineData("error", true, "fired")]
    [InlineData("firing", true, null)]
    [InlineData("firing", false, "resolved")]
    [InlineData("ok", false, null)]
    [InlineData("pending", false, null)]
    public void Notifications_are_sent_only_on_transitions(string state, bool firing, string? expected) =>
        Assert.Equal(expected, AlertRules.Transition(state, firing));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.18.0.5", true)]
    [InlineData("192.168.65.254", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Private_loopback_link_local_and_metadata_addresses_are_blocked(string ip, bool blocked) =>
        Assert.Equal(blocked, WebhookSender.IsPrivate(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://127.0.0.1:8080/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/hook")]
    public async Task Webhooks_to_private_addresses_are_rejected_by_default(string url)
    {
        using var sender = new WebhookSender(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<ValidationException>(() => sender.CheckDestinationAsync(WebhookSender.ParseUrl(url), CancellationToken.None));
    }

    [Fact]
    public async Task Private_webhooks_can_be_allowed_for_local_setups()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Alerts:AllowPrivateWebhooks"] = "true" }).Build();
        using var sender = new WebhookSender(cfg);
        await sender.CheckDestinationAsync(WebhookSender.ParseUrl("http://127.0.0.1:8080/hook"), CancellationToken.None);
    }

    [Theory]
    [InlineData("ftp://example.com/x")]
    [InlineData("http://user:secret@example.com/x")]
    [InlineData("example.com/hook")]
    public void Only_plain_http_and_https_urls_are_accepted(string url) =>
        Assert.Throws<ValidationException>(() => WebhookSender.ParseUrl(url));

    [Fact]
    public void Signature_is_lowercase_hex_hmac_sha256() =>
        Assert.Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            WebhookSender.Sign("key", "The quick brown fox jumps over the lazy dog"u8.ToArray()));
}
