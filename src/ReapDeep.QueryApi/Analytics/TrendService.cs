using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class TrendService(StarRocksClient starRocks, CohortService cohorts)
{
    public const string Previous = "previous";
    public const string FormulaMetric = "formula";

    sealed record SeriesPlan(Series Series, int Index, string? Cohort, bool IsPrevious, TrendSql.Plan Plan);
    sealed record Plan(string Interval, TimeRange Range, TimeRange? PreviousRange, List<SeriesPlan> Series, TrendFormula? Formula);
    readonly record struct Row(DateTime? Bucket, string? Breakdown, double Value);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, TrendRequest req, CancellationToken ct) =>
        await starRocks.ExplainAsync((await BuildPlanAsync(project, req, ct)).Series.Where(s => !s.IsPrevious)
            .SelectMany(s => new[] { s.Plan.Buckets, s.Plan.Total }).OfType<SqlQuery>().ToList(), ct);

    public async Task<TrendResponse> RunAsync(ProjectContext project, TrendRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);

        var bucketTasks = plan.Series.Select(s => Query(s.Plan.Buckets, ct)).ToList();
        var totalTasks = plan.Series.Select(s => s.Plan.Total is { } t ? Query(t, ct) : null).ToList();
        await Task.WhenAll(bucketTasks.Concat(totalTasks.OfType<Task<List<Row>>>()));

        var buckets = plan.Range.Buckets(plan.Interval);
        var previousBuckets = plan.PreviousRange is { } prev ? Align(prev.Buckets(plan.Interval), buckets.Count, plan.Interval) : [];
        var results = new List<TrendResult>();
        for (var i = 0; i < plan.Series.Count; i++)
        {
            var sp = plan.Series[i];
            var totals = totalTasks[i]?.Result.ToDictionary(r => r.Breakdown ?? "", r => r.Value);
            var series = BuildResults(sp.Series, bucketTasks[i].Result, totals, sp.IsPrevious ? previousBuckets : buckets,
                req.Breakdown is not null && sp.Cohort is null);
            results.AddRange(series.Select(r => r with
            {
                BreakdownValue = sp.Cohort ?? r.BreakdownValue,
                Series = Letter(sp.Index),
                Compare = sp.IsPrevious ? Previous : null,
            }));
        }
        if (plan.Formula is not null) results.AddRange(ApplyFormula(plan.Formula, results, req.Series!.Count));
        return new TrendResponse(plan.Interval, project.TimeZoneId, results, sw.Elapsed.TotalMilliseconds);
    }

    async Task<Plan> BuildPlanAsync(ProjectContext project, TrendRequest req, CancellationToken ct)
    {
        var requested = req.Series ?? [];
        Ensure.That(requested.Count is > 0 and <= 10, "series must contain 1-10 items");
        foreach (var s in requested)
        {
            Ensure.That(!string.IsNullOrEmpty(s.Event), "Each series requires an event");
            Ensure.That(s.Metric is null || TrendSql.Metrics.Contains(s.Metric), $"Invalid metric: '{s.Metric}' ({string.Join(" | ", TrendSql.Metrics)})");
            if (TrendSql.PropertyMetrics.Contains(s.Metric))
                Ensure.That(!string.IsNullOrWhiteSpace(s.Property), $"Metric '{s.Metric}' needs a numeric event property");
        }
        var formula = string.IsNullOrWhiteSpace(req.Formula) ? null : TrendFormula.Parse(req.Formula, requested.Count);
        var interval = Ensure.OneOf(req.Interval ?? "day", "interval", TimeRange.Intervals);
        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(7));
        TimeRange? previous = req.Compare ? TimeRange.Between(range.FromUtc - (range.ToUtc - range.FromUtc), range.FromUtc, project.TimeZone) : null;
        var refs = await cohorts.ResolveAsync(project, req.Filters, req.Breakdown, ct);

        SqlBuilder NewBuilder(TimeRange r, CohortRef? cohort)
        {
            var b = new SqlBuilder(project.ProjectId, r.FromUtc, r.ToUtc);
            b.AddFilters(req.Filters, refs);
            if (cohort is not null) b.InCohort(cohort);
            return b;
        }
        IEnumerable<SeriesPlan> Build(Series s, int index, CohortRef? cohort, Breakdown? bd)
        {
            yield return new SeriesPlan(s, index, cohort?.Name, false,
                TrendSql.Build(() => NewBuilder(range, cohort), s, req.Deduplicate, interval, range, project.TimeZoneId, bd));
            if (previous is { } p)
                yield return new SeriesPlan(s, index, cohort?.Name, true,
                    TrendSql.Build(() => NewBuilder(p, cohort), s, req.Deduplicate, interval, p, project.TimeZoneId, bd));
        }

        if (req.Breakdown?.Scope != "cohort")
            return new Plan(interval, range, previous, requested.SelectMany((s, i) => Build(s, i, null, req.Breakdown)).ToList(), formula);

        var breakdownCohorts = req.Breakdown.CohortIds!.Distinct().Select(id => refs[id]).ToList();
        Ensure.That(requested.Count * breakdownCohorts.Count <= 10, "series x cohorts can be at most 10");
        return new Plan(interval, range, previous, requested
            .SelectMany((s, i) => breakdownCohorts.SelectMany(c => Build(s, i, c, null)))
            .ToList(), formula);
    }

    Task<List<Row>> Query(SqlQuery q, CancellationToken ct) => starRocks.QueryAsync(q, r => new Row(
        r.IsDBNull(0) ? null : r.GetDateTime(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.IsDBNull(2) ? 0 : Math.Round(Convert.ToDouble(r.GetValue(2)), 4)), ct);

    static string Letter(int index) => ((char)('A' + index)).ToString();

    static List<DateTime> Align(List<DateTime> previous, int count, string interval)
    {
        var aligned = previous.Take(count).ToList();
        while (aligned.Count < count) aligned.Add(aligned.Count == 0 ? DateTime.MinValue : TimeRange.Next(aligned[^1], interval));
        return aligned;
    }

    static IEnumerable<TrendResult> ApplyFormula(TrendFormula formula, List<TrendResult> results, int seriesCount)
    {
        foreach (var group in results.GroupBy(r => (r.Compare, r.BreakdownValue)))
        {
            var bySeries = group.ToDictionary(r => r.Series!);
            var values = new double[seriesCount];
            double Eval(Func<TrendResult, double> pick)
            {
                for (var k = 0; k < seriesCount; k++) values[k] = bySeries.TryGetValue(Letter(k), out var r) ? pick(r) : 0;
                return formula.Evaluate(values);
            }
            var data = group.First().Data
                .Select((p, j) => new TrendPoint(p.Bucket, Eval(r => j < r.Data.Count ? r.Data[j].Count : 0)))
                .ToList();
            yield return new TrendResult(formula.Text, FormulaMetric, group.Key.BreakdownValue, Eval(r => r.Total), data,
                Compare: group.Key.Compare, Series: FormulaMetric);
        }
    }

    static IEnumerable<TrendResult> BuildResults(Series series, List<Row> rows, Dictionary<string, double>? totals, List<DateTime> buckets, bool hasBreakdown)
    {
        var groups = rows.GroupBy(r => r.Breakdown).Select(g => (g.Key, Rows: g.ToList())).ToList();
        if (!hasBreakdown && groups.Count == 0) groups.Add((null, []));

        return groups.Select(g =>
        {
            var byBucket = g.Rows.Where(r => r.Bucket is not null).ToDictionary(r => r.Bucket!.Value, r => r.Value);
            var total = totals is null ? Math.Round(byBucket.Values.Sum(), 4) : totals.GetValueOrDefault(g.Key ?? "");
            return new TrendResult(series.Event, series.Metric ?? "total", g.Key, total,
                buckets.Select(b => new TrendPoint(TimeRange.Format(b), byBucket.GetValueOrDefault(b))).ToList(), series.Property);
        }).OrderByDescending(r => r.Total);
    }
}
