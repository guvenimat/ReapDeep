using System.Diagnostics;
using ReapDeep.QueryApi.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class TrendService(StarRocksClient starRocks)
{
    static readonly string[] Metrics = ["total", "unique_users"];

    sealed record SeriesPlan(Series Series, TrendSql.Plan Plan);
    sealed record Plan(string Interval, TimeRange Range, List<SeriesPlan> Series);
    readonly record struct Row(DateTime? Bucket, string? Breakdown, long Count);

    public Task<ExplainResponse> ExplainAsync(ProjectContext project, TrendRequest req, CancellationToken ct) =>
        starRocks.ExplainAsync(BuildPlan(project, req).Series.SelectMany(s => new[] { s.Plan.Buckets, s.Plan.Total }).OfType<SqlQuery>().ToList(), ct);

    public async Task<TrendResponse> RunAsync(ProjectContext project, TrendRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = BuildPlan(project, req);

        var bucketTasks = plan.Series.Select(s => Query(s.Plan.Buckets, ct)).ToList();
        var totalTasks = plan.Series.Select(s => s.Plan.Total is { } t ? Query(t, ct) : null).ToList();
        await Task.WhenAll(bucketTasks.Concat(totalTasks.OfType<Task<List<Row>>>()));

        var buckets = plan.Range.Buckets(plan.Interval);
        var results = new List<TrendResult>();
        for (var i = 0; i < plan.Series.Count; i++)
        {
            var totals = totalTasks[i]?.Result.ToDictionary(r => r.Breakdown ?? "", r => r.Count);
            results.AddRange(BuildResults(plan.Series[i].Series, bucketTasks[i].Result, totals, buckets, req.Breakdown is not null));
        }
        return new TrendResponse(plan.Interval, project.TimeZoneId, results, sw.Elapsed.TotalMilliseconds);
    }

    static Plan BuildPlan(ProjectContext project, TrendRequest req)
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

        SqlBuilder NewBuilder()
        {
            var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
            b.AddFilters(req.Filters);
            return b;
        }
        var series = requested
            .Select(s => new SeriesPlan(s, TrendSql.Build(NewBuilder, s, req.Deduplicate, interval, range, project.TimeZoneId, req.Breakdown)))
            .ToList();
        return new Plan(interval, range, series);
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
