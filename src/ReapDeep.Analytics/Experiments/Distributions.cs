namespace ReapDeep.Analytics.Experiments;

public static class Distributions
{
    public const double Z975 = 1.959963984540054;
    const double Sqrt2 = 1.4142135623730951;
    const double Epsilon = 1e-16;
    const double Tiny = 1e-300;
    const int MaxIterations = 10_000;

    static readonly double[] Lanczos =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
        12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    public static double LogGamma(double x)
    {
        if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        x -= 1;
        var a = Lanczos[0];
        var t = x + 7.5;
        for (var i = 1; i < Lanczos.Length; i++) a += Lanczos[i] / (x + i);
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    public static double GammaP(double a, double x) => x <= 0 ? 0 : x < a + 1 ? GammaSeries(a, x) : 1 - GammaContinuedFraction(a, x);

    public static double GammaQ(double a, double x) => x <= 0 ? 1 : x < a + 1 ? 1 - GammaSeries(a, x) : GammaContinuedFraction(a, x);

    static double GammaSeries(double a, double x)
    {
        var ap = a;
        var sum = 1 / a;
        var del = sum;
        for (var n = 0; n < MaxIterations; n++)
        {
            ap++;
            del *= x / ap;
            sum += del;
            if (Math.Abs(del) < Math.Abs(sum) * Epsilon) break;
        }
        return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
    }

    static double GammaContinuedFraction(double a, double x)
    {
        var b = x + 1 - a;
        var c = 1 / Tiny;
        var d = 1 / b;
        var h = d;
        for (var i = 1; i < MaxIterations; i++)
        {
            var an = -i * (i - a);
            b += 2;
            d = an * d + b;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = b + an / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            var del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < Epsilon) break;
        }
        return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
    }

    public static double RegularizedBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        var front = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        return x < (a + 1) / (a + b + 2)
            ? front * BetaContinuedFraction(x, a, b) / a
            : 1 - front * BetaContinuedFraction(1 - x, b, a) / b;
    }

    static double BetaContinuedFraction(double x, double a, double b)
    {
        var qab = a + b;
        var qap = a + 1;
        var qam = a - 1;
        var c = 1.0;
        var d = 1 - qab * x / qap;
        if (Math.Abs(d) < Tiny) d = Tiny;
        d = 1 / d;
        var h = d;
        for (var m = 1; m < MaxIterations; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            var del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < Epsilon) break;
        }
        return h;
    }

    public static double Erfc(double x) => x >= 0 ? GammaQ(0.5, x * x) : 2 - GammaQ(0.5, x * x);

    public static double NormalCdf(double z) => 0.5 * Erfc(-z / Sqrt2);

    public static double NormalQuantile(double p)
    {
        Ensure.That(p is > 0 and < 1, "p must be between 0 and 1");
        var low = -10.0;
        var high = 10.0;
        for (var i = 0; i < 100; i++)
        {
            var mid = (low + high) / 2;
            if (NormalCdf(mid) < p) low = mid;
            else high = mid;
        }
        return (low + high) / 2;
    }

    public static double NormalTwoSidedP(double z) => Erfc(Math.Abs(z) / Sqrt2);

    public static double StudentTCdf(double t, double df)
    {
        var tail = 0.5 * RegularizedBeta(df / (df + t * t), df / 2, 0.5);
        return t > 0 ? 1 - tail : tail;
    }

    public static double StudentTTwoSidedP(double t, double df) => RegularizedBeta(df / (df + t * t), df / 2, 0.5);

    public static double StudentTQuantile975(double df)
    {
        double lo = 0, hi = 2;
        while (StudentTTwoSidedP(hi, df) > 0.05) hi *= 2;
        for (var i = 0; i < 200 && hi - lo > 1e-13 * hi; i++)
        {
            var mid = (lo + hi) / 2;
            if (StudentTTwoSidedP(mid, df) > 0.05) lo = mid;
            else hi = mid;
        }
        return (lo + hi) / 2;
    }

    public static double ChiSquareSurvival(double x, double df) => x <= 0 ? 1 : GammaQ(df / 2, x / 2);
}
