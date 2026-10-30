using System.Diagnostics;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Rfm;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics;

public sealed class RfmService(StarRocksClient starRocks, CohortService cohorts)
{
    const int MaxCurrencies = 10;

    sealed record Plan(int WindowDays, string Event, string Amount, DateTime NowUtc, SqlQuery Report, SqlQuery Currencies);
    readonly record struct Cell(int R, int F, long Persons, long SumRecency, long MinRecency, long MaxRecency,
        long SumFrequency, long MinFrequency, long MaxFrequency, double Revenue);

    public async Task<ExplainResponse> ExplainAsync(ProjectContext project, RfmRequest req, CancellationToken ct) =>
        await starRocks.ExplainAsync([(await BuildPlanAsync(project, req, ct)).Report], ct);

    public async Task<RfmResponse> RunAsync(ProjectContext project, RfmRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var plan = await BuildPlanAsync(project, req, ct);
        var cellsTask = starRocks.QueryAsync(plan.Report, r => new Cell(
            Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)),
            Convert.ToInt64(r.GetValue(3)), Convert.ToInt64(r.GetValue(4)), Convert.ToInt64(r.GetValue(5)),
            Convert.ToInt64(r.GetValue(6)), Convert.ToInt64(r.GetValue(7)), Convert.ToInt64(r.GetValue(8)),
            Convert.ToDouble(r.GetValue(9))), ct);
        var currenciesTask = starRocks.QueryAsync(plan.Currencies, r => r.GetString(0), ct);
        await Task.WhenAll(cellsTask, currenciesTask);

        var cells = cellsTask.Result;
        var customers = cells.Sum(c => c.Persons);
        var revenue = Math.Round(cells.Sum(c => c.Revenue), 2);
        return new RfmResponse(plan.WindowDays, plan.Event, plan.Amount, plan.NowUtc, customers, revenue, currenciesTask.Result,
            Grid(cells), Segments(cells, customers, revenue), Thresholds(cells), sw.Elapsed.TotalMilliseconds);
    }

    public static readonly int[] MigrationPeriods = [7, 30, 90];
    public const string Outside = "none";

    public async Task<RfmMigrationResponse> MigrationAsync(ProjectContext project, RfmMigrationRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var window = Window(req.WindowDays);
        var period = req.PeriodDays ?? 30;
        Ensure.That(MigrationPeriods.Contains(period), $"period_days: {string.Join(" | ", MigrationPeriods)}");
        var now = DateTime.UtcNow;
        var past = now.AddDays(-period);
        var rows = await starRocks.QueryAsync(RfmSql.Migration(project.ProjectId, Event(req.PurchaseEvent), window, now, past), r => new RfmMigrationRow(
            Segment(r, 0), Segment(r, 2), Convert.ToInt64(r.GetValue(4))), ct);
        var merged = rows.GroupBy(x => (x.From, x.To)).Select(g => new RfmMigrationRow(g.Key.From, g.Key.To, g.Sum(x => x.Persons)))
            .OrderByDescending(x => x.Persons).ToList();
        return new RfmMigrationResponse(window, period, past, now, merged, sw.Elapsed.TotalMilliseconds);
    }

    public async Task<RfmPersonResponse> PersonAsync(ProjectContext project, string person, int? windowDays, string? purchaseEvent, CancellationToken ct)
    {
        var window = Window(windowDays);
        var query = RfmSql.Person(project.ProjectId, Event(purchaseEvent), RfmSql.DefaultAmountProperty, window, DateTime.UtcNow, Ensure.NotBlank(person, "person_id"));
        var rows = await starRocks.QueryAsync(query, r => (R: Convert.ToInt32(r.GetValue(0)), F: Convert.ToInt32(r.GetValue(1)),
            Recency: Convert.ToInt64(r.GetValue(2)), Orders: Convert.ToInt64(r.GetValue(3)), Monetary: Math.Round(Convert.ToDouble(r.GetValue(4)), 2)), ct);
        if (rows.Count == 0) return new RfmPersonResponse(window, null, null, null, null, null, null, null);
        var x = rows[0];
        var segment = RfmSegments.Of(x.R, x.F);
        return new RfmPersonResponse(window, segment, RfmSegments.All.First(s => s.Key == segment).Name, x.R, x.F, x.Recency, x.Orders, x.Monetary);
    }

    static string Segment(MySqlConnector.MySqlDataReader r, int i) =>
        r.IsDBNull(i) ? Outside : RfmSegments.Of(Convert.ToInt32(r.GetValue(i)), Convert.ToInt32(r.GetValue(i + 1)));

    static int Window(int? requested)
    {
        var window = requested ?? RfmSql.WindowDays[^1];
        Ensure.That(RfmSql.WindowDays.Contains(window), $"window_days: {string.Join(" | ", RfmSql.WindowDays)}");
        return window;
    }

    static string Event(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? RfmSql.DefaultEvent : Ensure.NotBlank(requested, "purchase_event");

    async Task<Plan> BuildPlanAsync(ProjectContext project, RfmRequest req, CancellationToken ct)
    {
        var window = Window(req.WindowDays);
        var purchaseEvent = Event(req.PurchaseEvent);
        var amount = ColumnMapper.Validate(string.IsNullOrWhiteSpace(req.AmountProperty) ? RfmSql.DefaultAmountProperty : req.AmountProperty.Trim());
        var now = DateTime.UtcNow;
        var refs = await cohorts.ResolveAsync(project, req.Filters, null, ct);

        SqlBuilder NewBuilder()
        {
            var b = new SqlBuilder(project.ProjectId, now.AddDays(-window), now);
            b.AddFilters(req.Filters, refs);
            return b;
        }

        return new Plan(window, purchaseEvent, amount, now, RfmSql.Report(NewBuilder(), purchaseEvent, amount, now),
            RfmSql.Currencies(NewBuilder(), purchaseEvent, MaxCurrencies + 1));
    }

    static List<RfmCell> Grid(List<Cell> cells)
    {
        var byScore = cells.ToDictionary(c => (c.R, c.F), c => c.Persons);
        return (from r in Enumerable.Range(1, 5)
                from f in Enumerable.Range(1, 5)
                select new RfmCell(r, f, byScore.GetValueOrDefault((r, f)), RfmSegments.Of(r, f))).ToList();
    }

    static List<RfmSegmentResult> Segments(List<Cell> cells, long customers, double revenue) =>
        RfmSegments.All.Select(s =>
        {
            var members = cells.Where(c => RfmSegments.Of(c.R, c.F) == s.Key).ToList();
            var persons = members.Sum(c => c.Persons);
            var segmentRevenue = Math.Round(members.Sum(c => c.Revenue), 2);
            double? Avg(Func<Cell, double> sum) => persons == 0 ? null : Math.Round(members.Sum(sum) / persons, 2);
            return new RfmSegmentResult(s.Key, s.Name, s.Description, persons, Ratio(persons, customers),
                Avg(c => c.SumRecency), Avg(c => c.SumFrequency), Avg(c => c.Revenue), segmentRevenue, Ratio(segmentRevenue, revenue));
        }).ToList();

    static RfmThresholds Thresholds(List<Cell> cells)
    {
        List<RfmScoreRange> Ranges(Func<Cell, int> score, Func<Cell, long> min, Func<Cell, long> max) =>
            Enumerable.Range(1, 5).Select(s =>
            {
                var members = cells.Where(c => score(c) == s && c.Persons > 0).ToList();
                return members.Count == 0 ? new RfmScoreRange(s, null, null) : new RfmScoreRange(s, members.Min(min), members.Max(max));
            }).ToList();

        return new RfmThresholds(Ranges(c => c.R, c => c.MinRecency, c => c.MaxRecency), Ranges(c => c.F, c => c.MinFrequency, c => c.MaxFrequency));
    }

    static double Ratio(double part, double total) => total <= 0 ? 0 : Math.Round(part / total, 4);
}
