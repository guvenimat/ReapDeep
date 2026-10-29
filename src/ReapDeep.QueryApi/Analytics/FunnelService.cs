using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class FunnelService(StarRocksClient starRocks)
{
    const long DefaultWindowSeconds = 7 * 24 * 3600;
    const long MaxWindowSeconds = 90 * 24 * 3600;

    sealed record Plan(List<FunnelStep> Steps, long WindowSeconds, double Sampling, SqlQuery Query);

    public Task<ExplainResponse> ExplainAsync(ProjectContext project, FunnelRequest req, CancellationToken ct) =>
        starRocks.ExplainAsync([BuildPlan(project, req).Query], ct);

    public async Task<FunnelResponse> RunAsync(ProjectContext project, FunnelRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = BuildPlan(project, req);
        var rows = await starRocks.QueryAsync(plan.Query, r => (
            Breakdown: r.IsDBNull(0) ? null : r.GetString(0),
            Counts: Enumerable.Range(1, plan.Steps.Count)
                .Select(i => r.IsDBNull(i) ? 0L : (long)Math.Round(Convert.ToInt64(r.GetValue(i)) / plan.Sampling))
                .ToArray()), ct);

        var results = rows.Select(row => new FunnelResult(row.Breakdown, plan.Steps.Select((step, i) => new FunnelStepResult(
            Step: i + 1,
            Event: step.Event,
            Count: row.Counts[i],
            ConversionFromFirst: Ratio(row.Counts[i], row.Counts[0]),
            ConversionFromPrevious: i == 0 ? 1 : Ratio(row.Counts[i], row.Counts[i - 1]))).ToList())).ToList();

        return new FunnelResponse(plan.WindowSeconds, plan.Sampling, results, sw.Elapsed.TotalMilliseconds);
    }

    static Plan BuildPlan(ProjectContext project, FunnelRequest req)
    {
        var steps = req.Steps ?? [];
        Ensure.That(steps.Count is >= 2 and <= 8, "steps must contain 2-8 items");
        Ensure.That(steps.All(s => !string.IsNullOrEmpty(s.Event)), "Each step requires an event");
        var window = req.WindowSeconds ?? DefaultWindowSeconds;
        Ensure.That(window is >= 1 and <= MaxWindowSeconds, "window_seconds must be between 1 second and 90 days");
        var sampling = req.Sampling ?? 1;
        Ensure.That(sampling is >= 0.01 and <= 1, "sampling must be between 0.01 and 1");

        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(30));
        var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
        b.AddFilters(req.Filters);
        return new Plan(steps, window, sampling, FunnelSql.Build(b, steps, window, req.Breakdown, sampling));
    }

    static double Ratio(long a, long b) => b == 0 ? 0 : Math.Round((double)a / b, 4);
}
