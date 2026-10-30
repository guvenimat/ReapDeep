using ReapDeep.Analytics;
using ReapDeep.Analytics.Cohorts;
using ReapDeep.Analytics.Rfm;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.UnitTests;

public class RfmLifecycleTests
{
    const long Project = 42;
    static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    static CohortCondition Rfm(params string[] segments) => new(ConditionTypes.RfmSegment, Segments: segments.ToList());

    static CohortDefinition Def(params CohortCondition[] conditions) => new([new CohortGroup(conditions.ToList())]);

    [Fact]
    public void Every_cell_of_the_grid_maps_to_one_of_the_eleven_segments_and_every_segment_is_reachable()
    {
        var cells = RfmSegments.Keys.SelectMany(RfmSegments.Cells).ToList();

        Assert.Equal(11, RfmSegments.Keys.Length);
        Assert.Equal(25, cells.Count);
        Assert.Equal(25, cells.Distinct().Count());
        Assert.All(RfmSegments.Keys, k => Assert.NotEmpty(RfmSegments.Cells(k)));
    }

    [Theory]
    [InlineData(5, 5, RfmSegments.Champions)]
    [InlineData(5, 1, RfmSegments.NewCustomers)]
    [InlineData(4, 1, RfmSegments.Promising)]
    [InlineData(4, 3, RfmSegments.PotentialLoyalist)]
    [InlineData(3, 5, RfmSegments.Loyal)]
    [InlineData(3, 3, RfmSegments.NeedAttention)]
    [InlineData(3, 1, RfmSegments.AboutToSleep)]
    [InlineData(2, 4, RfmSegments.AtRisk)]
    [InlineData(1, 5, RfmSegments.CantLose)]
    [InlineData(2, 2, RfmSegments.Hibernating)]
    [InlineData(1, 1, RfmSegments.Lost)]
    public void Scores_map_to_the_standard_segments(int r, int f, string segment) => Assert.Equal(segment, RfmSegments.Of(r, f));

    [Fact]
    public void Report_counts_orders_per_person_after_the_distinct_id_aggregation()
    {
        var b = new SqlBuilder(Project, Now.AddDays(-365), Now);
        var q = RfmSql.Report(b, "purchase", "amount", Now);

        Assert.Matches(@"e\.project_id = @p\d+ AND e\.timestamp >= @p\d+ AND e\.timestamp < @p\d+ AND e\.event = @p\d+ GROUP BY 1, 2, 3", q.Sql);
        Assert.Contains("COALESCE(NULLIF(get_json_string(e.properties, @p", q.Sql);
        Assert.Contains("e.event_id) AS ok", q.Sql);
        Assert.Contains("LEFT JOIN analytics.person_distinct_ids pdi ON o.project_id = pdi.project_id AND o.distinct_id = pdi.distinct_id", q.Sql);
        Assert.Contains("COUNT(DISTINCT o.ok) AS f", q.Sql);
        Assert.DoesNotContain("INNER JOIN", q.Sql);
        Assert.DoesNotContain("NTILE", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is string s && s == "$.\"order_id\"");
        Assert.Contains(q.Parameters, p => p.Value is string s && s == "$.\"amount\"");
    }

    [Fact]
    public void Ties_share_a_score_because_scores_come_from_rank_not_ntile()
    {
        var q = RfmSql.Report(new SqlBuilder(Project, Now.AddDays(-90), Now), "purchase", "amount", Now);

        Assert.Contains("LEAST(5, 1 + FLOOR(5 * (RANK() OVER (ORDER BY pp.f) - 1) / COUNT(*) OVER ())) AS fs", q.Sql);
        Assert.Contains("LEAST(5, 1 + FLOOR(5 * (RANK() OVER (ORDER BY pp.last_ts) - 1) / COUNT(*) OVER ())) AS r", q.Sql);
    }

    [Fact]
    public void Cohort_filter_on_the_report_joins_after_the_order_aggregation()
    {
        var b = new SqlBuilder(Project, Now.AddDays(-365), Now);
        b.InCohort(new CohortRef(7, "VIP", 3));
        var q = RfmSql.Report(b, "purchase", "amount", Now);

        Assert.Contains("GROUP BY 1, 2, 3) a LEFT SEMI JOIN analytics.cohort_members cm0 ON cm0.project_id = a.project_id", q.Sql);
    }

