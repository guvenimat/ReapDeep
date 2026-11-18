using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Actions;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed record StickinessRequest(string? Event, string? Action, string? Interval, string? DateFrom, string? DateTo, List<Filter>? Filters, bool Explain);
public sealed record StickinessBucket(int Periods, long Persons, double Share);
public sealed record StickinessResponse(string Event, string Interval, int TotalPeriods, long Persons, double? AveragePeriods, string Timezone,
    List<StickinessBucket> Buckets, double TookMs);

public sealed class StickinessService(StarRocksClient starRocks, CohortService cohorts, ActionRepository actions)
{
    const int MaxPeriods = 92;

    sealed record Plan(string Event, string Interval, int TotalPeriods, SqlQuery Query);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, StickinessRequest req, CancellationToken ct) =>
        await starRocks.ExplainAsync([(await BuildPlanAsync(project, req, ct)).Query], ct);

    public async Task<StickinessResponse> RunAsync(ProjectContext project, StickinessRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);
        var rows = await starRocks.QueryAsync(plan.Query, r => (Periods: Convert.ToInt32(r.GetValue(0)), Persons: Convert.ToInt64(r.GetValue(1))), ct);
        var byPeriods = rows.ToDictionary(r => r.Periods, r => r.Persons);
        var total = rows.Sum(r => r.Persons);
        var buckets = Enumerable.Range(1, plan.TotalPeriods)
            .Select(k => new StickinessBucket(k, byPeriods.GetValueOrDefault(k), total == 0 ? 0 : Math.Round(byPeriods.GetValueOrDefault(k) / (double)total, 4)))
            .ToList();
        double? average = total == 0 ? null : Math.Round(rows.Sum(r => (double)r.Periods * r.Persons) / total, 2);
        return new StickinessResponse(plan.Event, plan.Interval, plan.TotalPeriods, total, average, project.TimeZoneId, buckets, sw.Elapsed.TotalMilliseconds);
    }

    async Task<Plan> BuildPlanAsync(ProjectContext project, StickinessRequest req, CancellationToken ct)
    {
        Ensure.That(!string.IsNullOrWhiteSpace(req.Event) || !string.IsNullOrWhiteSpace(req.Action), "event or action is required");
        var interval = Ensure.OneOf(req.Interval ?? "day", "interval", StickinessSql.Intervals);
        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, TimeSpan.FromDays(interval == "day" ? 30 : 84));
        var first = LifecycleSql.Index(interval, TimeRange.Truncate(range.FromLocal, interval));
        var last = LifecycleSql.Index(interval, range.ToLocal.AddTicks(-1));
        var total = (int)(last - first + 1);
        Ensure.That(total is >= 1 and <= MaxPeriods, $"The range must cover 1-{MaxPeriods} {interval}s");

        var series = (await actions.ApplyAsync(project.ProjectId, [new Series(req.Event ?? "", null, null, req.Action, null)], ct))[0];
        Ensure.NotBlank(series.Event, "event", 200);
        var b = new SqlBuilder(project.ProjectId, range.FromUtc, range.ToUtc);
        b.AddFilters(req.Filters, await cohorts.ResolveAsync(project, req.Filters, null, ct));
        return new Plan(series.Event, interval, total, StickinessSql.Build(b, series.Event, series.Events, interval, range, project.TimeZoneId));
    }
}
