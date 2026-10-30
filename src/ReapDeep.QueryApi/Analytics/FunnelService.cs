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
    public const int MaxPersons = 10_000;

    sealed record Plan(List<FunnelStep> Steps, long WindowSeconds, double Sampling, List<(string? Cohort, SqlQuery Query)> Queries, SqlQuery Times);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, FunnelRequest req, CancellationToken ct)
    {
        var plan = await BuildPlanAsync(project, req, ct);
        return await starRocks.ExplainAsync([.. plan.Queries.Select(q => q.Query), plan.Times], ct);
    }

    public async Task<FunnelResponse> RunAsync(ProjectContext project, FunnelRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);
        var timesTask = starRocks.QueryAsync(plan.Times, r => ReadTimes(r, plan.Steps.Count), ct);
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

        var times = (await timesTask).SingleOrDefault();
        return new FunnelResponse(plan.WindowSeconds, plan.Sampling, results, sw.Elapsed.TotalMilliseconds, times);
    }

    public async Task<FunnelPersonsResponse> PersonsAsync(ProjectContext project, FunnelPersonsRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var steps = Steps(req.Steps);
        var window = Window(req.WindowSeconds);
        var kind = Ensure.OneOf(req.Kind ?? FunnelSql.Dropped, "kind", FunnelSql.Dropped, FunnelSql.Converted);
        var step = Ensure.InRange(req.Step ?? 1, 1, kind == FunnelSql.Dropped ? steps.Count - 1 : steps.Count, "step");
        var limit = Ensure.InRange(req.Limit ?? 100, 1, MaxPersons, "limit");

        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(30));
        var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
        b.AddFilters(req.Filters, await cohorts.ResolveAsync(project, req.Filters, null, ct));
        var rows = await starRocks.QueryAsync(FunnelSql.Persons(b, steps, window, step, kind, limit),
            r => (Person: r.GetString(0), Total: Convert.ToInt64(r.GetValue(1))), ct);

        var persons = await cohorts.DescribeAsync(project, rows.Select(r => r.Person).ToList(), ct);
        return new FunnelPersonsResponse(step, kind, rows.Count == 0 ? 0 : rows[0].Total, persons, sw.Elapsed.TotalMilliseconds);
    }

    async Task<Plan> BuildPlanAsync(ProjectContext project, FunnelRequest req, CancellationToken ct)
    {
        var steps = Steps(req.Steps);
        var window = Window(req.WindowSeconds);
        var sampling = req.Sampling ?? 1;
        Ensure.That(sampling is >= 0.01 and <= 1, "sampling must be between 0.01 and 1");

        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(30));
        var refs = await cohorts.ResolveAsync(project, req.Filters, req.Breakdown, ct);

        SqlBuilder NewBuilder(CohortRef? cohort)
        {
            var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
            b.AddFilters(req.Filters, refs);
            if (cohort is not null) b.InCohort(cohort);
            return b;
        }

        List<(string?, SqlQuery)> queries = req.Breakdown?.Scope == "cohort"
            ? req.Breakdown.CohortIds!.Distinct().Select(id => ((string?)refs[id].Name, FunnelSql.Build(NewBuilder(refs[id]), steps, window, null, sampling))).ToList()
            : [(null, FunnelSql.Build(NewBuilder(null), steps, window, req.Breakdown, sampling))];
        return new Plan(steps, window, sampling, queries, FunnelSql.Times(NewBuilder(null), steps, window, sampling));
    }

    static List<FunnelStep> Steps(List<FunnelStep>? requested)
    {
        var steps = requested ?? [];
        Ensure.That(steps.Count is >= 2 and <= 8, "steps must contain 2-8 items");
        Ensure.That(steps.All(s => !string.IsNullOrEmpty(s.Event)), "Each step requires an event");
        return steps;
    }

    static long Window(long? requested)
    {
        var window = requested ?? DefaultWindowSeconds;
        Ensure.That(window is >= 1 and <= MaxWindowSeconds, "window_seconds must be between 1 second and 90 days");
        return window;
    }

    static FunnelTimes ReadTimes(MySqlConnector.MySqlDataReader r, int stepCount)
    {
        double? Read(int i) => r.IsDBNull(i) ? null : Math.Round(Convert.ToDouble(r.GetValue(i)), 1);
        var steps = Enumerable.Range(2, stepCount - 1).Select((k, j) =>
            new FunnelStepTime(k, r.IsDBNull(j * 3) ? 0 : Convert.ToInt64(r.GetValue(j * 3)), Read(j * 3 + 1), Read(j * 3 + 2))).ToList();
        var last = (stepCount - 1) * 3;
        return new FunnelTimes(steps, Read(last), Read(last + 1));
    }

    static double Ratio(long a, long b) => b == 0 ? 0 : Math.Round((double)a / b, 4);
}
