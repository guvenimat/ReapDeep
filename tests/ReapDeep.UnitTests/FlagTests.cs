using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Flags;
using ReapDeep.Recommender.Flags;

namespace ReapDeep.UnitTests;

public class FlagHashingTests
{
    static readonly Dictionary<string, string> NoProps = [];

    static CompiledFlag Flag(int rollout = 100, List<FlagVariant>? variants = null, List<FlagCondition>? conditions = null, string salt = "s1", string key = "new-checkout") =>
        new(key, salt, rollout, variants ?? [], conditions ?? []);

    static IEnumerable<string> Identities(int n) => Enumerable.Range(0, n).Select(i => $"0193a5c2-{i:x8}-7000-8000-identity{i}");

    [Fact]
    public void Bucket_is_deterministic_in_range_and_pinned()
    {
        Assert.Equal(FlagHashing.Bucket("k", "salt", "v", "id-1"), FlagHashing.Bucket("k", "salt", "v", "id-1"));
        Assert.All(Identities(2000), id => Assert.InRange(FlagHashing.Bucket("k", "salt", "r", id), 0, FlagHashing.Buckets - 1));
        Assert.Equal(PinnedBucket, FlagHashing.Bucket("new-checkout", "0123456789abcdef", "v", "anon-1"));
    }

    const int PinnedBucket = 9618;

