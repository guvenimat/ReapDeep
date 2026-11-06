using System.Security.Cryptography;
using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;
using ReapDeep.QueryApi.Insights;

namespace ReapDeep.QueryApi.Alerts;

public sealed class AlertService(AlertRepository alerts, InsightRepository insights, TrendService trends, WebhookSender webhooks, EmailSender email,
    IConfiguration cfg, ILogger<AlertService> log)
{
    readonly IReadOnlyList<TimeSpan> _retryDelays = AlertRules.ParseDelays(cfg["Alerts:RetryDelaysSeconds"]);

    public const int MaxAlertsPerProject = 50;

    public async Task<AlertRow> CreateAsync(ProjectContext project, AlertBody req, CancellationToken ct)
    {
        var (name, insight, condition, threshold, window, target) = await ValidateAsync(project, req, ct);
        Ensure.That((await alerts.ListAsync(project.ProjectId, ct)).Count < MaxAlertsPerProject, $"A project can have at most {MaxAlertsPerProject} alerts");
        var secret = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var id = await alerts.CreateAsync(project.ProjectId, name, insight.Id, condition, threshold, window, target, secret, req.Enabled ?? true,
            project.UserId!.Value, ct);
        return (await alerts.GetAsync(project.ProjectId, id, ct))!.ToContract(withSecret: true);
    }

    public async Task<AlertRow?> UpdateAsync(ProjectContext project, long id, AlertBody req, CancellationToken ct)
    {
        if (await alerts.GetAsync(project.ProjectId, id, ct) is not { } current) return null;
        var channel = req.Channel ?? current.Channel;
        var sameChannel = channel == current.Channel;
        var merged = new AlertBody(req.Name ?? current.Name, req.InsightId ?? current.InsightId, req.Condition ?? current.Condition,
            req.Threshold ?? current.Threshold, req.Window ?? current.WindowUnit, req.WebhookUrl ?? (sameChannel ? current.WebhookUrl : null),
            req.Enabled ?? current.Enabled, channel, req.EmailTo ?? (sameChannel ? current.EmailTo?.ToList() : null));
        var (name, insight, condition, threshold, window, target) = await ValidateAsync(project, merged, ct);
        var rulesChanged = insight.Id != current.InsightId || condition != current.Condition || threshold != current.Threshold || window != current.WindowUnit;
        await alerts.UpdateAsync(project.ProjectId, id, name, insight.Id, condition, threshold, window, target, merged.Enabled!.Value, rulesChanged, ct);
        return (await alerts.GetAsync(project.ProjectId, id, ct))!.ToContract(withSecret: true);
    }

    public async Task<AlertCheckResponse?> CheckNowAsync(ProjectContext project, long id, CancellationToken ct) =>
        await alerts.GetAsync(project.ProjectId, id, ct) is { } alert ? await EvaluateAsync(alert, ct) : null;

    internal async Task<AlertCheckResponse> EvaluateAsync(AlertRepository.AlertRecord alert, CancellationToken ct)
    {
        var next = DateTime.UtcNow + AlertRules.CheckEvery(alert.WindowUnit);
        double value;
        double? previous;
        try
        {
            (value, previous) = await MeasureAsync(alert, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Alert {Alert} could not be evaluated", alert.Id);
            var message = e is ValidationException or InsightGoneException ? e.Message : "The insight query failed";
            AlertEventRow? errorEvent = alert.State == AlertRules.Error
                ? null
                : await alerts.AddEventAsync(alert.Id, alert.ProjectId, AlertRules.Error, null, null, message, null, null, ct);
            await alerts.SaveResultAsync(alert.Id, AlertRules.Error, null, null, message, next, ct);
            return new AlertCheckResponse(AlertRules.Error, null, null, message, errorEvent);
        }

        var (firing, text) = AlertRules.Evaluate(alert.Condition, alert.Threshold, value, previous);
        var state = firing ? AlertRules.Firing : AlertRules.Ok;
        AlertEventRow? evt = null;
        if (AlertRules.Transition(alert.State, firing) is { } kind)
        {
            var payload = new AlertWebhookPayload(alert.Id, alert.Name, alert.ProjectId, firing ? AlertRules.Firing : AlertRules.Resolved,
                alert.Condition, alert.Threshold, alert.WindowUnit, value, previous, text, new AlertWebhookInsight(alert.InsightId, alert.InsightName), DateTime.UtcNow);
            var body = AlertMessages.Render(alert.Channel, payload);
            var pending = await alerts.AddEventAsync(alert.Id, alert.ProjectId, kind, value, previous, text, body, DateTime.UtcNow + AlertJob.Lease, ct,
                alert.Channel);
            evt = await DeliverAsync(pending.Id, alert.Id, alert.Channel, alert.WebhookUrl, alert.EmailTo, alert.Secret, body, 1, ct);
        }
        await alerts.SaveResultAsync(alert.Id, state, value, previous, null, next, ct);
        return new AlertCheckResponse(state, value, previous, text, evt);
    }

    internal async Task<AlertEventRow> DeliverAsync(long eventId, long alertId, string channel, string? url, string[]? emailTo, string secret,
        string payload, int attempt, CancellationToken ct)
    {
        var result = channel switch
        {
            AlertChannels.Email when emailTo is { Length: > 0 } => await email.SendAsync(emailTo, payload, ct),
            AlertChannels.Webhook or AlertChannels.Slack when url is not null => await webhooks.SendAsync(new Uri(url), secret, payload, alertId, eventId, attempt, ct),
            _ => new WebhookResult(null, $"The alert no longer has a {channel} destination", false),
        };
        if (result.Error is null)
            return await alerts.RecordAttemptAsync(eventId, result.Status, null, AlertRules.DeliveryDelivered, null, ct);
        var retryIn = result.Retryable ? AlertRules.RetryAfter(attempt, _retryDelays) : null;
        if (retryIn is null)
            log.LogWarning("Webhook delivery {Delivery} of alert {Alert} failed for good after {Attempts} attempts: {Error}", eventId, alertId, attempt, result.Error);
        return await alerts.RecordAttemptAsync(eventId, result.Status, result.Error,
            retryIn is null ? AlertRules.DeliveryFailed : AlertRules.DeliveryPending, DateTime.UtcNow + retryIn, ct);
    }

    async Task<(double Value, double? Previous)> MeasureAsync(AlertRepository.AlertRecord alert, CancellationToken ct)
    {
        var insight = await insights.GetAsync(alert.ProjectId, alert.InsightId, ct);
        if (insight is null || insight.Kind != "trends") throw new InsightGoneException("The insight is missing or is not a trend");

        var saved = insight.Query.Deserialize(AppJsonContext.Default.TrendRequest) ?? throw new InsightGoneException("The insight has no query");
        var now = DateTime.UtcNow;
        var req = saved with
        {
            DateFrom = (now - AlertRules.Span(alert.WindowUnit)).ToString("o"),
            DateTo = now.ToString("o"),
            Interval = alert.WindowUnit == "week" ? "day" : "hour",
            Breakdown = null,
            Compare = AlertRules.ComparesPeriods(alert.Condition),
            Explain = false,
        };
        var project = ProjectContext.Create(alert.ProjectId, alert.TimeZone, null, null, log);
        var results = (await trends.RunAsync(project, req, ct)).Results;
        var visible = results.Any(r => r.Metric == TrendService.FormulaMetric) ? results.Where(r => r.Metric == TrendService.FormulaMetric).ToList() : results;
        var current = visible.FirstOrDefault(r => r.Compare is null) ?? throw new InsightGoneException("The insight returned no series");
        var prev = visible.FirstOrDefault(r => r.Compare is not null && r.Series == current.Series);
        return (current.Total, prev?.Total);
    }

    async Task<(string Name, InsightRow Insight, string Condition, double Threshold, string Window, AlertTarget Target)> ValidateAsync(
        ProjectContext project, AlertBody req, CancellationToken ct)
    {
        var name = Ensure.NotBlank(req.Name, "name");
        Ensure.That(req.InsightId is > 0, "insight_id is required");
        var insight = await insights.GetAsync(project.ProjectId, req.InsightId!.Value, ct);
        Ensure.That(insight is not null, $"Unknown insight: {req.InsightId}");
        Ensure.That(insight!.Kind == "trends", "Alerts watch a saved trend insight");
        var condition = req.Condition ?? AlertRules.Above;
        var threshold = req.Threshold ?? 0;
        AlertRules.Validate(condition, threshold);
        var window = Ensure.OneOf(req.Window ?? "day", "window", AlertRules.Windows);
        var channel = Ensure.OneOf(req.Channel ?? AlertChannels.Webhook, "channel", AlertChannels.All);
        if (channel == AlertChannels.Email)
        {
            Ensure.That(email.Configured, "Email alerts need an SMTP server (Alerts__Smtp__Host)");
            return (name, insight, condition, threshold, window, new AlertTarget(channel, null, EmailSender.ParseRecipients(req.EmailTo)));
        }
        var url = WebhookSender.ParseUrl(req.WebhookUrl);
        await webhooks.CheckDestinationAsync(url, ct);
        return (name, insight, condition, threshold, window, new AlertTarget(channel, url.ToString(), null));
    }
}

public sealed class InsightGoneException(string message) : Exception(message);
