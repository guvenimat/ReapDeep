using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Cohorts;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.UnitTests;

public class CohortSqlTests
{
    const long Project = 42;
    static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    static CohortCondition Performed(string ev, int count = 1, int days = 30, string op = "gte", List<Filter>? filters = null) =>
        new(ConditionTypes.Performed, Event: ev, CountOp: op, Count: count, WithinDays: days, Filters: filters);

    static CohortCondition NotPerformed(string ev, int days = 30) => new(ConditionTypes.NotPerformed, Event: ev, WithinDays: days);

    static CohortCondition Property(string name, string value) => new(ConditionTypes.PersonProperty, Property: name, Op: "eq", Value: Json($"\"{value}\""));

    static CohortDefinition Def(params CohortCondition[][] groups) =>
        new(groups.Select(g => new CohortGroup(g.ToList())).ToList());

    static object Param(SqlQuery q, string name) => q.Parameters.Single(p => p.Name == name).Value;

    [Fact]
    public void Behavioral_rule_filters_by_project_and_time_and_aggregates_before_the_person_join()
    {
        var q = CohortSql.Count(Project, Def([Performed("product_viewed", 3), NotPerformed("purchase")]), Now);

        Assert.Matches(@"e\.project_id = @p\d+ AND e\.timestamp >= @p\d+ AND e\.timestamp < @p\d+ AND e\.event IN \(@p\d+, @p\d+\) GROUP BY 1, 2", q.Sql);
        Assert.Contains("LEFT JOIN analytics.person_distinct_ids pdi ON a.project_id = pdi.project_id AND a.distinct_id = pdi.distinct_id", q.Sql);
        Assert.Contains("COALESCE(pdi.person_id, a.distinct_id) AS person", q.Sql);
        Assert.DoesNotContain("INNER JOIN", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is long v && v == Project);
        Assert.Contains(q.Parameters, p => p.Value is DateTime t && t == Now.AddDays(-30));
    }

    [Fact]
    public void Values_are_parameters_not_sql_text()
    {
        var def = Def([Performed("bought'; DROP TABLE x", filters: [new Filter("event", "$browser", "eq", Json("\"Chrome\""))]), Property("plan", "premium-tier")]);
        var q = CohortSql.Count(Project, def, Now);

        Assert.DoesNotContain("DROP TABLE", q.Sql);
        Assert.DoesNotContain("Chrome", q.Sql);
        Assert.DoesNotContain("premium-tier", q.Sql);
        Assert.Contains(q.Parameters, p => (p.Value as string) == "bought'; DROP TABLE x");
        Assert.Contains("e.browser = @p", q.Sql);
    }

    [Fact]
    public void Count_operators_and_negation_become_per_person_conditions()
    {
        var q = CohortSql.Count(Project, Def([Performed("a", 3), Performed("b", 2, op: "lte"), Performed("c", 5, op: "eq"), NotPerformed("d")]), Now);

        Assert.Matches(@"x\.c0 >= @p\d+", q.Sql);
        Assert.Matches(@"x\.c1 BETWEEN 1 AND @p\d+", q.Sql);
        Assert.Matches(@"x\.c2 = @p\d+", q.Sql);
        Assert.Contains("x.c3 = 0", q.Sql);
    }

    [Fact]
    public void Property_only_group_reads_persons_without_scanning_events()
    {
        var q = CohortSql.Count(Project, Def([Property("plan", "pro")]), Now);

        Assert.DoesNotContain("analytics.events", q.Sql);
        Assert.Contains("FROM analytics.persons p WHERE p.project_id = @p", q.Sql);
        Assert.Contains("p.is_deleted = 0", q.Sql);
        Assert.Contains("get_json_string(p.properties, @p", q.Sql);
    }

    [Fact]
    public void Property_with_negation_keeps_persons_who_never_did_the_event()
    {
        var q = CohortSql.Count(Project, Def([Property("plan", "pro"), NotPerformed("purchase", 14)]), Now);

        Assert.Contains("FROM analytics.persons p LEFT JOIN (", q.Sql);
        Assert.Contains("COALESCE(x.c0, 0) = 0", q.Sql);
    }

    [Fact]
    public void Groups_are_combined_with_union()
    {
        var q = CohortSql.Count(Project, Def([Performed("a")], [Property("plan", "pro")]), Now);

        Assert.Equal(1, q.Sql.Split(" UNION ").Length - 1);
        Assert.StartsWith("WITH members AS (", q.Sql);
    }

    [Fact]
    public void Materialize_expands_persons_to_every_distinct_id_and_keeps_anonymous_members()
    {
        var q = CohortSql.Materialize(Project, 7, 1234, Def([Performed("a")]), Now);

        Assert.StartsWith("INSERT INTO analytics.cohort_members (project_id, cohort_id, version, distinct_id) WITH members AS (", q.Sql);
        Assert.Contains("FROM members m JOIN analytics.person_distinct_ids pdi ON pdi.project_id = @p", q.Sql);
        Assert.Contains("UNION ALL SELECT", q.Sql);
        Assert.Contains("FROM members m LEFT ANTI JOIN analytics.person_distinct_ids pdi", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is long v && v == 1234);
    }

