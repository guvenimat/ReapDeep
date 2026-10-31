using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.UnitTests;

public class RecommendationSqlTests
{
    const long Project = 42;
    static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(RecommendationSql.Popular)]
    [InlineData(RecommendationSql.Trending)]
    [InlineData(RecommendationSql.CoViewed)]
    [InlineData(RecommendationSql.BoughtTogether)]
    public void Every_model_filters_by_project_time_and_item_events(string model)
    {
        var q = RecommendationSql.Build(model, Project, Now);

        Assert.Matches(@"e\.project_id = @p\d+ AND e\.timestamp >= @p\d+ AND e\.timestamp < @p\d+ AND e\.event IN \(@p\d+, @p\d+, @p\d+\) AND e\.item_id IS NOT NULL", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is long v && v == Project);
        Assert.Contains(q.Parameters, p => (p.Value as string) == "purchase");
        Assert.DoesNotContain("'purchase'", q.Sql);
    }

    [Theory]
    [InlineData(RecommendationSql.Popular)]
    [InlineData(RecommendationSql.CoViewed)]
    public void Interactions_are_counted_per_person_across_identities(string model)
    {
        var q = RecommendationSql.Build(model, Project, Now);

        Assert.Contains("COALESCE(pdi.person_id, d.distinct_id) AS person", q.Sql);
        Assert.Contains("LEFT JOIN analytics.person_distinct_ids pdi ON d.project_id = pdi.project_id AND d.distinct_id = pdi.distinct_id", q.Sql);
        Assert.DoesNotContain("INNER JOIN", q.Sql);
    }

    [Fact]
    public void Similarity_caps_items_per_person_and_uses_cosine_scores()
    {
        var q = RecommendationSql.Build(RecommendationSql.CoViewed, Project, Now);

        Assert.Contains($"WHERE n <= {RecommendationSql.MaxItemsPerPerson}", q.Sql);
        Assert.Contains("p.co / sqrt(cs.n * cd.n) AS score", q.Sql);
        Assert.Contains($"WHERE rn <= {RecommendationSql.ItemCandidates}", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is DateTime t && t == Now - RecommendationSql.CoViewedWindow);
    }

    [Fact]
    public void Bought_together_only_uses_purchases_over_a_longer_window()
    {
        var q = RecommendationSql.Build(RecommendationSql.BoughtTogether, Project, Now);

        Assert.Contains($"WHERE w >= {RecommendationSql.PurchaseWeight}", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is DateTime t && t == Now - RecommendationSql.BoughtTogetherWindow);
    }

    [Fact]
    public void Global_models_keep_enough_candidates_for_category_and_stock_filters()
    {
        Assert.EndsWith($"LIMIT {RecommendationSql.GlobalCandidates}", RecommendationSql.Build(RecommendationSql.Popular, Project, Now).Sql);
        Assert.EndsWith($"LIMIT {RecommendationSql.GlobalCandidates}", RecommendationSql.Build(RecommendationSql.Trending, Project, Now).Sql);
        Assert.True(RecommendationSql.GlobalCandidates >= 100);
    }

    [Fact]
    public void Hit_rate_trains_before_the_test_window_and_tests_after_it()
    {
        var q = RecommendationSql.HitRate(Project, Now, 10);
        var testFrom = Now - RecommendationSql.EvaluationWindow;

        Assert.Contains(q.Parameters, p => p.Value is DateTime t && t == testFrom - RecommendationSql.CoViewedWindow);
        Assert.Equal(2, q.Parameters.Count(p => p.Value is DateTime t && t == testFrom));
        Assert.Contains("a.rn = 1 AND n.rn = 2", q.Sql);
        Assert.Contains("rn <= 10", q.Sql);
    }

    [Fact]
    public void Unknown_model_is_rejected()
    {
        Assert.Throws<ValidationException>(() => RecommendationSql.Build("collaborative", Project, Now));
    }

    [Fact]
    public void Person_history_keeps_recent_items_and_every_purchase_per_person()
    {
        var q = RecommendationSql.PersonHistory(Project, Now);

        Assert.Contains("COALESCE(pdi.person_id, d.distinct_id) AS person", q.Sql);
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY person ORDER BY ts DESC, item_id)", q.Sql);
        Assert.Contains($"WHERE n <= {RecommendationSql.MaxHistoryItems} OR w >= {RecommendationSql.PurchaseWeight}", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value is DateTime t && t == Now - RecommendationSql.HistoryWindow);
        Assert.Contains(q.Parameters, p => p.Value is long v && v == Project);
    }
}
