using ReapDeep.QueryApi.Auth;

namespace ReapDeep.UnitTests;

public class QueryQuotaTests
{
    static readonly DateTime Now = new(2026, 10, 31, 12, 0, 15, DateTimeKind.Utc);

    [Fact]
    public void Requests_within_both_limits_pass() =>
        Assert.True(QuotaRules.Decide(300, 1200, 300, 1200, Now).Allowed);

    [Fact]
    public void The_principal_limit_is_checked_first()
    {
        var d = QuotaRules.Decide(301, 5000, 300, 1200, Now);
        Assert.Equal((false, "principal"), (d.Allowed, d.Scope));
    }

    [Fact]
    public void A_busy_project_is_limited_as_a_whole() =>
        Assert.Equal("project", QuotaRules.Decide(10, 1201, 300, 1200, Now).Scope);

    [Fact]
    public void Retry_after_points_to_the_next_window() =>
        Assert.Equal(45, QuotaRules.Decide(301, 0, 300, 1200, Now).RetryAfterSeconds);

    [Fact]
    public void Windows_roll_over_every_minute()
    {
        Assert.Equal(QuotaRules.WindowKey("project", "1", Now), QuotaRules.WindowKey("project", "1", Now.AddSeconds(40)));
        Assert.NotEqual(QuotaRules.WindowKey("project", "1", Now), QuotaRules.WindowKey("project", "1", Now.AddSeconds(50)));
    }
}