    [Fact]
    public void Rfm_condition_defaults_to_purchase_and_a_year_and_rejects_unknown_segments_and_windows()
    {
        var normalized = CohortRules.Normalize(Def(Rfm(RfmSegments.Champions, RfmSegments.Champions)));
        var c = normalized.Groups![0].Conditions![0];

        Assert.Equal("purchase", c.Event);
        Assert.Equal(365, c.WithinDays);
        Assert.Equal([RfmSegments.Champions], c.Segments);
        Assert.Throws<ValidationException>(() => CohortRules.Normalize(Def(Rfm("vip"))));
        Assert.Throws<ValidationException>(() => CohortRules.Normalize(Def(Rfm())));
        Assert.Throws<ValidationException>(() => CohortRules.Normalize(Def(Rfm(RfmSegments.Lost) with { WithinDays = 30 })));
    }

    [Fact]
    public void Rfm_condition_selects_the_cells_of_its_segments()
    {
        var q = CohortSql.Count(Project, Def(Rfm(RfmSegments.Champions, RfmSegments.Lost)), Now);

        Assert.Contains("WHERE s.r * 10 + s.fs IN (11, 54, 55)", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is DateTime t && t == Now.AddDays(-365));
    }

    [Fact]
    public void Rfm_condition_can_be_the_only_positive_condition_next_to_a_negation()
    {
        var def = Def(Rfm(RfmSegments.AtRisk), new CohortCondition(ConditionTypes.NotPerformed, Event: "purchase", WithinDays: 30));
        var q = CohortSql.Count(Project, def, Now);

        Assert.Contains(") r LEFT JOIN (SELECT COALESCE(pdi.person_id, a.distinct_id) AS person", q.Sql);
        Assert.Contains("x ON x.person = r.person WHERE COALESCE(x.c0, 0) = 0", q.Sql);
        Assert.DoesNotContain("FROM analytics.persons p LEFT JOIN", q.Sql);
    }

    [Fact]
    public void Two_rfm_conditions_in_a_group_intersect()
    {
        var def = Def(Rfm(RfmSegments.Champions), Rfm(RfmSegments.Champions) with { WithinDays = 90 });
        var q = CohortSql.Count(Project, def, Now);

        Assert.Contains(") g LEFT SEMI JOIN (SELECT s.person", q.Sql);
    }

    [Theory]
    [InlineData("day", "2026-10-30", "2026-10-30")]
    [InlineData("week", "2026-10-29", "2026-10-26")]
    [InlineData("month", "2026-10-29", "2026-10-01")]
    public void Period_index_and_start_round_trip(string period, string day, string start)
    {
        var index = LifecycleSql.Index(period, DateTime.Parse(day));

        Assert.Equal(DateTime.Parse(start), LifecycleSql.Start(period, index));
        Assert.Equal(index - 1, LifecycleSql.Index(period, LifecycleSql.Start(period, index).AddDays(-1)));
    }

    [Fact]
    public void Lifecycle_classifies_with_window_functions_over_distinct_person_periods()
    {
        var range = TimeRange.Between(Now.AddDays(-120), Now, TimeZoneInfo.Utc);
        var q = LifecycleSql.Build(new SqlBuilder(Project, range.FromUtc, range.ToUtc), null, "week", range, "UTC", 2900, 2904);

        Assert.Contains("SELECT DISTINCT COALESCE(pdi.person_id, d.distinct_id) AS person, d.p", q.Sql);
        Assert.Contains("LAG(a.p) OVER (PARTITION BY a.person ORDER BY a.p) AS prev_p", q.Sql);
        Assert.Contains("WHEN x.prev_p = x.p - 1 THEN 'returning'", q.Sql);
        Assert.Contains("SELECT x.p + 1, 'dormant'", q.Sql);
        Assert.DoesNotContain("e.event =", q.Sql);
    }

    [Fact]
    public void Lifecycle_month_index_is_year_times_twelve_plus_month()
    {
        var range = TimeRange.Between(Now.AddDays(-60), Now, TimeZoneInfo.Utc);
        var q = LifecycleSql.Build(new SqlBuilder(Project, range.FromUtc, range.ToUtc), "purchase", "month", range, "UTC", 1, 2);

        Assert.Contains("CAST(year(seconds_add(e.timestamp, 0)) * 12 + month(seconds_add(e.timestamp, 0)) - 1 AS BIGINT) AS p", q.Sql);
        Assert.Matches(@"e\.event = @p\d+", q.Sql);
    }
}
