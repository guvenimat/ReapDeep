using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class TrendService(StarRocksClient starRocks, CohortService cohorts)
{
    static readonly string[] Metrics = ["total", "unique_users"];

    sealed record SeriesPlan(Series Series, string? Cohort, TrendSql.Plan Plan);
    sealed record Plan(string Interval, TimeRange Range, List<SeriesPlan> Series);
    readonly record struct Row(DateTime? Bucket, string? Breakdown, long Count);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, TrendRequest req, CancellationToken ct) =>
        await starRocks.ExplainAsync((await BuildPlanAsync(project, req, ct)).Series.SelectMany(s => new[] { s.Plan.Buckets, s.Plan.Total }).OfType<SqlQuery>().ToList(), ct);

    public async Task<TrendResponse> RunAsync(ProjectContext project, TrendRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);

        var bucketTasks = plan.Series.Select(s => Query(s.Plan.Buckets, ct)).ToList();
        var totalTasks = plan.Series.Select(s => s.Plan.Total is { } t ? Query(t, ct) : null).ToList();
        await Task.WhenAll(bucketTasks.Concat(totalTasks.OfType<Task<List<Row>>>()));

        var buckets = plan.Range.Buckets(plan.Interval);
        var results = new List<TrendResult>();
        for (var i = 0; i < plan.Series.Count; i++)
        {
            var totals = totalTasks[i]?.Result.ToDictionary(r => r.Breakdown ?? "", r => r.Count);
            var cohort = plan.Series[i].Cohort;
            var series = BuildResults(plan.Series[i].Series, bucketTasks[i].Result, totals, buckets, req.Breakdown is not null && cohort is null);
            results.AddRange(cohort is null ? series : series.Select(r => r with { BreakdownValue = cohort }));
        }
        return new TrendResponse(plan.Interval, project.TimeZoneId, results, sw.Elapsed.TotalMilliseconds);
    }

    async Task<Plan> BuildPlanAsync(ProjectContext project, TrendRequest req, CancellationToken ct)
    {
        var requested = req.Series ?? [];
        Ensure.That(requested.Count is > 0 and <= 10, "series must contain 1-10 items");
        foreach (var s in requested)
        {
            Ensure.That(!string.IsNullOrEmpty(s.Event), "Each series requires an event");
            Ensure.That(s.Metric is null || Metrics.Contains(s.Metric), $"Invalid metric: '{s.Metric}' (total | unique_users)");
        }
        var interval = Ensure.OneOf(req.Interval ?? "day", "interval", TimeRange.Intervals);
        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(7));
        var refs = await cohorts.ResolveAsync(project, req.Filters, req.Breakdown, ct);

        SqlBuilder NewBuilder(CohortRef? cohort)
        {
            var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
            b.AddFilters(req.Filters, refs);
            if (cohort is not null) b.InCohort(cohort);
            return b;
        }
        TrendSql.Plan Build(Series s, CohortRef? cohort, Breakdown? bd) =>
            TrendSql.Build(() => NewBuilder(cohort), s, req.Deduplicate, interval, range, project.TimeZoneId, bd);

        if (req.Breakdown?.Scope != "cohort")
            return new Plan(interval, range, requested.Select(s => new SeriesPlan(s, null, Build(s, null, req.Breakdown))).ToList());

        var breakdownCohorts = req.Breakdown.CohortIds!.Distinct().Select(id => refs[id]).ToList();
        Ensure.That(requested.Count * breakdownCohorts.Count <= 10, "series x cohorts can be at most 10");
        return new Plan(interval, range, requested
            .SelectMany(s => breakdownCohorts.Select(c => new SeriesPlan(s, c.Name, Build(s, c, null))))
            .ToList());
    }

    Task<List<Row>> Query(SqlQuery q, CancellationToken ct) => starRocks.QueryAsync(q, r => new Row(
        r.IsDBNull(0) ? null : r.GetDateTime(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.GetInt64(2)), ct);

    static IEnumerable<TrendResult> BuildResults(Series series, List<Row> rows, Dictionary<string, long>? totals, List<DateTime> buckets, bool hasBreakdown)
    {
        var groups = rows.GroupBy(r => r.Breakdown).Select(g => (g.Key, Rows: g.ToList())).ToList();
        if (!hasBreakdown && groups.Count == 0) groups.Add((null, []));

        return groups.Select(g =>
        {
            var byBucket = g.Rows.Where(r => r.Bucket is not null).ToDictionary(r => r.Bucket!.Value, r => r.Count);
            var total = totals is null ? byBucket.Values.Sum() : totals.GetValueOrDefault(g.Key ?? "");
            return new TrendResult(series.Event, series.Metric ?? "total", g.Key, total,
                buckets.Select(b => new TrendPoint(TimeRange.Format(b), byBucket.GetValueOrDefault(b))).ToList());
        }).OrderByDescending(r => r.Total);
    }
}
