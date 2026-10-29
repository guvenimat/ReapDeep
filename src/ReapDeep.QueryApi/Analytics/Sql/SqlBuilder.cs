using System.Text;
using System.Text.Json;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Analytics.Sql;

public sealed record SqlQuery(string Sql, IReadOnlyList<(string Name, object Value)> Parameters);

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

    public SqlQuery Build(string sql) => new(sql, _params.ToArray());

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
            _ => throw new ValidationException($"Invalid op: '{f.Op}' (eq, neq, contains, not_contains, in, is_set, is_not_set)"),
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

    static string Scalar(Filter f) => f.Value is { } v ? ToText(v) : throw new ValidationException($"Filter on '{f.Property}' requires a value");

    static IEnumerable<string> List(Filter f) => f.Value is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() is > 0 and <= 1000
        ? arr.EnumerateArray().Select(ToText)
        : throw new ValidationException($"'in' filter on '{f.Property}' requires an array of 1-1000 values");

    static string ToText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        _ => throw new ValidationException("Filter value must be a string, number or boolean"),
    };

    static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
