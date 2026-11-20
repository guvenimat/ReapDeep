using Microsoft.Extensions.Logging.Abstractions;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Experiments;
using ReapDeep.Analytics.Flags;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Experiments;

namespace ReapDeep.UnitTests;

public class DistributionTests
{
    static void Close(double expected, double actual, double relative = 1e-10)
    {
        var tolerance = Math.Max(Math.Abs(expected) * relative, 1e-300);
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected:R}, got {actual:R}");
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.5, 0.6914624612740131)]
    [InlineData(1.0, 0.8413447460685429)]
    [InlineData(1.959963984540054, 0.975)]
    [InlineData(2.5, 0.9937903346742238)]
    [InlineData(-1.3, 0.09680048458561036)]
    [InlineData(3.5, 0.9997673709209645)]
    [InlineData(6.0, 0.9999999990134123)]
    [InlineData(-8.0, 6.22096057427174e-16)]
    public void Normal_cdf_matches_scipy(double z, double expected) => Close(expected, Distributions.NormalCdf(z), 1e-9);

    [Theory]
    [InlineData(0.1, 0.8875370839817152)]
    [InlineData(1.0, 0.15729920705028516)]
    [InlineData(2.0, 0.004677734981047266)]
    [InlineData(4.0, 1.541725790028002e-08)]
    [InlineData(6.0, 2.1519736712498913e-17)]
    public void Erfc_matches_scipy_also_in_the_far_tail(double x, double expected) => Close(expected, Distributions.Erfc(x), 1e-9);

    [Theory]
    [InlineData(2.0, 5, 0.9490302605850709)]
    [InlineData(-1.5, 10, 0.08225366322272008)]
    [InlineData(0.7, 3.5, 0.7361829288271604)]
    [InlineData(3.0, 100.25, 0.9982969788937842)]
    [InlineData(2.2, 1, 0.8642002512199082)]
    [InlineData(5.0, 2000, 0.9999996883551082)]
    public void Student_t_cdf_matches_scipy(double t, double df, double expected) => Close(expected, Distributions.StudentTCdf(t, df), 1e-9);

    [Theory]
    [InlineData(1, 12.706204736432095)]
    [InlineData(2, 4.302652729696142)]
    [InlineData(5, 2.570581835636314)]
    [InlineData(10, 2.2281388519649385)]
    [InlineData(30, 2.0422724563012373)]
    [InlineData(57.3, 2.002238268231336)]
    [InlineData(1000, 1.9623390808264074)]
    [InlineData(100000, 1.9599877075346093)]
    public void Student_t_97_5_percent_quantile_matches_scipy(double df, double expected) => Close(expected, Distributions.StudentTQuantile975(df), 1e-9);

    [Theory]
    [InlineData(1.0, 1, 0.31731050786291115)]
    [InlineData(3.84, 1, 0.05004352124870519)]
    [InlineData(5.99, 2, 0.05003662708658629)]
    [InlineData(20.0, 1, 7.744216431044088e-06)]
    [InlineData(10.0, 4, 0.04042768199451279)]
    [InlineData(0.5, 3, 0.9188914116546758)]
    [InlineData(50.0, 9, 1.0772382022574693e-07)]
    public void Chi_square_survival_matches_scipy(double x, double df, double expected) => Close(expected, Distributions.ChiSquareSurvival(x, df), 1e-9);

    [Theory]
    [InlineData(0.5, 0.5723649429247)]
    [InlineData(1, 0.0)]
    [InlineData(4.5, 2.4537365708424423)]
    [InlineData(10, 12.801827480081469)]
    [InlineData(100.5, 361.43554046777757)]
    public void Log_gamma_matches_scipy(double x, double expected) => Assert.Equal(expected, Distributions.LogGamma(x), 1e-12);

    [Theory]
    [InlineData(0.3, 2, 3, 0.34829999999999994)]
    [InlineData(0.9, 0.5, 12.5, 0.9999999999999476)]
    [InlineData(0.05, 30, 0.5, 9.793216739467787e-41)]
    public void Regularized_beta_matches_scipy(double x, double a, double b, double expected) => Close(expected, Distributions.RegularizedBeta(x, a, b), 1e-9);
}

