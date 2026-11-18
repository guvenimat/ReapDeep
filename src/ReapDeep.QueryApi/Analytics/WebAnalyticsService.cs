using System.Diagnostics;
using MySqlConnector;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class WebAnalyticsService(StarRocksClient starRocks, CohortService cohorts)
{
    sealed record Plan(string Interval, TimeRange Range, TimeRange Previous, SqlQuery Overview, SqlQuery Series, SqlQuery Pages, SqlQuery Sessions);
    readonly record struct BreakdownRow(string Dimension, string? Value, long Sessions, long Visitors, long Bounces, long Duration);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, WebAnalyticsRequest req, CancellationToken ct)
    {
        var plan = await BuildPlanAsync(project, req, ct);
        return await starRocks.ExplainAsync([plan.Overview, plan.Series, plan.Pages, plan.Sessions], ct);
    }

    public async Task<WebAnalyticsResponse> RunAsync(ProjectContext project, WebAnalyticsRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);

        using var gate = StarRocksClient.RequestGate();
        var overview = starRocks.QueryAsync(plan.Overview,
            r => (Current: Long(r, 0) == 1, Totals: new WebTotals(Long(r, 1), Long(r, 2), Long(r, 3), Long(r, 4), Long(r, 5))), gate, ct);
        var series = starRocks.QueryAsync(plan.Series, r => (Bucket: r.GetDateTime(0), Visitors: Long(r, 1), Pageviews: Long(r, 2)), gate, ct);
        var pages = starRocks.QueryAsync(plan.Pages, r => new WebPage(Text(r, 0), Long(r, 1), Long(r, 2)), gate, ct);
        var sessions = starRocks.QueryAsync(plan.Sessions,
            r => new BreakdownRow(r.GetString(0), Text(r, 1), Long(r, 2), Long(r, 3), Long(r, 4), Long(r, 5)), gate, ct);
        await Task.WhenAll(overview, series, pages, sessions);

        var byBucket = series.Result.ToDictionary(p => p.Bucket);
        var points = plan.Range.Buckets(plan.Interval).Select(b => byBucket.TryGetValue(b, out var p)
            ? new WebPoint(TimeRange.Format(b), p.Visitors, p.Pageviews)
            : new WebPoint(TimeRange.Format(b), 0, 0)).ToList();
        var breakdowns = WebSql.Dimensions.Select(d => new WebBreakdown(d, sessions.Result
            .Where(r => r.Dimension == d)
            .OrderByDescending(r => r.Sessions).ThenBy(r => r.Value, StringComparer.Ordinal)
            .Select(r => new WebBreakdownRow(r.Value, r.Sessions, r.Visitors, Rate(r.Bounces, r.Sessions), Average(r.Duration, r.Sessions)))
            .ToList())).ToList();

        return new WebAnalyticsResponse(plan.Interval, project.TimeZoneId, TimeRange.Format(plan.Range.FromLocal), TimeRange.Format(plan.Range.ToLocal),
            TimeRange.Format(plan.Previous.FromLocal),
            Summary(WebTotals.Sum(overview.Result.Where(r => r.Current).Select(r => r.Totals))),
            Summary(WebTotals.Sum(overview.Result.Where(r => !r.Current).Select(r => r.Totals))),
            points, pages.Result, breakdowns, sw.Elapsed.TotalMilliseconds);
    }

    async Task<Plan> BuildPlanAsync(ProjectContext project, WebAnalyticsRequest req, CancellationToken ct)
    {
        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(7));
        var interval = req.Interval is null ? WebSql.DefaultInterval(range) : Ensure.OneOf(req.Interval, "interval", TimeRange.Intervals);
        var previous = TimeRange.Between(range.FromUtc - (range.ToUtc - range.FromUtc), range.FromUtc, project.TimeZone);
        var limit = WebSql.Limit(req.Limit);
        var refs = await cohorts.ResolveAsync(project, req.Filters, null, ct);

        SqlBuilder Builder(DateTime fromUtc, DateTime toUtc)
        {
            var b = new SqlBuilder(project.ProjectId, fromUtc, toUtc);
            b.AddFilters(req.Filters, refs);
            return b;
        }

        return new Plan(interval, range, previous,
            WebSql.Overview(Builder(previous.FromUtc, range.ToUtc), range.FromUtc),
            WebSql.Series(Builder(range.FromUtc, range.ToUtc), interval, range, project.TimeZoneId),
            WebSql.Pages(Builder(range.FromUtc, range.ToUtc), limit),
            WebSql.Sessions(Builder(range.FromUtc, range.ToUtc), limit));
    }

    static WebSummary Summary(WebTotals t) => new(t.Visitors, t.Pageviews, t.Sessions, t.Bounces, t.BounceRate, t.AvgSessionSeconds);

    static double Rate(long part, long whole) => whole == 0 ? 0 : Math.Round((double)part / whole, 4);

    static double Average(long sum, long count) => count == 0 ? 0 : Math.Round((double)sum / count, 1);

    static long Long(MySqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));

    static string? Text(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
