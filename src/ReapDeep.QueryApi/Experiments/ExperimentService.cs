using System.Diagnostics;
using ReapDeep.Analytics.Experiments;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Experiments;

public sealed class ExperimentService(StarRocksClient starRocks)
{
    public sealed record VariantRow(string? Variant, long Persons, long Converted, double Mean, double? Variance);

    public static ExperimentResultsKey Key(Experiment x, DateTime now)
    {
        var (from, _) = ExperimentRules.Range(x.StartDate, x.EndDate, now);
        DateTime? to = x.EndDate is { } end && end < now ? end : null;
        return new ExperimentResultsKey(x.Id, x.FlagKey, x.Variants, x.ControlVariant, x.Metric, from, to);
    }

    public async Task<ExperimentResults> RunAsync(ProjectContext project, Experiment x, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (from, to) = ExperimentRules.Range(x.StartDate, x.EndDate, DateTime.UtcNow);
        var query = ExperimentSql.Build(new SqlBuilder(project.ProjectId, from, to), x.FlagKey, x.Variants.Select(v => v.Key).ToList(), x.Metric);
        using var gate = StarRocksClient.RequestGate();
        var rows = await starRocks.QueryAsync(query, r => new VariantRow(
            r.IsDBNull(0) ? null : r.GetString(0),
            Convert.ToInt64(r.GetValue(1)),
            r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2)),
            r.IsDBNull(3) ? 0 : Convert.ToDouble(r.GetValue(3)),
            r.IsDBNull(4) ? null : Convert.ToDouble(r.GetValue(4))), gate, ct);
        return Analyse(x, rows, from, to) with { TookMs = sw.Elapsed.TotalMilliseconds };
    }

    public static ExperimentResults Analyse(Experiment x, IReadOnlyList<VariantRow> rows, DateTime from, DateTime to)
    {
        var byKey = rows.Where(r => r.Variant is not null).ToDictionary(r => r.Variant!, StringComparer.Ordinal);
        var excluded = rows.Where(r => r.Variant is null).Sum(r => r.Persons);
        var revenue = x.Metric.Kind == MetricKinds.Revenue;
        var empty = new VariantRow(null, 0, 0, 0, null);
        var control = byKey.GetValueOrDefault(x.ControlVariant) ?? empty;

        var variants = x.Variants.Select(v =>
        {
            var row = byKey.GetValueOrDefault(v.Key) ?? empty;
            var isControl = v.Key == x.ControlVariant;
            var mean = revenue ? row.Mean : row.Persons == 0 ? 0 : (double)row.Converted / row.Persons;
            double? sd = revenue && row.Variance is { } var ? Math.Sqrt(Math.Max(0, var)) : null;
            return new ExperimentVariantResult(v.Key, v.Percentage, isControl, row.Persons, row.Converted, mean, sd,
                isControl ? null : Compare(revenue, control, row));
        }).ToList();

        var srm = ExperimentStats.SampleRatioMismatch(x.Variants.Select(v => ((byKey.GetValueOrDefault(v.Key) ?? empty).Persons, v.Percentage)).ToList());
        return new ExperimentResults(x.Id, x.Metric.Kind ?? MetricKinds.Conversion, revenue ? "welch_t" : "two_proportion_z", x.ControlVariant,
            from, to, variants, excluded, srm is null ? null : new ExperimentSrm(srm.ChiSquare, srm.PValue, srm.Mismatch, ExperimentStats.SrmAlpha), 0);
    }

    static ExperimentComparison? Compare(bool revenue, VariantRow control, VariantRow row)
    {
        Comparison? c;
        if (revenue)
            c = control.Persons >= 2 && row.Persons >= 2
                ? ExperimentStats.Welch(control.Persons, control.Mean, Math.Max(0, control.Variance ?? 0), row.Persons, row.Mean, Math.Max(0, row.Variance ?? 0))
                : null;
        else
            c = control.Persons > 0 && row.Persons > 0 ? ExperimentStats.TwoProportion(control.Persons, control.Converted, row.Persons, row.Converted) : null;
        return c is null ? null : new ExperimentComparison(c.Difference, c.DifferenceLow, c.DifferenceHigh, c.RelativeLift, c.RelativeLow, c.RelativeHigh,
            c.Statistic, c.PValue, c.DegreesOfFreedom, c.Significant);
    }
}