public class ExperimentStatsTests
{
    static void Close(double expected, double? actual, double relative = 1e-9)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - actual.Value) <= Math.Abs(expected) * relative + 1e-15, $"expected {expected:R}, got {actual:R}");
    }

    [Theory]
    [InlineData(200, 40, 200, 60, 2.3094010767585025, 0.020921335337794035, 0.01569876146648773, 0.1843012385335122, 0.5, -0.02316722085243128, 1.023167220852431)]
    [InlineData(1000, 100, 1000, 130, 2.102740605622114, 0.03548845046647471, 0.0020679344393763656, 0.057932065560623636, 0.3, -0.01917939771358701, 0.6191793977135871)]
    [InlineData(5000, 500, 4900, 510, 0.6708462365743366, 0.5023184891071499, -0.007845249205093389, 0.016008514511215825, 0.04081632653061207, -0.08084315693763999, 0.16247580999886413)]
    public void Two_proportion_z_test_matches_reference_values(long n1, long x1, long n2, long x2,
        double z, double p, double low, double high, double lift, double liftLow, double liftHigh)
    {
        var c = ExperimentStats.TwoProportion(n1, x1, n2, x2);
        Close(z, c.Statistic);
        Close(p, c.PValue, 1e-8);
        Close(low, c.DifferenceLow);
        Close(high, c.DifferenceHigh);
        Close(lift, c.RelativeLift);
        Close(liftLow, c.RelativeLow);
        Close(liftHigh, c.RelativeHigh);
        Assert.Equal(p < 0.05, c.Significant);
        Assert.Null(c.DegreesOfFreedom);
    }

    [Theory]
    [InlineData(300, 4.0, 64.21404682274247, 200, 8.5, 233.91959798994975,
        3.825605684996192, 0.00016180042242849225, 272.4298074252528, 2.1842373262785215, 6.815762673721478, 1.125, 0.40565381620345, 1.84434618379655)]
    [InlineData(5, 3.0, 2.5, 6, 7.0, 14.0,
        2.3763541031440183, 0.04928433820673049, 6.9722557297949335, 0.01653130437117012, 7.983468695628829, 1.3333333333333335, -0.44034868019297413, 3.107015346859641)]
    public void Welch_t_test_matches_scipy_ttest_ind(long n1, double m1, double v1, long n2, double m2, double v2,
        double t, double p, double df, double low, double high, double lift, double liftLow, double liftHigh)
    {
        var c = ExperimentStats.Welch(n1, m1, v1, n2, m2, v2);
        Close(t, c.Statistic);
        Close(p, c.PValue, 1e-8);
        Close(df, c.DegreesOfFreedom);
        Close(low, c.DifferenceLow, 1e-8);
        Close(high, c.DifferenceHigh, 1e-8);
        Close(lift, c.RelativeLift);
        Close(liftLow, c.RelativeLow, 1e-8);
        Close(liftHigh, c.RelativeHigh, 1e-8);
        Assert.True(c.Significant);
    }

    [Fact]
    public void Equal_groups_without_variance_have_no_test_and_are_not_significant()
    {
        var w = ExperimentStats.Welch(10, 5, 0, 10, 5, 0);
        Assert.Null(w.PValue);
        Assert.Null(w.DifferenceLow);
        Assert.False(w.Significant);
        var z = ExperimentStats.TwoProportion(100, 0, 100, 0);
        Assert.Null(z.Statistic);
        Assert.Null(z.RelativeLift);
        Assert.False(z.Significant);
    }

    [Fact]
    public void Invalid_counts_are_rejected()
    {
        Assert.Throws<ValidationException>(() => ExperimentStats.TwoProportion(0, 0, 10, 1));
        Assert.Throws<ValidationException>(() => ExperimentStats.TwoProportion(10, 11, 10, 1));
        Assert.Throws<ValidationException>(() => ExperimentStats.Welch(1, 1, 0, 10, 1, 1));
    }

    [Theory]
    [InlineData(300, 200, 50, 50, 20.0, 7.744216431044088e-06, true)]
    [InlineData(200, 200, 50, 50, 0.0, 1.0, false)]
    [InlineData(480, 520, 50, 50, 1.6, 0.20590321073206466, false)]
    public void Sample_ratio_mismatch_matches_scipy_chisquare(long a, long b, int pa, int pb, double chi, double p, bool mismatch)
    {
        var srm = ExperimentStats.SampleRatioMismatch([(a, pa), (b, pb)]);
        Assert.NotNull(srm);
        Assert.Equal(chi, srm.ChiSquare, 1e-9);
        Assert.Equal(p, srm.PValue, 1e-12);
        Assert.Equal(mismatch, srm.Mismatch);
    }

    [Fact]
    public void Sample_ratio_mismatch_with_three_variants_and_zero_percent_variants()
    {
        var srm = ExperimentStats.SampleRatioMismatch([(1000, 25), (1050, 25), (2010, 50), (0, 0)]);
        Assert.NotNull(srm);
        Assert.Equal(1.625615763546798, srm.ChiSquare, 1e-9);
        Assert.Equal(0.4436107094018199, srm.PValue, 1e-9);
        Assert.Null(ExperimentStats.SampleRatioMismatch([(0, 50), (0, 50)]));
    }
}

