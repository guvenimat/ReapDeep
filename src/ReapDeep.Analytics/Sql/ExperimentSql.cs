using ReapDeep.Analytics.Experiments;

namespace ReapDeep.Analytics.Sql;

public static class ExperimentSql
{
    public const string ExposureEvent = "$feature_flag_called";
    public const string FlagProperty = "$feature_flag";
    public const string ResponseProperty = "$feature_flag_response";
    static readonly string[] NotVariants = ["true", "false"];

    public static SqlQuery Build(SqlBuilder b, string flagKey, IReadOnlyList<string> variants, ExperimentMetric metric)
    {
        var keys = variants.Where(v => !NotVariants.Contains(v)).Distinct().ToList();
        Ensure.That(keys.Count > 0, "The flag has no variants to analyse");
        var events = metric.Kind == MetricKinds.Funnel ? metric.Steps!.Distinct().ToList() : [metric.Event!];

        var response = ColumnMapper.Expression(b, "event", ResponseProperty);
        var exposure = $"e.event = {b.P(ExposureEvent)} AND {ColumnMapper.Expression(b, "event", FlagProperty)} = {b.P(flagKey)} " +
                       $"AND {response} IN ({string.Join(", ", keys.Select(k => b.P(k)))})";
        b.Where($"(({exposure}) OR e.event IN ({string.Join(", ", events.Select(e => b.P(e)))}))");
        var amount = metric.Kind == MetricKinds.Revenue ? $"CAST({ColumnMapper.Expression(b, "event", metric.Property!)} AS DOUBLE)" : "NULL";
        var rows = b.Select($"{b.Person()} AS person, e.timestamp AS ts, e.event AS event, IF({exposure}, {response}, NULL) AS variant, {amount} AS amount");

        const string joined = "FROM base m LEFT JOIN exposures x ON x.person = m.person WHERE m.variant IS NULL AND x.first_ts IS NOT NULL AND m.ts >= x.first_ts";
        var window = metric.Kind != MetricKinds.Funnel && metric.WindowSeconds is { } w ? $" AND m.ts <= seconds_add(x.first_ts, {w})" : "";
        var outcomes = metric.Kind switch
        {
            MetricKinds.Revenue => $"SELECT m.person, SUM(m.amount) AS value {joined}{window} GROUP BY m.person",
            MetricKinds.Funnel =>
                $"SELECT f.person, IF(f.lvl >= {metric.Steps!.Count}, 1, 0) AS value FROM (SELECT m.person, " +
                $"window_funnel({metric.WindowSeconds ?? ExperimentRules.DefaultFunnelWindowSeconds}, m.ts, 0, [{string.Join(", ", metric.Steps.Select(s => $"m.event = {b.P(s)}"))}]) AS lvl " +
                $"{joined} GROUP BY m.person) f",
            _ => $"SELECT m.person, 1 AS value {joined}{window} GROUP BY m.person",
        };

        return b.Build(
            $"WITH base AS ({rows}), " +
            "exposures AS (SELECT person, MIN(ts) AS first_ts, MIN(variant) AS variant, COUNT(DISTINCT variant) AS variants FROM base WHERE variant IS NOT NULL GROUP BY person), " +
            $"outcomes AS ({outcomes}) " +
            "SELECT IF(x.variants = 1, x.variant, NULL) AS variant, COUNT(*) AS persons, SUM(IF(o.value > 0, 1, 0)) AS converted, " +
            "AVG(COALESCE(o.value, 0)) AS mean, VAR_SAMP(COALESCE(o.value, 0)) AS variance " +
            "FROM exposures x LEFT JOIN outcomes o ON o.person = x.person GROUP BY 1");
    }
}
