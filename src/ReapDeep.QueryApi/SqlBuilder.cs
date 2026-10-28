using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace ReapDeep.QueryApi;

public sealed class SqlBuilder
{
    public const string PersonExpr = "COALESCE(pdi.person_id, e.distinct_id)";

    readonly List<(string Name, object Value)> _params = [];
    readonly List<string> _where = [];
    readonly List<string> _personWhere = [];
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
        foreach (var f in filters) (f.Scope == "person" ? _personWhere : _where).Add(Condition(f));
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
        if (_joinPdi) sb.Append(PersonJoins("e"));
        sb.Append(" WHERE ").AppendJoin(" AND ", _where.Concat(_personWhere));
        if (groupBy is not null) sb.Append(" GROUP BY ").Append(groupBy);
        if (orderBy is not null) sb.Append(" ORDER BY ").Append(orderBy);
        if (limit is not null) sb.Append(" LIMIT ").Append(limit.Value);
        return sb.ToString();
    }

    public string EventsOnly(string select, string groupBy) =>
        $"SELECT {select} FROM analytics.events e WHERE {string.Join(" AND ", _where)} GROUP BY {groupBy}";

    public string PersonJoins(string alias)
    {
        var sb = new StringBuilder(" LEFT JOIN analytics.person_distinct_ids pdi ON ")
            .Append(alias).Append(".project_id = pdi.project_id AND ").Append(alias).Append(".distinct_id = pdi.distinct_id AND pdi.is_deleted = 0");
        if (_joinPersons)
            sb.Append(" LEFT JOIN analytics.persons p ON ").Append(alias).Append(".project_id = p.project_id AND pdi.person_id = p.person_id AND p.is_deleted = 0");
        return sb.ToString();
    }

    public string PersonWhere() => _personWhere.Count == 0 ? "" : " WHERE " + string.Join(" AND ", _personWhere);

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

    public sealed record Query(string Sql, SqlBuilder Builder);

    public static (Query Buckets, Query? Total) Trend(Func<SqlBuilder> builder, Series s, bool dedup, string interval, TimeRange range, string tz, Breakdown? bd)
    {
        if (s.Metric == "unique_users")
            return (UniqueUsers(builder(), s, interval, range, tz, bd, withBucket: true), UniqueUsers(builder(), s, interval, range, tz, bd, withBucket: false));
        if (dedup)
            return (EventCount(builder(), s, interval, range, tz, bd, distinct: true, withBucket: true), EventCount(builder(), s, interval, range, tz, bd, distinct: true, withBucket: false));
        return (EventCount(builder(), s, interval, range, tz, bd, distinct: false, withBucket: true), null);
    }

    static string Bucket(SqlBuilder b, string interval, TimeRange range, string tz) => range.FixedOffsetSeconds is { } offset
        ? $"date_trunc('{interval}', seconds_add(e.timestamp, {offset}))"
        : $"date_trunc('{interval}', convert_tz(e.timestamp, 'UTC', {b.P(tz)}))";

    static Query EventCount(SqlBuilder b, Series s, string interval, TimeRange range, string tz, Breakdown? bd, bool distinct, bool withBucket)
    {
        b.Where($"e.event = {b.P(s.Event)}");
        var bucket = withBucket ? $"{Bucket(b, interval, range, tz)} AS bucket" : "NULL AS bucket";
        var value = distinct ? ", xx_hash3_64(e.event_id) AS v" : "";
        var agg = distinct ? "COUNT(DISTINCT v)" : "COUNT(*)";

        if (bd is null)
            return new($"SELECT bucket, NULL AS bd, {agg} AS c FROM ({b.Select($"{bucket}{value}")}) s GROUP BY bucket", b);

        var bdExpr = $"COALESCE(CAST({ColumnMapper.Expression(b, bd.Scope, bd.Property)} AS VARCHAR), {b.P(None)})";
        return new($"WITH base AS ({b.Select($"{bucket}, {bdExpr} AS bd{value}")}), " +
                   $"top AS (SELECT bd FROM base GROUP BY bd ORDER BY COUNT(*) DESC, bd LIMIT {Limit(bd)}) " +
                   $"SELECT base.bucket, base.bd, {(distinct ? "COUNT(DISTINCT base.v)" : "COUNT(*)")} AS c FROM base JOIN top ON base.bd = top.bd GROUP BY base.bucket, base.bd", b);
    }

    static Query UniqueUsers(SqlBuilder b, Series s, string interval, TimeRange range, string tz, Breakdown? bd, bool withBucket)
    {
        b.Where($"e.event = {b.P(s.Event)}");
        var inner = new List<string> { "e.project_id", "e.distinct_id" };
        if (withBucket) inner.Add($"{Bucket(b, interval, range, tz)} AS bucket");

        string? bdOuter = null;
        if (bd is not null)
        {
            var expr = $"COALESCE(CAST({ColumnMapper.Expression(b, bd.Scope, bd.Property)} AS VARCHAR), {b.P(None)})";
            if (bd.Scope == "person") bdOuter = expr;
            else
            {
                inner.Add($"{expr} AS bd");
                bdOuter = "d.bd";
            }
        }

        var from = $"({b.EventsOnly(string.Join(", ", inner), string.Join(", ", Enumerable.Range(1, inner.Count)))}) d{b.PersonJoins("d")}{b.PersonWhere()}";
        const string person = "COALESCE(pdi.person_id, d.distinct_id)";
        var bucketOut = withBucket ? "d.bucket" : "NULL";

        if (bdOuter is null)
            return new($"SELECT {bucketOut} AS bucket, NULL AS bd, COUNT(DISTINCT {person}) AS c FROM {from}{(withBucket ? " GROUP BY d.bucket" : "")}", b);

        return new($"WITH base AS (SELECT {bucketOut} AS bucket, {bdOuter} AS bd, {person} AS v FROM {from}), " +
                   $"top AS (SELECT bd FROM base GROUP BY bd ORDER BY COUNT(DISTINCT v) DESC, bd LIMIT {Limit(bd!)}) " +
                   "SELECT base.bucket, base.bd, COUNT(DISTINCT base.v) AS c FROM base JOIN top ON base.bd = top.bd GROUP BY base.bucket, base.bd", b);
    }

    public static string Funnel(SqlBuilder b, List<FunnelStep> steps, long windowSeconds, Breakdown? bd, double sampling)
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

        var bdSelect = bd is null ? "" :
            $", min_by(COALESCE(CAST({ColumnMapper.Expression(b, bd.Scope, bd.Property)} AS VARCHAR), {b.P(None)}), IF({conds[0]}, e.timestamp, NULL)) AS bd";

        var inner = b.Select($"{person} AS person{bdSelect}, window_funnel({windowSeconds}, e.timestamp, 0, [{string.Join(", ", conds)}]) AS lvl",
            groupBy: "1");
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