public class ExperimentRuleTests
{
    static readonly List<FlagVariant> Variants = [new("control", 50), new("test", 50)];

    [Fact]
    public void Metric_kinds_are_validated_and_normalized()
    {
        Assert.Equal(new ExperimentMetric("conversion", "purchase"), ExperimentRules.Metric(new("conversion", " purchase ")));
        var funnel = ExperimentRules.Metric(new("funnel", Steps: ["add_to_cart", "purchase"]));
        Assert.Equal(ExperimentRules.DefaultFunnelWindowSeconds, funnel.WindowSeconds);
        Assert.Equal("amount", ExperimentRules.Metric(new("revenue", "purchase", Property: "amount")).Property);
        Assert.Throws<ValidationException>(() => ExperimentRules.Metric(null));
        Assert.Throws<ValidationException>(() => ExperimentRules.Metric(new("clicks", "x")));
        Assert.Throws<ValidationException>(() => ExperimentRules.Metric(new("conversion", " ")));
        Assert.Throws<ValidationException>(() => ExperimentRules.Metric(new("conversion", "$feature_flag_called")));
        Assert.Throws<ValidationException>(() => ExperimentRules.Metric(new("funnel", Steps: ["one"])));
        Assert.Throws<ValidationException>(() => ExperimentRules.Metric(new("revenue", "purchase")));
        Assert.Throws<ValidationException>(() => ExperimentRules.Metric(new("revenue", "purchase", Property: "a'b")));
        Assert.Throws<ValidationException>(() => ExperimentRules.Metric(new("conversion", "purchase", WindowSeconds: 0)));
    }

    [Fact]
    public void Control_defaults_to_the_first_variant_and_must_exist()
    {
        Assert.Equal("control", ExperimentRules.Control(null, Variants));
        Assert.Equal("test", ExperimentRules.Control("test", Variants));
        Assert.Throws<ValidationException>(() => ExperimentRules.Control("other", Variants));
        Assert.Throws<ValidationException>(() => ExperimentRules.Control(null, []));
    }

    [Fact]
    public void Variants_are_locked_on_any_key_percentage_or_order_change()
    {
        Assert.True(ExperimentRules.SameVariants(Variants, [new("control", 50), new("test", 50)]));
        Assert.False(ExperimentRules.SameVariants(Variants, [new("control", 40), new("test", 60)]));
        Assert.False(ExperimentRules.SameVariants(Variants, [new("test", 50), new("control", 50)]));
        Assert.False(ExperimentRules.SameVariants(Variants, [new("control", 50), new("b", 50)]));
        Assert.False(ExperimentRules.SameVariants(Variants, []));
    }

    [Fact]
    public void Results_range_runs_from_the_start_to_the_end_or_now()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var start = now.AddDays(-3);
        Assert.Equal((start, now), ExperimentRules.Range(start, null, now));
        Assert.Equal((start, now.AddDays(-1)), ExperimentRules.Range(start, now.AddDays(-1), now));
        Assert.Equal((start, now), ExperimentRules.Range(start, now.AddDays(5), now));
        Assert.Throws<ValidationException>(() => ExperimentRules.Range(null, null, now));
        Assert.Throws<ValidationException>(() => ExperimentRules.Range(now.AddHours(1), null, now));
        Assert.Throws<ValidationException>(() => ExperimentRules.Range(now.AddDays(-400), null, now));
    }

    [Fact]
    public void Metric_json_round_trips()
    {
        var metric = new ExperimentMetric("funnel", Steps: ["a", "b"], WindowSeconds: 3600);
        var json = ExperimentRules.MetricJson(metric);
        Assert.Equal("""{"kind":"funnel","steps":["a","b"],"window_seconds":3600}""", json);
        var parsed = ExperimentRules.ParseMetric(json);
        Assert.Equal(metric.Steps, parsed.Steps);
        Assert.Equal(3600, parsed.WindowSeconds);
    }
}

