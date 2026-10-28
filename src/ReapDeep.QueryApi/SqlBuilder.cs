using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace ReapDeep.QueryApi;

public sealed class SqlBuilder
{
    public const string PersonExpr = "COALESCE(pdi.person_id, e.distinct_id)";

    readonly List<(string Name, object Value)> _params = [];
    readonly List<string> _where = [];
    bool _joinPdi;
    bool _joinPersons;

    public SqlBuilder(long projectId, DateTime fromUtc, DateTime toUtc)
    {
        _where.Add($"e.project_id = {P(projectId)}");
        _where.Add($"e.timestamp >= {P(fromUtc)}");
        _where.Add($"e.timestamp < {P(toUtc)}");
    }

    public IReadOnlyList<(string Name, object Value)> Parameters => _params;

    public string P(object value)
    {
        var name = "@p" + _params.Count;
        _params.Add((name, value));
        return name;
    }

    public void Where(string condition) => _where.Add(condition);

    public string Person()
    {
        _joinPdi = true;
        return PersonExpr;
    }

    public void RequirePersons() => _joinPdi = _joinPersons = true;

    public void AddFilters(IEnumerable<Filter>? filters)
    {
        if (filters is null) return;
        foreach (var f in filters) _where.Add(Condition(f));
    }

    public string Condition(Filter f)
    {
        var expr = ColumnMapper.Expression(this, f.Scope, f.Property);
        return f.Op switch
        {
            "eq" => $"{expr} = {P(Scalar(f))}",
            "neq" => $"({expr} IS NULL OR {expr} <> {P(Scalar(f))})",
            "contains" => $"{expr} LIKE {P("%" + EscapeLike(Scalar(f)) + "%")}",
            "not_contains" => $"({expr} IS NULL OR {expr} NOT LIKE {P("%" + EscapeLike(Scalar(f)) + "%")})",
            "in" => $"{expr} IN ({string.Join(", ", List(f).Select(v => P(v)))})",
            "is_set" => $"{expr} IS NOT NULL",
            "is_not_set" => $"{expr} IS NULL",
            _ => throw new QueryException($"Invalid op: '{f.Op}' (eq, neq, contains, not_contains, in, is_set, is_not_set)"),
        };
    }

    public string Select(string select, string? groupBy = null, string? orderBy = null, int? limit = null)
    {
        var sb = new StringBuilder("SELECT ").Append(select).Append(" FROM analytics.events e");
        if (_joinPdi)
            sb.Append(" LEFT JOIN analytics.person_distinct_ids pdi ON e.project_id = pdi.project_id AND e.distinct_id = pdi.distinct_id AND pdi.is_deleted = 0");
        if (_joinPersons)
            sb.Append(" LEFT JOIN analytics.persons p ON e.project_id = p.project_id AND pdi.person_id = p.person_id AND p.is_deleted = 0");
        sb.Append(" WHERE ").AppendJoin(" AND ", _where);
        if (groupBy is not null) sb.Append(" GROUP BY ").Append(groupBy);
        if (orderBy is not null) sb.Append(" ORDER BY ").Append(orderBy);
        if (limit is not null) sb.Append(" LIMIT ").Append(limit.Value);
        return sb.ToString();
    }

    static string Scalar(Filter f) => f.Value is { } v ? ToText(v) : throw new QueryException($"Filter on '{f.Property}' requires a value");

    static IEnumerable<string> List(Filter f) => f.Value is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() is > 0 and <= 1000
        ? arr.EnumerateArray().Select(ToText)
        : throw new QueryException($"'in' filter on '{f.Property}' requires an array of 1-1000 values");

    static string ToText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        _ => throw new QueryException("Filter value must be a string, number or boolean"),
    };

    static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}

public static class Queries
{
    const string None = "$none";

