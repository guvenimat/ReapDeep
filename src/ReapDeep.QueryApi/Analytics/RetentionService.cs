using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class RetentionService(StarRocksClient starRocks, CohortService cohortService)
{
    sealed record Plan(string Period, int Periods, TimeRange Cohorts, SqlQuery Query);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, RetentionRequest req, CancellationToken ct) =>
        await starRocks.ExplainAsync([(await BuildPlanAsync(project, req, ct)).Query], ct);

    public async Task<RetentionResponse> RunAsync(ProjectContext project, RetentionRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);
        var rows = await starRocks.QueryAsync(plan.Query,
            r => (Cohort: Convert.ToInt64(r.GetValue(0)), Period: Convert.ToInt64(r.GetValue(1)), Persons: r.GetInt64(2)), ct);

        var sizes = rows.Where(r => r.Period < 0).ToDictionary(r => r.Cohort, r => r.Persons);
        var counts = rows.Where(r => r.Period >= 0).ToDictionary(r => (r.Cohort, r.Period), r => r.Persons);
        var nowIndex = Index(plan.Period, TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, project.TimeZone));

        var cohorts = new List<RetentionCohort>();
        for (var c = Index(plan.Period, plan.Cohorts.FromLocal); c <= Index(plan.Period, plan.Cohorts.ToLocal.AddTicks(-1)); c++)
        {
            var cohort = c;
            var cells = Enumerable.Range(0, plan.Periods + 1)
                .Select(k => cohort + k > nowIndex ? (long?)null : counts.GetValueOrDefault((cohort, (long)k)))
                .ToList();
            cohorts.Add(new RetentionCohort(TimeRange.Format(Start(plan.Period, c)), sizes.GetValueOrDefault(c), cells));
        }

        return new RetentionResponse(plan.Period, plan.Periods, project.TimeZoneId, cohorts, WeightedAverage(cohorts, plan.Periods),
            sw.Elapsed.TotalMilliseconds);
    }

    async Task<Plan> BuildPlanAsync(ProjectContext project, RetentionRequest req, CancellationToken ct)
    {
        var startEvent = Ensure.NotBlank(req.StartEvent, "start_event");
        var returnEvent = string.IsNullOrWhiteSpace(req.ReturnEvent) ? startEvent : req.ReturnEvent.Trim();
        var period = Ensure.OneOf(req.Period ?? "week", "period", "day", "week");
        var periods = Ensure.InRange(req.Periods ?? (period == "week" ? 8 : 14), 1, 30, "periods");

        var cohorts = TimeRange.Parse(req.DateFrom, req.DateTo, project.TimeZone, period == "week" ? TimeSpan.FromDays(56) : TimeSpan.FromDays(14));
        var step = period == "week" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
        var activityEnd = Min(cohorts.ToUtc + step * (periods + 1), DateTime.UtcNow.AddMinutes(1));
        var activity = activityEnd > cohorts.FromUtc
            ? TimeRange.Parse(cohorts.FromUtc.ToString("o"), activityEnd.ToString("o"), project.TimeZone, TimeSpan.Zero)
            : cohorts;

        var b = new SqlBuilder(project.ProjectId, activity.FromUtc, activity.ToUtc);
        b.AddFilters(req.Filters, await cohortService.ResolveAsync(project, req.Filters, null, ct));
        var query = RetentionSql.Build(b, startEvent, returnEvent, period, periods, activity, project.TimeZoneId, cohorts.ToUtc);
        return new Plan(period, periods, cohorts, query);
    }

    static List<double?> WeightedAverage(List<RetentionCohort> cohorts, int periods) =>
        Enumerable.Range(0, periods + 1).Select(k =>
        {
            var eligible = cohorts.Where(c => c.Size > 0 && c.Counts[k] is not null).ToList();
            var size = eligible.Sum(c => c.Size);
            return size == 0 ? (double?)null : Math.Round(eligible.Sum(c => (double)c.Counts[k]!.Value) / size, 4);
        }).ToList();

    static long Index(string period, DateTime local)
    {
        var days = (long)Math.Floor((local.Date - RetentionSql.IndexEpoch).TotalDays);
        return period == "week" ? (long)Math.Floor(days / 7.0) : days;
    }

    static DateTime Start(string period, long index) =>
        RetentionSql.IndexEpoch.AddDays(period == "week" ? index * 7 : index);

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
