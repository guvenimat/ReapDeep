using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Analytics;

namespace ReapDeep.UnitTests;

public class AnalyticsCacheTests
{
    const string Body = "{\"series\":[{\"event\":\"$pageview\"}]}";

    static string Key(long project = 1, string endpoint = "trends", string tz = "UTC", string body = Body, params CohortRef[] cohorts) =>
        AnalyticsCacheKeys.Build(project, endpoint, tz, body, cohorts);

    [Fact]
    public void Same_inputs_give_the_same_key() => Assert.Equal(Key(), Key());

    [Fact]
    public void The_key_is_scoped_by_project_and_endpoint()
    {
        Assert.StartsWith("query:7:funnels:", Key(project: 7, endpoint: "funnels"));
        Assert.NotEqual(Key(project: 1), Key(project: 2));
        Assert.NotEqual(Key(endpoint: "trends"), Key(endpoint: "funnels"));
    }

    [Fact]
    public void Body_and_time_zone_change_the_key()
    {
        Assert.NotEqual(Key(), Key(body: Body.Replace("$pageview", "signup")));
        Assert.NotEqual(Key(tz: "UTC"), Key(tz: "Europe/Istanbul"));
    }

    [Fact]
    public void A_new_cohort_version_changes_the_key_but_order_does_not()
    {
        var a = new CohortRef(1, "a", 1);
        var b = new CohortRef(2, "b", 3);
        Assert.Equal(Key(cohorts: [a, b]), Key(cohorts: [b, a]));
        Assert.NotEqual(Key(cohorts: [a, b]), Key(cohorts: [a, b with { Version = 4 }]));
        Assert.NotEqual(Key(), Key(cohorts: [a]));
    }

    [Theory]
    [InlineData("no-cache", true)]
    [InlineData("No-Store", true)]
    [InlineData("max-age=0, no-cache", true)]
    [InlineData("max-age=60", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Cache_control_no_cache_or_no_store_skips_the_cached_read(string? header, bool bypass) =>
        Assert.Equal(bypass, AnalyticsCacheKeys.BypassRead(header));
}
