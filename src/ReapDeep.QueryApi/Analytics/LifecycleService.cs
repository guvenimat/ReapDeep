using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class LifecycleService(StarRocksClient starRocks, CohortService cohorts)
{
    public const int DefaultLookbackDays = 90;
    const int MinLookbackDays = 7;
    const int MaxLookbackDays = 365;

    sealed record Plan(string? Event, string Period, int LookbackDays, long First, long Last, SqlQuery Query);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, LifecycleRequest req, CancellationToken ct) =>
        await starRocks.ExplainAsync([(await BuildPlanAsync(project, req, ct)).Query], ct);

    public async Task<LifecycleResponse> RunAsync(ProjectContext project, LifecycleRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);
        var rows = await starRocks.QueryAsync(plan.Query,
            r => (Period: Convert.ToInt64(r.GetValue(0)), Status: r.GetString(1), Persons: Convert.ToInt64(r.GetValue(2))), ct);
        var counts = rows.ToDictionary(r => (r.Period, r.Status), r => r.Persons);

        var periods = new List<LifecyclePeriod>();
        for (var p = plan.First; p <= plan.Last; p++)
            periods.Add(new LifecyclePeriod(TimeRange.Format(LifecycleSql.Start(plan.Period, p)),
                counts.GetValueOrDefault((p, LifecycleSql.New)), counts.GetValueOrDefault((p, LifecycleSql.Returning)),
                counts.GetValueOrDefault((p, LifecycleSql.Resurrecting)), counts.GetValueOrDefault((p, LifecycleSql.Dormant))));

        return new LifecycleResponse(plan.Event, plan.Period, plan.LookbackDays, project.TimeZoneId, periods, sw.Elapsed.TotalMilliseconds);
    }

    async Task<Plan> BuildPlanAsync(ProjectContext project, LifecycleRequest req, CancellationToken ct)
    {
        var eventName = string.IsNullOrWhiteSpace(req.Event) ? null : Ensure.NotBlank(req.Event, "event");
        var period = Ensure.OneOf(req.Period ?? "week", "period", LifecycleSql.Periods);
        var lookback = Ensure.InRange(req.LookbackDays ?? DefaultLookbackDays, MinLookbackDays, MaxLookbackDays, "lookback_days");
        var defaultSpan = period switch { "day" => TimeSpan.FromDays(14), "week" => TimeSpan.FromDays(56), _ => TimeSpan.FromDays(180) };
        var range = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, defaultSpan);

        var firstLocal = TimeRange.Truncate(range.FromLocal, period);
        var first = LifecycleSql.Index(period, firstLocal);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, project.TimeZone);
        var last = Math.Min(LifecycleSql.Index(period, range.ToLocal.AddTicks(-1)), LifecycleSql.Index(period, nowLocal));
        Ensure.That(last >= first, "The date range has no completed or current period");
        Ensure.That(last - first < 400, "At most 400 periods; choose a larger period");

        var previous = LifecycleSql.Start(period, first - 1);
        var lookbackStart = firstLocal.AddDays(-lookback);
        var scanFromUtc = LocalToUtc(previous < lookbackStart ? previous : lookbackStart, project.TimeZone);
        var scanned = TimeRange.Between(scanFromUtc, range.ToUtc, project.TimeZone);

        var b = new SqlBuilder(project.ProjectId, scanned.FromUtc, scanned.ToUtc);
        b.AddFilters(req.Filters, await cohorts.ResolveAsync(project, req.Filters, null, ct));
        return new Plan(eventName, period, lookback, first, last, LifecycleSql.Build(b, eventName, period, scanned, project.TimeZoneId, first, last));
    }

    static DateTime LocalToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }
}
