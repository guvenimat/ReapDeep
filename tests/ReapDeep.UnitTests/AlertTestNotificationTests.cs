using System.Text.Json;
using ReapDeep.QueryApi.Alerts;

namespace ReapDeep.UnitTests;

public class AlertTestNotificationTests
{
    static readonly DateTime Now = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_test_email_says_it_is_a_test_and_not_a_state_change()
    {
        var mail = JsonDocument.Parse(AlertMessages.Render(AlertChannels.Email, AlertMessages.TestPayload(5, "Revenue drop", 1, Now))).RootElement;
        var text = mail.GetProperty("text").GetString()!;
        Assert.Equal("[ReapDeep] Test notification for Revenue drop", mail.GetProperty("subject").GetString());
        Assert.StartsWith("Test notification for Revenue drop.", text);
        Assert.Contains(AlertMessages.TestMessage, text);
        Assert.DoesNotContain("is test", text);
    }

    [Fact]
    public void A_test_slack_message_is_escaped()
    {
        var slack = JsonDocument.Parse(AlertMessages.Render(AlertChannels.Slack, AlertMessages.TestPayload(0, "A <b> & c", 1, Now))).RootElement;
        Assert.Equal($"*A &lt;b&gt; &amp; c*: {AlertMessages.TestMessage}", slack.GetProperty("text").GetString());
    }

    [Fact]
    public void A_test_webhook_has_the_test_state_and_the_alert_id()
    {
        using var doc = JsonDocument.Parse(AlertMessages.Render(AlertChannels.Webhook, AlertMessages.TestPayload(9, "Orders", 4, Now)));
        Assert.Equal("test", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal(9, doc.RootElement.GetProperty("alert_id").GetInt64());
        Assert.Equal(4, doc.RootElement.GetProperty("project_id").GetInt64());
    }

    [Theory]
    [InlineData(null, AlertRules.EventNotFound)]
    [InlineData("pending", "This delivery is still being retried automatically")]
    [InlineData("delivered", "This delivery already succeeded")]
    [InlineData("none", "This event has no notification to resend")]
    public void Only_failed_deliveries_can_be_retried_by_hand(string? status, string refusal) =>
        Assert.Equal(refusal, AlertRules.RetryRefusal(status));
}
