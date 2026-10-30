namespace ReapDeep.Analytics.Sql;

public static class TrendSql
{
    public sealed record Plan(SqlQuery Buckets, SqlQuery? Total);

    public static readonly string[] PropertyMetrics = ["sum", "avg", "min", "max", "median", "p90"];
    public static readonly string[] Metrics = ["total", "unique_users", .. PropertyMetrics];

    public static Plan Build(Func<SqlBuilder> builder, Series s, bool dedup, string interval, TimeRange range, string tz, Breakdown? bd)
    {
        if (PropertyMetrics.Contains(s.Metric))
            return new(PropertyAggregate(builder(), s, interval, range, tz, bd, withBucket: true),
                s.Metric == "sum" ? null : PropertyAggregate(builder(), s, interval, range, tz, bd, withBucket: false));
        if (s.Metric == "unique_users")
            return new(UniqueUsers(builder(), s, interval, range, tz, bd, withBucket: true), UniqueUsers(builder(), s, interval, range, tz, bd, withBucket: false));
        if (dedup)
            return new(EventCount(builder(), s, interval, range, tz, bd, distinct: true, withBucket: true), EventCount(builder(), s, interval, range, tz, bd, distinct: true, withBucket: false));
        return new(EventCount(builder(), s, interval, range, tz, bd, distinct: false, withBucket: true), null);
    }

    static SqlQuery EventCount(SqlBuilder b, Series s, string interval, TimeRange range, string tz, Breakdown? bd, bool distinct, bool withBucket)
    {
        b.Where($"e.event = {b.P(s.Event)}");
        var bucket = withBucket ? $"{SqlFragments.Bucket(b, interval, range, tz)} AS bucket" : "NULL AS bucket";
        var value = distinct ? ", xx_hash3_64(e.event_id) AS v" : "";
        var agg = distinct ? "COUNT(DISTINCT v)" : "COUNT(*)";

        if (bd is null)
            return b.Build($"SELECT bucket, NULL AS bd, {agg} AS c FROM ({b.Select($"{bucket}{value}")}) s GROUP BY bucket");

        var bdExpr = SqlFragments.BreakdownValue(b, bd);
        return b.Build($"WITH base AS ({b.Select($"{bucket}, {bdExpr} AS bd{value}")}), " +
                       $"top AS (SELECT bd FROM base GROUP BY bd ORDER BY COUNT(*) DESC, bd LIMIT {SqlFragments.Limit(bd)}) " +
                       $"SELECT base.bucket, base.bd, {(distinct ? "COUNT(DISTINCT base.v)" : "COUNT(*)")} AS c FROM base JOIN top ON base.bd = top.bd GROUP BY base.bucket, base.bd");
    }

    static SqlQuery PropertyAggregate(SqlBuilder b, Series s, string interval, TimeRange range, string tz, Breakdown? bd, bool withBucket)
    {
        b.Where($"e.event = {b.P(s.Event)}");
        var value = $"CAST({ColumnMapper.Expression(b, "event", s.Property ?? "")} AS DOUBLE)";
        var bucket = withBucket ? $"{SqlFragments.Bucket(b, interval, range, tz)} AS bucket" : "NULL AS bucket";
        var agg = Aggregate(s.Metric!, "base.v");

        if (bd is null)
            return b.Build($"SELECT base.bucket, NULL AS bd, {agg} AS c FROM ({b.Select($"{bucket}, {value} AS v")}) base WHERE base.v IS NOT NULL GROUP BY base.bucket");

        var bdExpr = SqlFragments.BreakdownValue(b, bd);
        return b.Build($"WITH base AS (SELECT * FROM ({b.Select($"{bucket}, {bdExpr} AS bd, {value} AS v")}) x WHERE x.v IS NOT NULL), " +
                       $"top AS (SELECT bd FROM base GROUP BY bd ORDER BY COUNT(*) DESC, bd LIMIT {SqlFragments.Limit(bd)}) " +
                       $"SELECT base.bucket, base.bd, {agg} AS c FROM base JOIN top ON base.bd = top.bd GROUP BY base.bucket, base.bd");
    }

    static string Aggregate(string metric, string column) => metric switch
    {
        "sum" => $"SUM({column})",
        "avg" => $"AVG({column})",
        "min" => $"MIN({column})",
        "max" => $"MAX({column})",
        "median" => $"percentile_approx({column}, 0.5)",
        _ => $"percentile_approx({column}, 0.9)",
    };

    static SqlQuery UniqueUsers(SqlBuilder b, Series s, string interval, TimeRange range, string tz, Breakdown? bd, bool withBucket)
    {
        b.Where($"e.event = {b.P(s.Event)}");
        var inner = new List<string> { "e.project_id", "e.distinct_id" };
        if (withBucket) inner.Add($"{SqlFragments.Bucket(b, interval, range, tz)} AS bucket");

        string? bdOuter = null;
        if (bd is not null)
        {
            var expr = SqlFragments.BreakdownValue(b, bd);
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
            return b.Build($"SELECT {bucketOut} AS bucket, NULL AS bd, COUNT(DISTINCT {person}) AS c FROM {from}{(withBucket ? " GROUP BY d.bucket" : "")}");

        return b.Build($"WITH base AS (SELECT {bucketOut} AS bucket, {bdOuter} AS bd, {person} AS v FROM {from}), " +
                       $"top AS (SELECT bd FROM base GROUP BY bd ORDER BY COUNT(DISTINCT v) DESC, bd LIMIT {SqlFragments.Limit(bd!)}) " +
                       "SELECT base.bucket, base.bd, COUNT(DISTINCT base.v) AS c FROM base JOIN top ON base.bd = top.bd GROUP BY base.bucket, base.bd");
    }
}
