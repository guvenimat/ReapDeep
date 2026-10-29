using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class FunnelService(StarRocksClient starRocks, CohortService cohorts)
{
    const long DefaultWindowSeconds = 7 * 24 * 3600;
    const long MaxWindowSeconds = 90 * 24 * 3600;

    sealed record Plan(List<FunnelStep> Steps, long WindowSeconds, double Sampling, List<(string? Cohort, SqlQuery Query)> Queries);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, FunnelRequest req, CancellationToken ct) =>
        await starRocks.ExplainAsync((await BuildPlanAsync(project, req, ct)).Queries.Select(q => q.Query).ToList(), ct);

    public async Task<FunnelResponse> RunAsync(ProjectContext project, FunnelRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);
        var perQuery = await Task.WhenAll(plan.Queries.Select(async q =>
        {
            var found = await starRocks.QueryAsync(q.Query, r => (
                Breakdown: q.Cohort ?? (r.IsDBNull(0) ? null : r.GetString(0)),
                Counts: Enumerable.Range(1, plan.Steps.Count)
                    .Select(i => r.IsDBNull(i) ? 0L : (long)Math.Round(Convert.ToInt64(r.GetValue(i)) / plan.Sampling))
                    .ToArray()), ct);
            return found.Count == 0 && q.Cohort is not null ? [(q.Cohort, new long[plan.Steps.Count])] : found;
        }));
        var rows = perQuery.SelectMany(r => r).ToList();

        var results = rows.Select(row => new FunnelResult(row.Breakdown, plan.Steps.Select((step, i) => new FunnelStepResult(
            Step: i + 1,
            Event: step.Event,
            Count: row.Counts[i],
            ConversionFromFirst: Ratio(row.Counts[i], row.Counts[0]),
            ConversionFromPrevious: i == 0 ? 1 : Ratio(row.Counts[i], row.Counts[i - 1]))).ToList())).ToList();

        return new FunnelResponse(plan.WindowSeconds, plan.Sampling, results, sw.Elapsed.TotalMilliseconds);
    }

    async Task<Plan> BuildPlanAsync(ProjectContext project, FunnelRequest req, CancellationToken ct)
    {
        var steps = req.Steps ?? [];
        Ensure.That(steps.Count is >= 2 and <= 8, "steps must contain 2-8 items");
        Ensure.That(steps.All(s => !string.IsNullOrEmpty(s.Event)), "Each step requires an event");
        var window = req.WindowSeconds ?? DefaultWindowSeconds;
        Ensure.That(window is >= 1 and <= MaxWindowSeconds, "window_seconds must be between 1 second and 90 days");
        var sampling = req.Sampling ?? 1;
        Ensure.That(sampling is >= 0.01 and <= 1, "sampling must be between 0.01 and 1");

        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(30));
        var refs = await cohorts.ResolveAsync(project, req.Filters, req.Breakdown, ct);

        SqlQuery Build(CohortRef? cohort, Breakdown? bd)
        {
            var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
            b.AddFilters(req.Filters, refs);
            if (cohort is not null) b.InCohort(cohort);
            return FunnelSql.Build(b, steps, window, bd, sampling);
        }

        List<(string?, SqlQuery)> queries = req.Breakdown?.Scope == "cohort"
            ? req.Breakdown.CohortIds!.Distinct().Select(id => ((string?)refs[id].Name, Build(refs[id], null))).ToList()
            : [(null, Build(null, req.Breakdown))];
        return new Plan(steps, window, sampling, queries);
    }

    static double Ratio(long a, long b) => b == 0 ? 0 : Math.Round((double)a / b, 4);
}
