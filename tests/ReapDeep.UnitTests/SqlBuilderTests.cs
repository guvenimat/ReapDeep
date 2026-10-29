using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Analytics;

namespace ReapDeep.UnitTests;

public class SqlBuilderTests
{
    const long Project = 42;
    static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime To = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
    static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    static SqlBuilder Builder() => new(Project, From, To);

    static TimeRange Range() => TimeRange.Parse("2026-10-01", "2026-10-07", Istanbul, TimeSpan.FromDays(7));

    static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    static void AssertTenantAndTimeFilters(SqlQuery query)
    {
        Assert.Contains("e.project_id = @p0", query.Sql);
        Assert.Contains("e.timestamp >= @p1", query.Sql);
        Assert.Contains("e.timestamp < @p2", query.Sql);
        Assert.Equal(Project, query.Parameters[0].Value);
        Assert.Equal(From, query.Parameters[1].Value);
        Assert.Equal(To, query.Parameters[2].Value);
    }

    [Fact]
    public void Constructor_adds_project_and_time_filters_as_parameters()
    {
        var b = Builder();
        var query = b.Build(b.Select("COUNT(*)"));
        Assert.Equal("SELECT COUNT(*) FROM analytics.events e WHERE e.project_id = @p0 AND e.timestamp >= @p1 AND e.timestamp < @p2", query.Sql);
        AssertTenantAndTimeFilters(query);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("unique_users")]
    public void Trend_queries_keep_the_tenant_filter(string metric)
    {
        var plan = TrendSql.Build(Builder, new Series("$pageview", metric), dedup: true, "day", Range(), "Europe/Istanbul",
            new Breakdown("event", "$browser", 5));
        AssertTenantAndTimeFilters(plan.Buckets);
        Assert.NotNull(plan.Total);
        AssertTenantAndTimeFilters(plan.Total);
    }

    [Fact]
    public void Funnel_retention_and_paths_keep_the_tenant_filter()
    {
        AssertTenantAndTimeFilters(FunnelSql.Build(Builder(), [new FunnelStep("a", null), new FunnelStep("b", null)], 3600, null, 0.1));
        AssertTenantAndTimeFilters(RetentionSql.Build(Builder(), "signup", "login", "week", 6, Range(), "Europe/Istanbul", To));
        AssertTenantAndTimeFilters(PathSql.Steps(Builder(), "$pageview", 4, 3600, true));
        AssertTenantAndTimeFilters(PathSql.Sequences(Builder(), "$pageview", 4, 3600, true, 25));
        AssertTenantAndTimeFilters(EventSql.Build(Builder(), "x", null, ["a", "b"], null, 50));
    }

    [Fact]
    public void Filter_values_never_appear_in_the_sql_text()
    {
        const string attack = "x' OR 1=1 --";
        var b = Builder();
        b.AddFilters([new Filter("event", "plan", "eq", Json($"\"{attack}\"")), new Filter("event", "$browser", "contains", Json("\"50%_off\""))]);
        var sql = b.Select("COUNT(*)");

        Assert.DoesNotContain(attack, sql);
        Assert.Contains(b.Parameters, p => p.Value as string == attack);
        Assert.Contains(b.Parameters, p => p.Value as string == "%50\\%\\_off%");
        Assert.Contains("e.browser LIKE @p", sql);
    }

    [Theory]
    [InlineData("plan'; DROP TABLE events; --")]
    [InlineData("a\"b")]
    [InlineData("")]
    public void Invalid_property_names_are_rejected(string property)
    {
        var b = Builder();
        Assert.Throws<ValidationException>(() => b.AddFilters([new Filter("event", property, "is_set", null)]));
    }

    [Fact]
    public void Unknown_operator_is_rejected()
    {
        Assert.Throws<ValidationException>(() => Builder().AddFilters([new Filter("event", "plan", "regex", Json("\"x\""))]));
    }

    [Fact]
    public void Person_filters_join_persons_and_resolve_people_with_a_left_join()
    {
        var b = Builder();
        b.AddFilters([new Filter("person", "plan", "eq", Json("\"pro\""))]);
        var sql = b.Select($"COUNT(DISTINCT {b.Person()})");

        Assert.Contains("LEFT JOIN analytics.person_distinct_ids pdi", sql);
        Assert.Contains("LEFT JOIN analytics.persons p", sql);
        Assert.DoesNotContain("INNER JOIN", sql);
        Assert.Contains("COALESCE(pdi.person_id, e.distinct_id)", sql);
    }

    [Fact]
    public void Promoted_properties_map_to_columns_and_others_to_json()
    {
        var b = Builder();
        Assert.Equal("e.current_url", ColumnMapper.Expression(b, "event", "$current_url"));
        Assert.Equal("e.utm_source", ColumnMapper.Expression(b, "event", "utm_source"));
        var json = ColumnMapper.Expression(b, "event", "cart_value");
        Assert.StartsWith("get_json_string(e.properties, @p", json);
        Assert.Equal("$.\"cart_value\"", b.Parameters[^1].Value);
    }

    [Fact]
    public void Fixed_offset_ranges_avoid_convert_tz()
    {
        var query = RetentionSql.Build(Builder(), "a", "b", "day", 7, Range(), "Europe/Istanbul", To);
        Assert.Contains("seconds_add(e.timestamp, 10800)", query.Sql);
        Assert.DoesNotContain("convert_tz", query.Sql);
    }
}