public class ExperimentSqlTests
{
    const long Project = 42;
    static readonly DateTime From = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime To = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

    static SqlQuery Build(ExperimentMetric metric, params string[] variants) =>
        ExperimentSql.Build(new SqlBuilder(Project, From, To), "new-checkout", variants.Length == 0 ? ["control", "test"] : variants, metric);

    public static TheoryData<string> Kinds => new() { "conversion", "funnel", "revenue" };

    static ExperimentMetric Metric(string kind) => kind switch
    {
        "funnel" => new("funnel", Steps: ["add_to_cart", "purchase"], WindowSeconds: 3600),
        "revenue" => new("revenue", "purchase", Property: "amount"),
        _ => new("conversion", "purchase", WindowSeconds: 86400),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_metric_keeps_the_tenant_and_time_filter_and_the_person_left_join(string kind)
    {
        var q = Build(Metric(kind));
        Assert.Contains("e.project_id = @p0", q.Sql);
        Assert.Contains("e.timestamp >= @p1", q.Sql);
        Assert.Contains("e.timestamp < @p2", q.Sql);
        Assert.Equal(Project, q.Parameters[0].Value);
        Assert.Equal(From, q.Parameters[1].Value);
        Assert.Equal(To, q.Parameters[2].Value);
        Assert.Contains("COALESCE(pdi.person_id, e.distinct_id) AS person", q.Sql);
        Assert.Contains("LEFT JOIN analytics.person_distinct_ids pdi", q.Sql);
        Assert.DoesNotContain("INNER JOIN", q.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?<!LEFT )JOIN", q.Sql);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Values_are_parameters_and_never_part_of_the_sql_text(string kind)
    {
        var q = ExperimentSql.Build(new SqlBuilder(Project, From, To), "x' OR 1=1 --", ["control", "b'c"], Metric(kind));
        Assert.DoesNotContain("x' OR 1=1", q.Sql);
        Assert.DoesNotContain("b'c", q.Sql);
        Assert.DoesNotContain("purchase", q.Sql);
        Assert.Contains(q.Parameters, p => p.Value as string == "x' OR 1=1 --");
        Assert.Contains(q.Parameters, p => p.Value as string == "$feature_flag_called");
        Assert.Contains(q.Parameters, p => p.Value as string == "$.\"$feature_flag\"");
        Assert.Contains(q.Parameters, p => p.Value as string == "$.\"$feature_flag_response\"");
    }

    [Fact]
    public void Exposure_uses_the_first_exposure_and_excludes_persons_with_several_variants()
    {
        var q = Build(Metric("conversion"));
        Assert.Contains("MIN(ts) AS first_ts", q.Sql);
        Assert.Contains("COUNT(DISTINCT variant) AS variants", q.Sql);
        Assert.Contains("IF(x.variants = 1, x.variant, NULL) AS variant", q.Sql);
        Assert.Contains("m.ts >= x.first_ts", q.Sql);
        Assert.Contains("m.ts <= seconds_add(x.first_ts, 86400)", q.Sql);
    }

    [Fact]
    public void Boolean_responses_are_never_variants()
    {
        var q = Build(Metric("conversion"), "control", "false", "true", "test");
        Assert.DoesNotContain(q.Parameters, p => p.Value as string is "false" or "true");
        Assert.Throws<ValidationException>(() => Build(Metric("conversion"), "false", "true"));
    }

    [Fact]
    public void Funnel_and_revenue_use_their_own_aggregates()
    {
        var funnel = Build(Metric("funnel"));
        Assert.Contains("window_funnel(3600, m.ts, 0, [", funnel.Sql);
        Assert.Contains("IF(f.lvl >= 2, 1, 0) AS value", funnel.Sql);
        Assert.DoesNotContain("seconds_add", funnel.Sql);
        var revenue = Build(Metric("revenue"));
        Assert.Contains("SUM(m.amount) AS value", revenue.Sql);
        Assert.Contains("CAST(get_json_string(e.properties, @p", revenue.Sql);
        Assert.Contains(revenue.Parameters, p => p.Value as string == "$.\"amount\"");
        Assert.Contains("VAR_SAMP(COALESCE(o.value, 0))", revenue.Sql);
    }
}

public class ExperimentResultTests
{
    static Experiment Experiment(string kind, string control = "control") => new(1, "Checkout", "", 7, "new-checkout", true,
        [new("control", 50), new("test", 50)], control, ExperimentStatus.Running, new ExperimentMetric(kind, "purchase", Property: kind == "revenue" ? "amount" : null),
        [], DateTime.UtcNow.AddDays(-1), null, null, null, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void Conversion_results_compare_every_variant_with_the_control_and_report_exclusions()
    {
        var rows = new List<ExperimentService.VariantRow> { new("control", 200, 40, 0.2, null), new("test", 200, 60, 0.3, null), new(null, 10, 10, 1, null) };
        var r = ExperimentService.Analyse(Experiment("conversion"), rows, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.Equal(10, r.ExcludedPersons);
        Assert.Equal("two_proportion_z", r.Test);
        var control = r.Variants.Single(v => v.Key == "control");
        Assert.True(control.IsControl);
        Assert.Null(control.Comparison);
        var test = r.Variants.Single(v => v.Key == "test");
        Assert.Equal(0.3, test.Mean, 1e-12);
        Assert.Equal(2.3094010767585025, test.Comparison!.Statistic!.Value, 1e-9);
        Assert.Equal(0.020921335337794035, test.Comparison.PValue!.Value, 1e-9);
        Assert.True(test.Comparison.Significant);
        Assert.NotNull(r.Srm);
        Assert.False(r.Srm.Warning);
    }

    [Fact]
    public void A_chosen_control_and_missing_variants_are_handled()
    {
        var rows = new List<ExperimentService.VariantRow> { new("control", 300, 0, 4.0, 64.21404682274247), new("test", 200, 0, 8.5, 233.91959798994975) };
        var r = ExperimentService.Analyse(Experiment("revenue", control: "test"), rows, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.Equal("welch_t", r.Test);
        Assert.Equal(-3.825605684996192, r.Variants.Single(v => v.Key == "control").Comparison!.Statistic!.Value, 1e-9);
        Assert.True(r.Srm!.Warning);

        var none = ExperimentService.Analyse(Experiment("conversion"), [], DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.All(none.Variants, v => Assert.Equal(0, v.Persons));
        Assert.Null(none.Variants.Single(v => v.Key == "test").Comparison);
        Assert.Null(none.Srm);
    }

    [Fact]
    public async Task Secondary_metric_failure_is_reported_on_that_metric_only()
    {
        var metric = new ExperimentMetric("conversion", "purchase");
        var r = await ExperimentService.SecondaryAsync(metric, () => throw new InvalidOperationException("boom"), NullLogger.Instance, CancellationToken.None);
        Assert.NotNull(r.Error);
        Assert.Empty(r.Variants);
        Assert.Same(metric, r.Metric);
    }

    [Fact]
    public async Task Secondary_metric_failure_after_cancellation_is_rethrown()
    {
        var metric = new ExperimentMetric("conversion", "purchase");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExperimentService.SecondaryAsync(metric,
            () => throw new OperationCanceledException(), NullLogger.Instance, new CancellationToken(true)));
    }
}

public class SampleSizeTests
{
    [Fact]
    public void Normal_quantiles_match_reference_values()
    {
        Assert.Equal(1.959964, Distributions.NormalQuantile(0.975), 6);
        Assert.Equal(0.841621, Distributions.NormalQuantile(0.8), 6);
    }

    [Fact]
    public void Two_proportion_sample_size_matches_the_formula()
    {
        var size = ExperimentStats.ForTwoProportions(0.1, 0.2);

        Assert.Equal(3841, size.PerVariant);
        Assert.Equal(7682, size.Total);
        Assert.Equal(0.1, size.BaselineRate);
        Assert.Equal(0.2, size.RelativeLift);
        Assert.Equal(2, size.Variants);
    }

    [Fact]
    public void More_variants_multiply_the_total_but_not_the_per_variant_count()
    {
        var three = ExperimentStats.ForTwoProportions(0.1, 0.2, variants: 3);
        Assert.Equal(3841, three.PerVariant);
        Assert.Equal(11523, three.Total);
    }
}