    [Fact]
    public void Person_count_matches_the_person_expression_of_the_analyses()
    {
        var q = CohortSql.PersonCount(Project, 7, 1234);

        Assert.StartsWith("SELECT COUNT(DISTINCT COALESCE(pdi.person_id, cm.distinct_id))", q.Sql);
        Assert.Contains("LEFT JOIN analytics.person_distinct_ids pdi ON cm.project_id = pdi.project_id AND cm.distinct_id = pdi.distinct_id", q.Sql);
        Assert.Matches(@"cm\.project_id = @p\d+ AND cm\.cohort_id = @p\d+ AND cm\.version = @p\d+", q.Sql);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public void Window_outside_1_to_90_days_is_rejected(int days)
    {
        Assert.Throws<ValidationException>(() => CohortRules.Normalize(Def([Performed("a", days: days)])));
    }

    [Fact]
    public void Group_with_only_negative_conditions_is_rejected()
    {
        var ex = Assert.Throws<ValidationException>(() => CohortRules.Normalize(Def([NotPerformed("purchase")])));
        Assert.Contains("not_performed_event alone", ex.Message);
    }

    [Fact]
    public void Person_filters_inside_a_behavioral_condition_are_rejected()
    {
        var def = Def([Performed("a", filters: [new Filter("person", "plan", "eq", Json("\"pro\""))])]);
        Assert.Throws<ValidationException>(() => CohortRules.Normalize(def));
    }

    [Fact]
    public void Too_many_groups_or_conditions_are_rejected()
    {
        Assert.Throws<ValidationException>(() => CohortRules.Normalize(Def([.. Enumerable.Range(0, 6).Select(_ => new[] { Performed("a") })])));
        Assert.Throws<ValidationException>(() => CohortRules.Normalize(Def([.. Enumerable.Range(0, 11).Select(_ => Performed("a"))])));
    }

    [Fact]
    public void Definition_round_trips_through_snake_case_json()
    {
        var json = """{"groups":[{"conditions":[{"type":"performed_event","event":"a","count_op":"gte","count":3,"within_days":30}]}]}""";
        var def = JsonSerializer.Deserialize(json, CohortJsonContext.Default.CohortDefinition)!;
        var c = def.Groups![0].Conditions![0];

        Assert.Equal(("gte", 3, 30), (c.CountOp, c.Count, c.WithinDays));
        Assert.Equal(json, JsonSerializer.Serialize(def, CohortJsonContext.Default.CohortDefinition));
    }

    static readonly IReadOnlyDictionary<long, CohortRef> Cohorts = new Dictionary<long, CohortRef> { [7] = new(7, "Buyers", 1234) };

    [Fact]
    public void Cohort_filter_is_a_colocated_semi_join_on_the_events_table()
    {
        var b = new SqlBuilder(Project, Now.AddDays(-7), Now);
        b.AddFilters([new Filter("cohort", "7", "in", null)], Cohorts);
        var q = b.Build(b.Select("COUNT(*)"));

        Assert.Matches(@"FROM analytics\.events e LEFT SEMI JOIN analytics\.cohort_members cm0 ON cm0\.project_id = e\.project_id AND cm0\.distinct_id = e\.distinct_id AND cm0\.cohort_id = @p\d+ AND cm0\.version = @p\d+ WHERE e\.project_id = @p0", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is long v && v == 1234);
    }

    [Fact]
    public void Excluding_a_cohort_is_an_anti_join()
    {
        var b = new SqlBuilder(Project, Now.AddDays(-7), Now);
        b.AddFilters([new Filter("cohort", "7", "not_in", null)], Cohorts);

        Assert.Contains("LEFT ANTI JOIN analytics.cohort_members cm0", b.Select("COUNT(*)"));
    }

    [Fact]
    public void Cohort_filter_joins_after_the_unique_users_pre_aggregation()
    {
        var plan = TrendSql.Build(() =>
        {
            var b = new SqlBuilder(Project, Now.AddDays(-7), Now);
            b.AddFilters([new Filter("cohort", "7", "in", null)], Cohorts);
            return b;
        }, new Series("$pageview", "unique_users"), false, "day", TimeRange.Parse("2026-10-20", "2026-10-26", TimeZoneInfo.Utc, TimeSpan.FromDays(7)), "UTC", null);

        Assert.Matches(@"FROM \(SELECT a\.\* FROM \(SELECT e\.project_id, e\.distinct_id, .* FROM analytics\.events e WHERE .* GROUP BY 1, 2, 3\) a LEFT SEMI JOIN analytics\.cohort_members cm0 ON cm0\.project_id = a\.project_id AND cm0\.distinct_id = a\.distinct_id .*\) d LEFT JOIN analytics\.person_distinct_ids pdi", plan.Buckets.Sql);
    }

    [Fact]
    public void Unknown_cohort_or_invalid_operator_is_rejected()
    {
        var b = new SqlBuilder(Project, Now.AddDays(-7), Now);
        Assert.Throws<ValidationException>(() => b.AddFilters([new Filter("cohort", "99", "in", null)], Cohorts));
        Assert.Throws<ValidationException>(() => b.AddFilters([new Filter("cohort", "7", "eq", null)], Cohorts));
        Assert.Throws<ValidationException>(() => b.AddFilters([new Filter("cohort", "abc", "in", null)], Cohorts));
    }
}
