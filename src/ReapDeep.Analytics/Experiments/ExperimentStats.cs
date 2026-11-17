namespace ReapDeep.Analytics.Experiments;

public sealed record Comparison(
    double Difference, double? DifferenceLow, double? DifferenceHigh,
    double? RelativeLift, double? RelativeLow, double? RelativeHigh,
    double? Statistic, double? PValue, double? DegreesOfFreedom, bool Significant);

public sealed record SrmCheck(double ChiSquare, double PValue, bool Mismatch);

public static class ExperimentStats
{
    public const double Alpha = 0.05;
    public const double SrmAlpha = 0.001;

    public static Comparison TwoProportion(long controlPersons, long controlConversions, long persons, long conversions)
    {
        Ensure.That(controlPersons > 0 && persons > 0, "Both groups need at least one person");
        Ensure.That(controlConversions >= 0 && controlConversions <= controlPersons && conversions >= 0 && conversions <= persons,
            "Conversions must be between 0 and the number of persons");
        var p1 = (double)controlConversions / controlPersons;
        var p2 = (double)conversions / persons;
        var diff = p2 - p1;

        var pooled = (double)(controlConversions + conversions) / (controlPersons + persons);
        var pooledSe = Math.Sqrt(pooled * (1 - pooled) * (1.0 / controlPersons + 1.0 / persons));
        double? z = pooledSe > 0 ? diff / pooledSe : null;
        double? p = z is { } zz ? Distributions.NormalTwoSidedP(zz) : null;

        var v1 = p1 * (1 - p1) / controlPersons;
        var v2 = p2 * (1 - p2) / persons;
        return Build(diff, p1, p2, v1, v2, Distributions.Z975, z, p, null);
    }

    public static Comparison Welch(long controlPersons, double controlMean, double controlVariance, long persons, double mean, double variance)
    {
        Ensure.That(controlPersons >= 2 && persons >= 2, "Both groups need at least two persons");
        Ensure.That(controlVariance >= 0 && variance >= 0, "Variances cannot be negative");
        var diff = mean - controlMean;
        var v1 = controlVariance / controlPersons;
        var v2 = variance / persons;
        var se2 = v1 + v2;
        if (se2 <= 0) return Build(diff, controlMean, mean, v1, v2, 0, null, null, null);

        var df = se2 * se2 / (v1 * v1 / (controlPersons - 1) + v2 * v2 / (persons - 1));
        var t = diff / Math.Sqrt(se2);
        return Build(diff, controlMean, mean, v1, v2, Distributions.StudentTQuantile975(df), t, Distributions.StudentTTwoSidedP(t, df), df);
    }

    static Comparison Build(double diff, double control, double value, double controlVar, double var, double critical, double? statistic, double? p, double? df)
    {
        var se = Math.Sqrt(controlVar + var);
        double? low = null, high = null, lift = null, liftLow = null, liftHigh = null;
        if (se > 0 && critical > 0)
        {
            low = diff - critical * se;
            high = diff + critical * se;
        }
        if (control > 0)
        {
            lift = value / control - 1;
            var liftSe = Math.Sqrt(var / (control * control) + value * value * controlVar / Math.Pow(control, 4));
            if (liftSe > 0 && critical > 0)
            {
                liftLow = lift - critical * liftSe;
                liftHigh = lift + critical * liftSe;
            }
        }
        return new Comparison(diff, low, high, lift, liftLow, liftHigh, statistic, p, df, p is { } pv && pv < Alpha);
    }

    public static SrmCheck? SampleRatioMismatch(IReadOnlyList<(long Observed, int Percentage)> groups)
    {
        var used = groups.Where(g => g.Percentage > 0).ToList();
        var total = used.Sum(g => g.Observed);
        var weights = used.Sum(g => g.Percentage);
        if (used.Count < 2 || total == 0) return null;
        var chi = used.Sum(g =>
        {
            var expected = (double)total * g.Percentage / weights;
            return (g.Observed - expected) * (g.Observed - expected) / expected;
        });
        var p = Distributions.ChiSquareSurvival(chi, used.Count - 1);
        return new SrmCheck(chi, p, p < SrmAlpha);
    }
}
