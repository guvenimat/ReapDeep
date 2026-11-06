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

    public static string Subject(AlertWebhookPayload p) => $"[ReapDeep] {p.AlertName} is {p.State}";

    public static string SlackText(AlertWebhookPayload p) =>
        $"*{Escape(p.AlertName)}* is {(p.State == AlertRules.Firing ? "firing" : "resolved")}: {Escape(p.Message)}\n" +
        $"Trend: {Escape(p.Insight.Name)} · window: {p.Window} · checked {Time(p.CheckedAt)}";

    public static string EmailText(AlertWebhookPayload p) => string.Join("\n",
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
