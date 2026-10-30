using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.Ingestion.Capture;
using ReapDeep.Ingestion.Definitions;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Organization;

namespace ReapDeep.UnitTests;

public class TimeRangeTests
{
    static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void Dates_are_project_local_days_converted_to_utc_with_an_inclusive_end()
    {
        var r = TimeRange.Parse("2026-10-01", "2026-10-07", Istanbul, TimeSpan.FromDays(7));
        Assert.Equal(new DateTime(2026, 9, 30, 21, 0, 0, DateTimeKind.Utc), r.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 7, 21, 0, 0, DateTimeKind.Utc), r.ToUtc);
        Assert.Equal(10800, r.FixedOffsetSeconds);
    }

    [Fact]
    public void A_range_across_a_dst_change_has_no_fixed_offset()
    {
        var r = TimeRange.Parse("2026-10-20", "2026-10-30", Berlin, TimeSpan.FromDays(7));
        Assert.Null(r.FixedOffsetSeconds);
    }

    [Theory]
    [InlineData("2026-10-07", "2026-10-01")]
    [InlineData("2025-01-01", "2026-06-01")]
    [InlineData("yesterday", null)]
    public void Invalid_ranges_are_rejected(string from, string? to)
    {
        Assert.Throws<ValidationException>(() => TimeRange.Parse(from, to, Istanbul, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void Week_buckets_start_on_monday()
    {
        var r = TimeRange.Parse("2026-10-01", "2026-10-14", Istanbul, TimeSpan.FromDays(7));
        var buckets = r.Buckets("week");
        Assert.All(buckets, b => Assert.Equal(DayOfWeek.Monday, b.DayOfWeek));
        Assert.Equal(new DateTime(2026, 9, 28), buckets[0]);
        Assert.Equal(3, buckets.Count);
    }
}

public class PasswordTests
{
    [Fact]
    public void Hash_verifies_only_the_right_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery");
        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("correct horse batterY", hash));
    }

    [Fact]
    public void Every_hash_has_its_own_salt()
    {
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("md5$1$abc$def")]
    public void Missing_or_malformed_hashes_never_verify(string? stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }
}

public class UserAgentParserTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome", "Windows", "Desktop")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0", "Microsoft Edge", "Windows", "Desktop")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", "Safari", "iOS", "Mobile")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox", "Linux", "Desktop")]
    public void Common_browsers_are_recognized(string ua, string browser, string os, string device)
    {
        var r = UserAgentParser.Parse(ua);
        Assert.Equal(browser, r.Browser);
        Assert.Equal(os, r.Os);
        Assert.Equal(device, r.DeviceType);
    }

    [Fact]
    public void Missing_user_agent_gives_empty_result()
    {
        Assert.Equal(default, UserAgentParser.Parse(null));
    }
}

public class DefinitionTypeTests
{
    [Theory]
    [InlineData("42.5", "number")]
    [InlineData("true", "boolean")]
    [InlineData("\"2026-10-29T10:00:00Z\"", "datetime")]
    [InlineData("\"2026-10-29\"", "datetime")]
    [InlineData("\"pro\"", "string")]
    [InlineData("\"2026 was a year\"", "string")]
    [InlineData("{\"a\":1}", null)]
    [InlineData("null", null)]
    public void Property_types_are_inferred_from_values(string json, string? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, PropertyTypes.Infer(doc.RootElement));
    }
}

public class MembershipRulesTests
{
    static readonly Dictionary<long, string> Members = new() { [1] = Roles.Owner, [2] = Roles.Admin, [3] = Roles.Member };

    [Theory]
    [InlineData(Roles.Owner, Roles.Owner, MembershipError.None)]
    [InlineData(Roles.Admin, Roles.Admin, MembershipError.None)]
    [InlineData(Roles.Admin, Roles.Owner, MembershipError.OwnerOnly)]
    public void Only_owners_can_add_owners(string actor, string role, MembershipError expected)
    {
        Assert.Equal(expected, MembershipRules.CanAdd(actor, role));
    }

    [Theory]
    [InlineData(Roles.Admin, 3L, Roles.Admin, MembershipError.None)]
    [InlineData(Roles.Admin, 3L, null, MembershipError.None)]
    [InlineData(Roles.Admin, 1L, Roles.Member, MembershipError.OwnerOnly)]
    [InlineData(Roles.Admin, 3L, Roles.Owner, MembershipError.OwnerOnly)]
    [InlineData(Roles.Owner, 1L, Roles.Admin, MembershipError.LastOwner)]
    [InlineData(Roles.Owner, 1L, null, MembershipError.LastOwner)]
    [InlineData(Roles.Owner, 9L, Roles.Admin, MembershipError.NotFound)]
    public void Role_changes_follow_the_rules(string actor, long userId, string? role, MembershipError expected)
    {
        Assert.Equal(expected, MembershipRules.CanChange(actor, Members, userId, role));
    }

    [Fact]
    public void An_owner_can_step_down_when_another_owner_exists()
    {
        var roles = new Dictionary<long, string> { [1] = Roles.Owner, [2] = Roles.Owner };
        Assert.Equal(MembershipError.None, MembershipRules.CanChange(Roles.Owner, roles, 1, Roles.Member));
    }
}

public class DapperConventionTests
{
    [Fact]
    public void Dapper_row_records_are_not_private()
    {
        var rows = new[] { typeof(PasswordHasher).Assembly, typeof(PropertyTypes).Assembly, typeof(ReapDeep.PersonWorker.Identity.IdentityEvent).Assembly,
            typeof(ReapDeep.Recommender.Recommendations.RecommendationCache).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsNested && t.Name.EndsWith("Record", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, t => Assert.False(t.IsNestedPrivate, $"{t.FullName} is private; Dapper.AOT silently skips private row types"));
    }
}