    public static string Trend(SqlBuilder b, Series s, bool dedup, string interval, string tz, Breakdown? bd)
    {
        b.Where($"e.event = {b.P(s.Event)}");
        var bucket = $"date_trunc('{interval}', convert_tz(e.timestamp, 'UTC', {b.P(tz)}))";
        var value = s.Metric switch
        {
            "unique_users" => b.Person(),
            _ => dedup ? "e.event_id" : null,
        };
        var valueCol = value is null ? "" : $", {value} AS v";

        if (bd is null)
            return $"SELECT bucket, NULL AS bd, {(value is null ? "COUNT(*)" : "COUNT(DISTINCT v)")} AS c, GROUPING(bucket) AS is_total " +
                   $"FROM ({b.Select($"{bucket} AS bucket{valueCol}")}) s GROUP BY GROUPING SETS ((bucket), ())";

        var bdExpr = $"COALESCE(CAST({ColumnMapper.Expression(b, bd.Scope, bd.Property)} AS VARCHAR), {b.P(None)})";
        return $"WITH base AS ({b.Select($"{bucket} AS bucket, {bdExpr} AS bd{valueCol}")}), " +
               $"top AS (SELECT bd FROM base GROUP BY bd ORDER BY COUNT(*) DESC LIMIT {Limit(bd)}) " +
               $"SELECT base.bucket, base.bd, {(value is null ? "COUNT(*)" : "COUNT(DISTINCT base.v)")} AS c, GROUPING(base.bucket) AS is_total " +
               $"FROM base JOIN top ON base.bd = top.bd GROUP BY GROUPING SETS ((base.bucket, base.bd), (base.bd))";
    }

    public static string Funnel(SqlBuilder b, List<FunnelStep> steps, long windowSeconds, Breakdown? bd)
    {
        var conds = steps.Select(st =>
        {
            var parts = new List<string> { $"e.event = {b.P(st.Event)}" };
            parts.AddRange(st.Filters?.Select(b.Condition) ?? []);
            return "(" + string.Join(" AND ", parts) + ")";
        }).ToList();

        b.Where($"e.event IN ({string.Join(", ", steps.Select(st => st.Event).Distinct().Select(e => b.P(e)))})");
        var person = b.Person();

        var bdSelect = bd is null ? "" :
            $", min_by(COALESCE(CAST({ColumnMapper.Expression(b, bd.Scope, bd.Property)} AS VARCHAR), {b.P(None)}), IF({conds[0]}, e.timestamp, NULL)) AS bd";

        var inner = b.Select($"{person} AS person{bdSelect}, window_funnel({windowSeconds}, e.timestamp, 0, [{string.Join(", ", conds)}]) AS lvl",
            groupBy: person);
        var counts = string.Join(", ", steps.Select((_, i) => $"SUM(IF(lvl >= {i + 1}, 1, 0)) AS s{i + 1}"));

        return bd is null
            ? $"SELECT NULL AS bd, {counts} FROM ({inner}) f WHERE lvl > 0"
            : $"SELECT bd, {counts} FROM ({inner}) f WHERE lvl > 0 GROUP BY bd ORDER BY s1 DESC LIMIT {Limit(bd)}";
    }

    public static string Events(SqlBuilder b, string? eventName, string? distinctId,
        IReadOnlyList<string>? personDistinctIds, string? personIdScan, int limit)
    {
        var person = b.Person();
        if (eventName is not null) b.Where($"e.event = {b.P(eventName)}");
        if (distinctId is not null) b.Where($"e.distinct_id = {b.P(distinctId)}");
        if (personDistinctIds is not null) b.Where($"e.distinct_id IN ({string.Join(", ", personDistinctIds.Select(d => b.P(d)))})");
        if (personIdScan is not null) b.Where($"{person} = {b.P(personIdScan)}");
        return b.Select(
            $"e.event_id, e.event, e.timestamp, e.distinct_id, {person} AS person, CAST(e.properties AS VARCHAR), " +
            string.Join(", ", ColumnMapper.EventColumnList.Select(c => $"CAST(e.{c} AS VARCHAR)")),
            orderBy: "e.timestamp DESC", limit: limit);
    }

    public static string EventNames(SqlBuilder b) =>
        b.Select("e.event, COUNT(*) AS c", groupBy: "e.event", orderBy: "c DESC", limit: 1000);

    public static string EventPropertyKeys(SqlBuilder b) =>
        $"SELECT k, COUNT(*) AS c FROM ({b.Select("e.properties", orderBy: "e.timestamp DESC", limit: 10_000)}) s, " +
        "unnest(CAST(json_keys(s.properties) AS ARRAY<VARCHAR>)) AS t(k) GROUP BY k ORDER BY c DESC LIMIT 500";

    static int Limit(Breakdown bd) => bd.Limit is null ? 10 : Math.Clamp(bd.Limit.Value, 1, 100);
}