    [Fact]
    public void Bucket_depends_on_key_salt_purpose_and_identity()
    {
        var ids = Identities(500).ToList();
        int Same(Func<string, int> a, Func<string, int> b) => ids.Count(id => a(id) == b(id));
        Assert.True(Same(id => FlagHashing.Bucket("k", "s", "v", id), id => FlagHashing.Bucket("k2", "s", "v", id)) < 5);
        Assert.True(Same(id => FlagHashing.Bucket("k", "s", "v", id), id => FlagHashing.Bucket("k", "s2", "v", id)) < 5);
        Assert.True(Same(id => FlagHashing.Bucket("k", "s", "v", id), id => FlagHashing.Bucket("k", "s", "r", id)) < 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(100)]
    public void Rollout_share_matches_the_percentage(int rollout)
    {
        var flag = Flag(rollout);
        const int n = 20_000;
        var on = Identities(n).Count(id => flag.Evaluate(id, NoProps).Enabled);
        Assert.InRange(on / (double)n * 100, rollout - 1.5, rollout + 1.5);
    }

    [Fact]
    public void Variant_shares_match_the_percentages()
    {
        var flag = Flag(variants: [new("control", 20), new("a", 30), new("b", 50)]);
        const int n = 30_000;
        var counts = Identities(n).GroupBy(id => flag.Evaluate(id, NoProps).Variant!).ToDictionary(g => g.Key, g => g.Count() * 100.0 / n);
        Assert.Equal(["a", "b", "control"], counts.Keys.Order());
        Assert.InRange(counts["control"], 18.5, 21.5);
        Assert.InRange(counts["a"], 28.5, 31.5);
        Assert.InRange(counts["b"], 48.5, 51.5);
    }

    [Fact]
    public void A_zero_percent_variant_is_never_assigned()
    {
        var flag = Flag(variants: [new("control", 0), new("test", 100)]);
        Assert.All(Identities(1000), id => Assert.Equal("test", flag.Evaluate(id, NoProps).Variant));
    }

    [Fact]
    public void Raising_the_rollout_keeps_everyone_already_in_and_their_variant()
    {
        List<FlagVariant> variants = [new("control", 50), new("test", 50)];
        var low = Flag(20, variants);
        var high = Flag(60, variants);
        var ids = Identities(5000).ToList();
        var lowIn = ids.Where(id => low.Evaluate(id, NoProps).Enabled).ToList();
        Assert.NotEmpty(lowIn);
        Assert.All(lowIn, id => Assert.Equal(low.Evaluate(id, NoProps), high.Evaluate(id, NoProps)));
        var share = lowIn.Count(id => low.Evaluate(id, NoProps).Variant == "test") / (double)lowIn.Count;
        Assert.InRange(share, 0.45, 0.55);
    }

    [Fact]
    public void A_new_salt_reshuffles_assignments()
    {
        List<FlagVariant> variants = [new("control", 50), new("test", 50)];
        var a = Flag(variants: variants, salt: "aaaa");
        var b = Flag(variants: variants, salt: "bbbb");
        var ids = Identities(4000).ToList();
        var same = ids.Count(id => a.Evaluate(id, NoProps) == b.Evaluate(id, NoProps)) / (double)ids.Count;
        Assert.InRange(same, 0.45, 0.55);
    }

    [Fact]
    public void Response_values_are_false_true_or_the_variant()
    {
        var flags = new[]
        {
            Flag(key: "off", rollout: 0),
            Flag(key: "on"),
            Flag(key: "multi", variants: [new("only", 100)]),
            Flag(key: "pro-only", conditions: [new("plan", FlagOps.Eq, Json("\"pro\""))]),
        };
        var r = FlagsEndpoint.Evaluate(flags, "id-1", new Dictionary<string, string> { ["plan"] = "free" });
        Assert.Equal(JsonValueKind.False, r["off"].ValueKind);
        Assert.Equal(JsonValueKind.True, r["on"].ValueKind);
        Assert.Equal("only", r["multi"].GetString());
        Assert.Equal(JsonValueKind.False, r["pro-only"].ValueKind);
    }

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

public class FlagTargetingTests
{
    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static bool Matches(string op, string? value, Dictionary<string, string> props, string property = "plan") =>
        new CompiledCondition(new FlagCondition(property, op, value is null ? null : Json(value))).Matches(props);

    static readonly Dictionary<string, string> Pro = new() { ["plan"] = "Pro", ["age"] = "34", ["email"] = "ada@example.com" };
    static readonly Dictionary<string, string> None = [];

    [Theory]
    [InlineData(FlagOps.Eq, "\"pro\"", true)]
    [InlineData(FlagOps.Eq, "\"free\"", false)]
    [InlineData(FlagOps.Neq, "\"free\"", true)]
    [InlineData(FlagOps.Neq, "\"PRO\"", false)]
    [InlineData(FlagOps.In, "[\"free\", \"pro\"]", true)]
    [InlineData(FlagOps.In, "[\"free\", \"team\"]", false)]
    [InlineData(FlagOps.NotIn, "[\"free\", \"team\"]", true)]
    [InlineData(FlagOps.NotIn, "[\"pro\"]", false)]
    [InlineData(FlagOps.IsSet, null, true)]
    [InlineData(FlagOps.IsNotSet, null, false)]
    public void Text_operators(string op, string? value, bool expected) => Assert.Equal(expected, Matches(op, value, Pro));

    [Theory]
    [InlineData(FlagOps.Contains, "\"@EXAMPLE.com\"", true)]
    [InlineData(FlagOps.Contains, "\"@example.org\"", false)]
    [InlineData(FlagOps.NotContains, "\"@example.org\"", true)]
    public void Contains_is_case_insensitive(string op, string value, bool expected) => Assert.Equal(expected, Matches(op, value, Pro, "email"));

    [Theory]
    [InlineData(FlagOps.Gt, "30", true)]
    [InlineData(FlagOps.Gt, "34", false)]
    [InlineData(FlagOps.Gte, "34", true)]
    [InlineData(FlagOps.Lt, "40", true)]
    [InlineData(FlagOps.Lte, "33.5", false)]
    [InlineData(FlagOps.Eq, "34.0", true)]
    [InlineData(FlagOps.Eq, "\"34\"", true)]
    public void Numeric_operators_compare_numbers(string op, string value, bool expected) => Assert.Equal(expected, Matches(op, value, Pro, "age"));

    [Fact]
    public void Missing_properties_only_match_negative_operators()
    {
        Assert.False(Matches(FlagOps.Eq, "\"pro\"", None));
        Assert.False(Matches(FlagOps.In, "[\"pro\"]", None));
        Assert.False(Matches(FlagOps.Contains, "\"p\"", None));
        Assert.False(Matches(FlagOps.Gt, "1", None));
        Assert.False(Matches(FlagOps.IsSet, null, None));
        Assert.True(Matches(FlagOps.Neq, "\"pro\"", None));
        Assert.True(Matches(FlagOps.NotIn, "[\"pro\"]", None));
        Assert.True(Matches(FlagOps.NotContains, "\"p\"", None));
        Assert.True(Matches(FlagOps.IsNotSet, null, None));
    }

    [Fact]
    public void Numeric_operators_fail_on_text()
    {
        Assert.False(Matches(FlagOps.Gt, "1", Pro));
        Assert.False(Matches(FlagOps.Lt, "1", Pro));
    }

    [Fact]
    public void All_conditions_must_match_before_the_rollout()
    {
        var flag = new CompiledFlag("beta", "s", 100, [], [new("plan", FlagOps.Eq, Json("\"pro\"")), new("age", FlagOps.Gte, Json("18"))]);
        Assert.True(flag.Evaluate("x", Pro).Enabled);
        Assert.False(flag.Evaluate("x", new Dictionary<string, string> { ["plan"] = "pro", ["age"] = "17" }).Enabled);
        Assert.False(flag.Evaluate("x", None).Enabled);
    }

    [Fact]
    public void Request_properties_are_parsed_to_text()
    {
        var p = FlagsEndpoint.ParseProperties("""{"plan":"pro","age":34,"beta":true,"gone":null}""")!;
        Assert.Equal("pro", p["plan"]);
        Assert.Equal("34", p["age"]);
        Assert.Equal("true", p["beta"]);
        Assert.False(p.ContainsKey("gone"));
        Assert.Empty(FlagsEndpoint.ParseProperties(null)!);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Invalid_request_properties_are_rejected(string json) => Assert.Null(FlagsEndpoint.ParseProperties(json));

    [Fact]
    public void Nested_request_properties_are_ignored_so_identify_traits_never_break_flags()
    {
        var p = FlagsEndpoint.ParseProperties("""{"plan":"pro","address":{"city":"x"},"tags":[1]}""")!;
        Assert.Equal(["plan"], p.Keys);
    }

    [Fact]
    public void Too_many_request_properties_are_rejected()
    {
        var json = "{" + string.Join(",", Enumerable.Range(0, 51).Select(i => $"\"p{i}\":1")) + "}";
        Assert.Null(FlagsEndpoint.ParseProperties(json));
    }
}

public class FlagValidationTests
{
    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("new-checkout")]
    [InlineData("Beta_2")]
    [InlineData("x")]
    public void Valid_keys(string key) => Assert.Equal(key, FlagRules.Key(key));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("dot.key")]
    [InlineData("ünicode")]
    public void Invalid_keys(string? key) => Assert.Throws<ValidationException>(() => FlagRules.Key(key));

    [Fact]
    public void Key_longer_than_100_is_rejected() => Assert.Throws<ValidationException>(() => FlagRules.Key(new string('a', 101)));

    [Fact]
    public void Variant_percentages_must_add_up_to_100()
    {
        Assert.Equal(2, FlagRules.Variants([new("control", 50), new("test", 50)]).Count);
        var e = Assert.Throws<ValidationException>(() => FlagRules.Variants([new("control", 50), new("test", 40)]));
        Assert.Contains("add up to 100", e.Message);
        Assert.Throws<ValidationException>(() => FlagRules.Variants([new("control", 150), new("test", -50)]));
    }

    [Fact]
    public void Variants_need_two_to_ten_unique_keys()
    {
        Assert.Empty(FlagRules.Variants(null));
        Assert.Throws<ValidationException>(() => FlagRules.Variants([new("only", 100)]));
        Assert.Throws<ValidationException>(() => FlagRules.Variants([new("a", 50), new("a", 50)]));
        Assert.Throws<ValidationException>(() => FlagRules.Variants([new("bad key", 50), new("b", 50)]));
        Assert.Throws<ValidationException>(() => FlagRules.Variants(Enumerable.Range(0, 11).Select(i => new FlagVariant($"v{i}", i == 0 ? 0 : 10)).ToList()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rollout_is_between_0_and_100(int value) => Assert.Throws<ValidationException>(() => FlagRules.Rollout(value));

    [Fact]
    public void Rollout_defaults_to_100() => Assert.Equal(100, FlagRules.Rollout(null));

    [Fact]
    public void Valid_conditions_are_kept()
    {
        var c = FlagRules.Conditions([
            new("plan", FlagOps.In, Json("[\"pro\", \"team\"]")),
            new("age", FlagOps.Gt, Json("18")),
            new("$browser", FlagOps.IsSet),
        ]);
        Assert.Equal(3, c.Count);
        Assert.Null(c[2].Value);
    }

    [Theory]
    [InlineData("plan", "like", "\"x\"")]
    [InlineData("plan", FlagOps.Eq, null)]
    [InlineData("plan", FlagOps.Eq, "null")]
    [InlineData("plan", FlagOps.Eq, "{\"a\":1}")]
    [InlineData("plan", FlagOps.In, "\"pro\"")]
    [InlineData("plan", FlagOps.In, "[]")]
    [InlineData("plan", FlagOps.In, "[[1]]")]
    [InlineData("age", FlagOps.Gt, "\"old\"")]
    [InlineData("bad'name", FlagOps.Eq, "\"x\"")]
    [InlineData("", FlagOps.IsSet, null)]
    public void Invalid_conditions_are_rejected(string property, string op, string? value) =>
        Assert.Throws<ValidationException>(() => FlagRules.Conditions([new(property, op, value is null ? null : Json(value))]));

    [Fact]
    public void At_most_20_conditions() =>
        Assert.Throws<ValidationException>(() => FlagRules.Conditions(Enumerable.Range(0, 21).Select(i => new FlagCondition($"p{i}", FlagOps.IsSet)).ToList()));

    [Fact]
    public void Definitions_round_trip_through_json()
    {
        List<FlagVariant> variants = [new("control", 50), new("test", 50)];
        List<FlagCondition> conditions = [new("plan", FlagOps.Eq, Json("\"pro\"")), new("x", FlagOps.IsSet)];
        Assert.Equal(variants, FlagRules.ParseVariants(FlagRules.VariantsJson(variants)));
        var back = FlagRules.ParseConditions(FlagRules.ConditionsJson(conditions));
        Assert.Equal("pro", back[0].Value!.Value.GetString());
        Assert.Null(back[1].Value);
        Assert.DoesNotContain("null", FlagRules.ConditionsJson(conditions));
    }
}
