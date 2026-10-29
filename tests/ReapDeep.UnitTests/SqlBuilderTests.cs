using System.Text.Json;
using ReapDeep.QueryApi;

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

    static void AssertTenantAndTimeFilters(string sql, SqlBuilder b)
    {
        Assert.Contains("e.project_id = @p0", sql);
        Assert.Contains("e.timestamp >= @p1", sql);
        Assert.Contains("e.timestamp < @p2", sql);
        Assert.Equal(Project, b.Parameters[0].Value);
        Assert.Equal(From, b.Parameters[1].Value);
        Assert.Equal(To, b.Parameters[2].Value);
    }

    [Fact]
    public void Constructor_adds_project_and_time_filters_as_parameters()
    {
        var b = Builder();
        var sql = b.Select("COUNT(*)");
        Assert.Equal("SELECT COUNT(*) FROM analytics.events e WHERE e.project_id = @p0 AND e.timestamp >= @p1 AND e.timestamp < @p2", sql);
        AssertTenantAndTimeFilters(sql, b);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("unique_users")]
    public void Trend_queries_keep_the_tenant_filter(string metric)
    {
        var builders = new List<SqlBuilder>();
        SqlBuilder New()
        {
            var b = Builder();
            builders.Add(b);
            return b;
        }
        var (buckets, total) = Queries.Trend(New, new Series("$pageview", metric), dedup: true, "day", Range(), "Europe/Istanbul",
            new Breakdown("event", "$browser", 5));
        AssertTenantAndTimeFilters(buckets.Sql, buckets.Builder);
        if (total is not null) AssertTenantAndTimeFilters(total.Sql, total.Builder);
    }

    [Fact]
    public void Funnel_retention_and_paths_keep_the_tenant_filter()
    {
        var f = Builder();
        AssertTenantAndTimeFilters(Queries.Funnel(f, [new FunnelStep("a", null), new FunnelStep("b", null)], 3600, null, 0.1), f);

        var r = Builder();
        AssertTenantAndTimeFilters(Queries.Retention(r, "signup", "login", "week", 6, Range(), "Europe/Istanbul", To), r);

        var p = Builder();
        AssertTenantAndTimeFilters(Queries.PathSequences(p, "$pageview", 4, 3600, true, 25), p);

        var e = Builder();
        AssertTenantAndTimeFilters(Queries.Events(e, "x", null, ["a", "b"], null, 50), e);
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
        Assert.Throws<QueryException>(() => b.AddFilters([new Filter("event", property, "is_set", null)]));
    }

    [Fact]
    public void Unknown_operator_is_rejected()
    {
        Assert.Throws<QueryException>(() => Builder().AddFilters([new Filter("event", "plan", "regex", Json("\"x\""))]));
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
        var b = Builder();
        var sql = Queries.Retention(b, "a", "b", "day", 7, Range(), "Europe/Istanbul", To);
        Assert.Contains("seconds_add(e.timestamp, 10800)", sql);
        Assert.DoesNotContain("convert_tz", sql);
    }
}
