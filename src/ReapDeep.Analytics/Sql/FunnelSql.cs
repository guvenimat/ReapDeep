namespace ReapDeep.Analytics.Sql;

public static class FunnelSql
{
    public const string Dropped = "dropped";
    public const string Converted = "converted";
    const string KeepProjections = " LIMIT 9223372036854775807";

    static List<string> Conditions(SqlBuilder b, IReadOnlyList<FunnelStep> steps)
    {
        var conds = steps.Select(st =>
        {
            var parts = new List<string> { $"e.event = {b.P(st.Event)}" };
            parts.AddRange(st.Filters?.Select(b.Condition) ?? []);
            return "(" + string.Join(" AND ", parts) + ")";
        }).ToList();
        b.Where($"e.event IN ({string.Join(", ", steps.Select(st => st.Event).Distinct().Select(e => b.P(e)))})");
        return conds;
    }

    static string Sample(SqlBuilder b, string person, double sampling)
    {
        if (sampling < 1) b.Where($"abs({person}) % 10000 < {(int)Math.Round(sampling * 10000)}");
        return person;
    }

    public static SqlQuery Times(SqlBuilder b, IReadOnlyList<FunnelStep> steps, long windowSeconds, double sampling)
    {
        var conds = Conditions(b, steps);
        var person = Sample(b, $"xx_hash3_64({b.Person()})", sampling);
        const string ts = "unix_timestamp(e.timestamp)";
        var arrays = string.Join(", ", conds.Skip(1).Select((c, k) => $"array_agg(IF({c}, {ts}, NULL)) AS a{k + 2}"));
        var sql = b.Select($"{person} AS person, MIN(IF({conds[0]}, {ts}, NULL)) AS t1, {arrays}", groupBy: "1") + KeepProjections;
        for (var k = 2; k <= steps.Count; k++)
        {
            var keep = Enumerable.Range(1, k - 1).Select(j => $"x.t{j}").Concat(Enumerable.Range(k + 1, steps.Count - k).Select(j => $"x.a{j}"));
            sql = $"SELECT {string.Join(", ", keep)}, " +
                  $"array_min(array_filter(v -> v >= x.t{k - 1} AND v <= x.t1 + {windowSeconds}, x.a{k})) AS t{k} FROM ({sql}) x WHERE x.t1 IS NOT NULL{KeepProjections}";
        }
        var stats = string.Join(", ", Enumerable.Range(2, steps.Count - 1).Select(k =>
            $"COUNT(t{k}), AVG(t{k} - t{k - 1}), percentile_approx(t{k} - t{k - 1}, 0.5)"));
        return b.Build($"SELECT {stats}, AVG(t{steps.Count} - t1), percentile_approx(t{steps.Count} - t1, 0.5) FROM ({sql}) f");
    }

    public static SqlQuery Persons(SqlBuilder b, IReadOnlyList<FunnelStep> steps, long windowSeconds, int step, string kind, int limit)
    {
        var conds = Conditions(b, steps);
        var inner = b.Select($"{b.Person()} AS person, window_funnel({windowSeconds}, e.timestamp, 0, [{string.Join(", ", conds)}]) AS lvl", groupBy: "1");
        var match = kind == Dropped ? $"lvl = {step}" : $"lvl >= {step}";
        return b.Build($"SELECT person, COUNT(*) OVER () AS total FROM ({inner}) f WHERE {match} ORDER BY person LIMIT {limit}");
    }

    public static SqlQuery Build(SqlBuilder b, IReadOnlyList<FunnelStep> steps, long windowSeconds, Breakdown? bd, double sampling)
    {
        var conds = Conditions(b, steps);
        var person = Sample(b, $"xx_hash3_64({b.Person()})", sampling);

        var bdSelect = bd is null ? "" : $", min_by({SqlFragments.BreakdownValue(b, bd)}, IF({conds[0]}, e.timestamp, NULL)) AS bd";

        var inner = b.Select($"{person} AS person{bdSelect}, window_funnel({windowSeconds}, e.timestamp, 0, [{string.Join(", ", conds)}]) AS lvl",
            groupBy: "1");
        var counts = string.Join(", ", steps.Select((_, i) => $"SUM(IF(lvl >= {i + 1}, 1, 0)) AS s{i + 1}"));

        return b.Build(bd is null
            ? $"SELECT NULL AS bd, {counts} FROM ({inner}) f WHERE lvl > 0"
            : $"SELECT bd, {counts} FROM ({inner}) f WHERE lvl > 0 GROUP BY bd ORDER BY s1 DESC LIMIT {SqlFragments.Limit(bd)}");
    }
}
