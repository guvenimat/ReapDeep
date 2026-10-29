namespace ReapDeep.QueryApi.Analytics.Sql;

public static class PathSql
{
    public const string Separator = "\u001f";
    static readonly string[] Excluded = ["$identify", "$set"];

    public static SqlQuery Steps(SqlBuilder b, string startEvent, int depth, int windowSeconds, bool pageviewPaths) =>
        b.Build(Ctes(b, startEvent, windowSeconds, pageviewPaths) +
                $"SELECT rn, node, COUNT(*) AS n FROM ranked WHERE rn <= {depth} GROUP BY rn, node");

    public static SqlQuery Sequences(SqlBuilder b, string startEvent, int depth, int windowSeconds, bool pageviewPaths, int limit) =>
        b.Build(Ctes(b, startEvent, windowSeconds, pageviewPaths) +
                $"SELECT path, COUNT(*) AS n FROM (SELECT person, group_concat(node ORDER BY rn SEPARATOR '{Separator}') AS path " +
                $"FROM ranked WHERE rn <= {depth} GROUP BY person) x GROUP BY path ORDER BY n DESC, path LIMIT {limit}");

    static string Ctes(SqlBuilder b, string startEvent, int windowSeconds, bool pageviewPaths)
    {
        b.Where($"e.event NOT IN ({string.Join(", ", Excluded.Select(x => b.P(x)))})");
        var node = pageviewPaths
            ? $"IF(e.event = {b.P("$pageview")}, concat({b.P("$pageview ")}, COALESCE(e.pathname, '')), e.event)"
            : "e.event";
        var ev = b.Select($"{b.Person()} AS person, e.timestamp AS ts, e.event_id AS eid, {node} AS node, e.event = {b.P(startEvent)} AS is_start");
        const string order = "ORDER BY ts, is_start DESC, eid";
        return $"WITH ev AS ({ev}), " +
               "s AS (SELECT person, MIN(ts) AS t0 FROM ev WHERE is_start GROUP BY person), " +
               $"seq AS (SELECT ev.person, ev.ts, ev.eid, ev.node, ev.is_start, LAG(ev.node) OVER (PARTITION BY ev.person {order}) AS prev " +
               $"FROM ev JOIN s ON s.person = ev.person AND ev.ts >= s.t0 AND ev.ts < seconds_add(s.t0, {windowSeconds})), " +
               $"ranked AS (SELECT person, node, ROW_NUMBER() OVER (PARTITION BY person {order}) AS rn FROM seq WHERE prev IS NULL OR prev <> node) ";
    }
}
