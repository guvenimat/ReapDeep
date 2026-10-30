using System.Security.Cryptography;
using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;
using ReapDeep.QueryApi.Insights;

namespace ReapDeep.QueryApi.Alerts;

public sealed class AlertService(AlertRepository alerts, InsightRepository insights, TrendService trends, WebhookSender webhooks, ILogger<AlertService> log)
{
    public const int MaxAlertsPerProject = 50;

    public async Task<AlertRow> CreateAsync(ProjectContext project, AlertBody req, CancellationToken ct)
    {
        var (name, insight, condition, threshold, window, url) = await ValidateAsync(project, req, ct);
        Ensure.That((await alerts.ListAsync(project.ProjectId, ct)).Count < MaxAlertsPerProject, $"A project can have at most {MaxAlertsPerProject} alerts");
        var secret = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var id = await alerts.CreateAsync(project.ProjectId, name, insight.Id, condition, threshold, window, url.ToString(), secret, req.Enabled ?? true,
            project.UserId!.Value, ct);
        return (await alerts.GetAsync(project.ProjectId, id, ct))!.ToContract(withSecret: true);
    }

    public async Task<AlertRow?> UpdateAsync(ProjectContext project, long id, AlertBody req, CancellationToken ct)
    {
        if (await alerts.GetAsync(project.ProjectId, id, ct) is not { } current) return null;
        var merged = new AlertBody(req.Name ?? current.Name, req.InsightId ?? current.InsightId, req.Condition ?? current.Condition,
            req.Threshold ?? current.Threshold, req.Window ?? current.WindowUnit, req.WebhookUrl ?? current.WebhookUrl, req.Enabled ?? current.Enabled);
        var (name, insight, condition, threshold, window, url) = await ValidateAsync(project, merged, ct);
        var rulesChanged = insight.Id != current.InsightId || condition != current.Condition || threshold != current.Threshold || window != current.WindowUnit;
        await alerts.UpdateAsync(project.ProjectId, id, name, insight.Id, condition, threshold, window, url.ToString(), merged.Enabled!.Value, rulesChanged, ct);
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
            var (status, error) = await webhooks.SendAsync(new Uri(alert.WebhookUrl), alert.Secret, payload, ct);
            evt = await alerts.AddEventAsync(alert.Id, alert.ProjectId, kind, value, previous, text, status, error, ct);
        }
        await alerts.SaveResultAsync(alert.Id, state, value, previous, null, next, ct);
        return new AlertCheckResponse(state, value, previous, text, evt);
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

    async Task<(string Name, InsightRow Insight, string Condition, double Threshold, string Window, Uri Url)> ValidateAsync(
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
        var url = WebhookSender.ParseUrl(req.WebhookUrl);
        await webhooks.CheckDestinationAsync(url, ct);
        return (name, insight, condition, threshold, window, url);
    }
}

public sealed class InsightGoneException(string message) : Exception(message);
