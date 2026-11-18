using System.Text.Json;
using System.Text.RegularExpressions;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.UnitTests;

public class WebSqlTests
{
    const long Project = 42;
    static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime To = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
    static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    static SqlBuilder Builder() => new(Project, From, To);

    static TimeRange Range() => TimeRange.Between(From, To, Istanbul);

    public static TheoryData<string> Queries() => ["overview", "series", "pages", "sessions"];

    static SqlQuery Build(string name, SqlBuilder b) => name switch
    {
        "overview" => WebSql.Overview(b, From.AddDays(3)),
        "series" => WebSql.Series(b, "day", Range(), "Europe/Istanbul"),
        "pages" => WebSql.Pages(b, 10),
        _ => WebSql.Sessions(b, 10),
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void Every_query_keeps_the_tenant_and_time_filters_as_parameters(string name)
    {
        var q = Build(name, Builder());
        Assert.Contains("e.project_id = @p0 AND e.timestamp >= @p1 AND e.timestamp < @p2", q.Sql);
        Assert.Equal(Project, q.Parameters[0].Value);
        Assert.Equal(From, q.Parameters[1].Value);
        Assert.Equal(To, q.Parameters[2].Value);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Persons_come_from_a_left_join_on_distinct_ids(string name)
    {
        var q = Build(name, Builder());
        Assert.Contains("COALESCE(pdi.person_id, e.distinct_id) AS person", q.Sql);
        Assert.Contains("LEFT JOIN analytics.person_distinct_ids pdi ON e.project_id = pdi.project_id AND e.distinct_id = pdi.distinct_id AND pdi.is_deleted = 0", q.Sql);
        Assert.DoesNotContain("INNER JOIN", q.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Regex.Matches(q.Sql, "person_distinct_ids"));
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Every_placeholder_has_a_parameter_and_no_value_is_inlined(string name)
    {
        var q = Build(name, Builder());
        var used = Regex.Matches(q.Sql, @"@p\d+").Select(m => m.Value).ToHashSet();
        Assert.Equal(q.Parameters.Select(p => p.Name).ToHashSet(), used);
        Assert.DoesNotContain("'$pageview'", q.Sql);
        Assert.DoesNotContain("$direct", q.Sql);
    }

    [Fact]
    public void Pageview_queries_filter_on_the_pageview_event()
    {
        foreach (var name in new[] { "series", "pages" })
        {
            var q = Build(name, Builder());
            var p = Assert.Single(q.Parameters, x => Equals(x.Value, "$pageview"));
            Assert.Contains($"e.event = {p.Name}", q.Sql);
        }
    }

    [Fact]
    public void Overview_splits_the_current_and_previous_period_by_the_session_start()
    {
        var current = From.AddDays(3);
        var q = WebSql.Overview(Builder(), current);
        var p = Assert.Single(q.Parameters, x => Equals(x.Value, current)).Name;
        Assert.Contains($"IF(ts >= {p}, 1, 0) AS cur", q.Sql);
        Assert.Contains($"IF(first_ts >= {p}, 1, 0) AS cur", q.Sql);
        Assert.Contains("HAVING SUM(pv) > 0", q.Sql);
        Assert.Contains("SUM(IF(pageviews = 1, 1, 0))", q.Sql);
        Assert.Contains("seconds_diff(last_ts, first_ts)", q.Sql);
    }

    [Fact]
    public void Sessions_take_their_attributes_from_the_first_pageview_and_rank_each_dimension()
    {
        var q = WebSql.Sessions(Builder(), 7);
        Assert.Contains("e.session_id IS NOT NULL", q.Sql);
        foreach (var d in WebSql.Dimensions)
        {
            Assert.Contains($"min_by({d}, IF(pv = 1, ts, NULL)) AS {d}", q.Sql);
            var p = Assert.Single(q.Parameters, x => Equals(x.Value, d)).Name;
            Assert.Contains($"SELECT {p} AS dim, {d} AS value", q.Sql);
            Assert.Contains($"GROUP BY {d} ORDER BY sessions DESC, value LIMIT 7", q.Sql);
        }
        Assert.Equal(WebSql.Dimensions.Length - 1, Regex.Matches(q.Sql, "UNION ALL").Count);
        var direct = Assert.Single(q.Parameters, x => Equals(x.Value, "$direct")).Name;
        Assert.Contains($"COALESCE(e.referring_domain, {direct}) AS referring_domain", q.Sql);
    }

    [Fact]
    public void Series_buckets_in_the_project_time_zone()
    {
        var q = WebSql.Series(Builder(), "day", Range(), "Europe/Istanbul");
        Assert.Contains("date_trunc('day', seconds_add(e.timestamp, 10800)) AS bucket", q.Sql);
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var dst = TimeRange.Between(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc), berlin);
        var q2 = WebSql.Series(new SqlBuilder(Project, dst.FromUtc, dst.ToUtc), "day", dst, "Europe/Berlin");
        Assert.Contains("convert_tz(e.timestamp, 'UTC', @p", q2.Sql);
        Assert.Contains(q2.Parameters, x => Equals(x.Value, "Europe/Berlin"));
    }

    [Fact]
    public void Filters_are_added_to_the_events_scan_as_parameters()
    {
        var b = Builder();
        b.AddFilters([new Filter("event", "$device_type", "eq", JsonDocument.Parse("\"Mobile\"").RootElement.Clone())]);
        var q = WebSql.Sessions(b, 10);
        var p = Assert.Single(q.Parameters, x => Equals(x.Value, "Mobile")).Name;
        Assert.Contains($"e.device_type = {p}", q.Sql);
        Assert.DoesNotContain("'Mobile'", q.Sql);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 1)]
    [InlineData(25, 25)]
    [InlineData(1000, 100)]
    public void The_row_limit_is_clamped(int? requested, int expected)
    {
        Assert.Equal(expected, WebSql.Limit(requested));
        Assert.Contains($"LIMIT {expected}", WebSql.Pages(Builder(), WebSql.Limit(requested)).Sql);
    }

    [Theory]
    [InlineData(1, "hour")]
    [InlineData(2, "hour")]
    [InlineData(7, "day")]
    [InlineData(90, "day")]
    [InlineData(180, "week")]
    public void The_default_interval_follows_the_range(int days, string expected)
    {
        Assert.Equal(expected, WebSql.DefaultInterval(TimeRange.Between(From, From.AddDays(days), TimeZoneInfo.Utc)));
    }

    [Fact]
    public void Totals_add_up_rows_and_derive_rates()
    {
        var t = WebTotals.Sum([new WebTotals(3, 10, 0, 0, 0), new WebTotals(0, 0, 4, 1, 300)]);
        Assert.Equal(new WebTotals(3, 10, 4, 1, 300), t);
        Assert.Equal(0.25, t.BounceRate);
        Assert.Equal(75.0, t.AvgSessionSeconds);
        Assert.Equal(0.3333, new WebTotals(0, 0, 3, 1, 100).BounceRate);
        Assert.Equal(33.3, new WebTotals(0, 0, 3, 1, 100).AvgSessionSeconds);
    }

    [Fact]
    public void Totals_without_sessions_have_no_rates()
    {
        var t = WebTotals.Sum([]);
        Assert.Null(t.BounceRate);
        Assert.Null(t.AvgSessionSeconds);
    }
}
