using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Analytics;

namespace ReapDeep.UnitTests;

public class TrendFormulaTests
{
    static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    static SqlBuilder Builder() => new(42, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));

    static TimeRange Range() => TimeRange.Parse("2026-10-01", "2026-10-07", Istanbul, TimeSpan.FromDays(7));

    [Theory]
    [InlineData("A / B", 0.25)]
    [InlineData("a / b * 100", 25)]
    [InlineData("(A + B) * 2", 50)]
    [InlineData("A + B * 2", 45)]
    [InlineData("-A + 10", 5)]
    [InlineData("A - B - 5", -20)]
    [InlineData("A / 3", 1.6667)]
    [InlineData("2.5 * A", 12.5)]
    public void Evaluates_with_precedence_and_parentheses(string formula, double expected) =>
        Assert.Equal(expected, TrendFormula.Parse(formula, 2).Evaluate([5, 20]));

    [Fact]
    public void Division_by_zero_is_zero_not_infinity() => Assert.Equal(0, TrendFormula.Parse("A / B", 2).Evaluate([5, 0]));

    [Theory]
    [InlineData("A / C")]
    [InlineData("A +")]
    [InlineData("(A + B")]
    [InlineData("A B")]
    [InlineData("A; DROP")]
    [InlineData("1..2 * A")]
    [InlineData("")]
    public void Rejects_unknown_series_and_malformed_input(string formula) =>
        Assert.Throws<ValidationException>(() => TrendFormula.Parse(formula, 2));

    [Theory]
    [InlineData("sum", "SUM(base.v)")]
    [InlineData("avg", "AVG(base.v)")]
    [InlineData("p90", "percentile_approx(base.v, 0.9)")]
    [InlineData("median", "percentile_approx(base.v, 0.5)")]
    public void Property_metrics_aggregate_the_numeric_property(string metric, string aggregate)
    {
        var plan = TrendSql.Build(Builder, new Series("purchase", metric, "amount"), false, "day", Range(), "Europe/Istanbul", null);

        Assert.Contains(aggregate, plan.Buckets.Sql);
        Assert.Contains("CAST(get_json_string(e.properties, @p", plan.Buckets.Sql);
        Assert.Contains("WHERE base.v IS NOT NULL", plan.Buckets.Sql);
        Assert.Contains(plan.Buckets.Parameters, p => p.Value is string s && s == "$.\"amount\"");
        Assert.Equal(metric == "sum", plan.Total is null);
    }

    [Fact]
    public void Property_metric_with_breakdown_picks_top_values_by_event_count()
    {
        var plan = TrendSql.Build(Builder, new Series("purchase", "avg", "amount"), false, "day", Range(), "Europe/Istanbul",
            new Breakdown("event", "$browser", 3));

        Assert.Contains("top AS (SELECT bd FROM base GROUP BY bd ORDER BY COUNT(*) DESC, bd LIMIT 3)", plan.Buckets.Sql);
        Assert.Contains("AVG(base.v)", plan.Buckets.Sql);
    }
}
