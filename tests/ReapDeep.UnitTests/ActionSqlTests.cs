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
}
