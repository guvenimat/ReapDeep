using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.UnitTests;

public class ActionSqlTests
{
    static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime To = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
    static SqlBuilder Builder() => new(42, From, To);
    static TimeRange Range() => TimeRange.Parse("2026-10-01", "2026-10-07", TimeZoneInfo.Utc, TimeSpan.FromDays(7));

    [Theory]
    [InlineData("total")]
    [InlineData("unique_users")]
    [InlineData("sum")]
    public void A_trend_series_from_an_action_matches_any_of_its_events(string metric)
    {
        var series = new Series("Checkout", metric, metric == "sum" ? "amount" : null, "Checkout", ["add_to_cart", "begin_checkout"]);
        var q = TrendSql.Build(Builder, series, dedup: false, "day", Range(), "UTC", null).Buckets;

        Assert.Matches(@"e\.event IN \(@p\d+, @p\d+\)", q.Sql);
        Assert.Contains(q.Parameters, p => (p.Value as string) == "begin_checkout");
        Assert.DoesNotContain(q.Parameters, p => (p.Value as string) == "Checkout");
    }

    [Fact]
    public void A_plain_series_still_matches_one_event()
    {
        var q = TrendSql.Build(Builder, new Series("purchase", "total"), dedup: false, "day", Range(), "UTC", null).Buckets;

        Assert.Matches(@"e\.event = @p\d+", q.Sql);
        Assert.DoesNotContain("e.event IN", q.Sql);
    }

    [Fact]
    public void A_funnel_step_from_an_action_matches_any_of_its_events_and_the_scan_covers_all_of_them()
    {
        var q = FunnelSql.Build(Builder(), [new FunnelStep("Browse", null, "Browse", ["product_viewed", "category_viewed"]), new FunnelStep("purchase", null)],
            3600, null, 1);

        Assert.Matches(@"\(e\.event IN \(@p\d+, @p\d+\)\)", q.Sql);
        Assert.Matches(@"e\.event IN \(@p\d+, @p\d+, @p\d+\)", q.Sql);
        Assert.Equal(["product_viewed", "category_viewed", "purchase"], q.Parameters.Select(p => p.Value).OfType<string>().Where(v => v is "product_viewed" or "category_viewed" or "purchase").Distinct());
    }

    [Fact]
    public void Retention_matches_the_events_of_start_and_return_actions()
    {
        var q = RetentionSql.Build(Builder(), "Signed up", ["$identify"], "Bought", ["purchase", "order_completed"], "week", 6, Range(), "UTC", To);

        Assert.Matches(@"e\.event IN \(@p\d+, @p\d+, @p\d+\)", q.Sql);
        Assert.Matches(@"e\.event IN \(@p\d+\) AND e\.timestamp < @p\d+ AS is_start", q.Sql);
        Assert.Matches(@"e\.event IN \(@p\d+, @p\d+\) AS is_ret", q.Sql);
        Assert.DoesNotContain(q.Parameters, p => (p.Value as string) is "Signed up" or "Bought");
    }

    [Fact]
    public void Paths_start_from_any_event_of_an_action()
    {
        var q = PathSql.Steps(Builder(), "Browsing", ["product_viewed", "category_viewed"], 4, 3600, true);

        Assert.Matches(@"e\.event IN \(@p\d+, @p\d+\) AS is_start", q.Sql);
        Assert.DoesNotContain(q.Parameters, p => (p.Value as string) == "Browsing");
    }

    [Fact]
    public void Lifecycle_follows_the_events_of_an_action()
    {
        var q = LifecycleSql.Build(Builder(), "Buying", ["purchase", "order_completed"], "week", Range(), "UTC", 1, 2);

        Assert.Matches(@"e\.event IN \(@p\d+, @p\d+\)", q.Sql);
        Assert.DoesNotContain(q.Parameters, p => (p.Value as string) == "Buying");
    }

    [Fact]
    public void Stickiness_counts_active_periods_per_person_with_tenant_filters_and_actions()
    {
        var q = StickinessSql.Build(Builder(), "Engaged", ["product_viewed", "purchase"], "day", Range(), "UTC");

        Assert.Matches(@"e\.project_id = @p\d+ AND e\.timestamp >= @p\d+ AND e\.timestamp < @p\d+", q.Sql);
        Assert.Matches(@"e\.event IN \(@p\d+, @p\d+\)", q.Sql);
        Assert.Contains("COALESCE(pdi.person_id, d.distinct_id) AS person", q.Sql);
        Assert.DoesNotContain("INNER JOIN", q.Sql);
        Assert.EndsWith("SELECT periods, COUNT(*) AS persons FROM c GROUP BY periods ORDER BY periods", q.Sql);
    }

    [Fact]
    public void Funnel_correlation_scans_all_events_once_and_leaves_the_steps_out_of_the_candidates()
    {
        var q = FunnelSql.Correlation(Builder(), [new FunnelStep("view", null), new FunnelStep("Buying", null, "Buying", ["cart", "buy"])], 3600, 100);

        Assert.Matches(@"window_funnel\(3600, e\.timestamp, 0, \[\(e\.event = @p\d+\), \(e\.event IN \(@p\d+, @p\d+\)\)\]\)", q.Sql);
        Assert.Matches(@"e\.event NOT IN \(@p\d+, @p\d+, @p\d+\)", q.Sql);
        Assert.DoesNotContain("e.event IN (@p", q.Sql.Split("window_funnel")[0]);
        Assert.Contains("COALESCE(pdi.person_id", q.Sql);
        Assert.Contains($"HAVING COUNT(*) >= {FunnelSql.MinCorrelationPersons}", q.Sql);
    }

    [Fact]
    public void Odds_ratio_is_smoothed_so_empty_cells_stay_finite()
    {
        Assert.Equal(1, FunnelSql.OddsRatio(5, 10, 5, 10), 6);
        Assert.True(FunnelSql.OddsRatio(10, 10, 0, 10) > 100);
        Assert.True(FunnelSql.OddsRatio(0, 10, 10, 10) < 0.01);
    }
}
