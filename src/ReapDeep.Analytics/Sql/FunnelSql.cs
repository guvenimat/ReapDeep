namespace ReapDeep.Analytics.Sql;

public static class FunnelSql
{
    public static SqlQuery Build(SqlBuilder b, IReadOnlyList<FunnelStep> steps, long windowSeconds, Breakdown? bd, double sampling)
    {
        var conds = steps.Select(st =>
        {
            var parts = new List<string> { $"e.event = {b.P(st.Event)}" };
            parts.AddRange(st.Filters?.Select(b.Condition) ?? []);
            return "(" + string.Join(" AND ", parts) + ")";
        }).ToList();

        b.Where($"e.event IN ({string.Join(", ", steps.Select(st => st.Event).Distinct().Select(e => b.P(e)))})");
        var person = $"xx_hash3_64({b.Person()})";
        if (sampling < 1) b.Where($"abs({person}) % 10000 < {(int)Math.Round(sampling * 10000)}");

        var bdSelect = bd is null ? "" : $", min_by({SqlFragments.BreakdownValue(b, bd)}, IF({conds[0]}, e.timestamp, NULL)) AS bd";

        var inner = b.Select($"{person} AS person{bdSelect}, window_funnel({windowSeconds}, e.timestamp, 0, [{string.Join(", ", conds)}]) AS lvl",
            groupBy: "1");
        var counts = string.Join(", ", steps.Select((_, i) => $"SUM(IF(lvl >= {i + 1}, 1, 0)) AS s{i + 1}"));

        return b.Build(bd is null
            ? $"SELECT NULL AS bd, {counts} FROM ({inner}) f WHERE lvl > 0"
            : $"SELECT bd, {counts} FROM ({inner}) f WHERE lvl > 0 GROUP BY bd ORDER BY s1 DESC LIMIT {SqlFragments.Limit(bd)}");
    }
}
