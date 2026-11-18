using System.Globalization;
using System.Text.Json;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Alerts;

public static class AlertChannels
{
    public const string Webhook = "webhook";
    public const string Slack = "slack";
    public const string Email = "email";

    public static readonly string[] All = [Webhook, Slack, Email];
}

public sealed record AlertTarget(string Channel, string? WebhookUrl, string[]? EmailTo);

public static class AlertMessages
{
    public static string Render(string channel, AlertWebhookPayload p) => channel switch
    {
        AlertChannels.Slack => JsonSerializer.Serialize(new SlackMessage(SlackText(p)), AppJsonContext.Default.SlackMessage),
        AlertChannels.Email => JsonSerializer.Serialize(new EmailMessage(Subject(p), EmailText(p)), AppJsonContext.Default.EmailMessage),
        _ => WebhookSender.Serialize(p),
    };

    public const string TestMessage = "This is a test notification from ReapDeep; the alert's state did not change.";

    public static AlertWebhookPayload TestPayload(long alertId, string alertName, long projectId, DateTime now) =>
        new(alertId, alertName, projectId, AlertRules.Test, AlertRules.Above, 0, "day", 0, null, TestMessage, new AlertWebhookInsight(0, "Test"), now);

    public static string Subject(AlertWebhookPayload p) =>
        p.State == AlertRules.Test ? $"[ReapDeep] Test notification for {p.AlertName}" : $"[ReapDeep] {p.AlertName} is {p.State}";

    public static string SlackText(AlertWebhookPayload p) => p.State == AlertRules.Test
        ? $"*{Escape(p.AlertName)}*: {Escape(p.Message)}"
        : $"*{Escape(p.AlertName)}* is {(p.State == AlertRules.Firing ? "firing" : "resolved")}: {Escape(p.Message)}\n" +
          $"Trend: {Escape(p.Insight.Name)} · window: {p.Window} · checked {Time(p.CheckedAt)}";

    public static string EmailText(AlertWebhookPayload p) => p.State == AlertRules.Test
        ? string.Join("\n", $"Test notification for {p.AlertName}.", "", p.Message, "", $"Sent: {Time(p.CheckedAt)}", "", "Sent by ReapDeep alerts.")
        : string.Join("\n",
        $"{p.AlertName} is {p.State}.",
        "",
        p.Message,
        "",
        $"Trend: {p.Insight.Name}",
        $"Window: {p.Window}",
        $"Value: {p.Value.ToString("0.####", CultureInfo.InvariantCulture)}",
        $"Previous window: {(p.PreviousValue is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "-")}",
        $"Checked: {Time(p.CheckedAt)}",
        "",
        "Sent by ReapDeep alerts.");

    static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
