namespace ReapDeep.Analytics.Sql;

public static class LifecycleSql
{
    public const string New = "new";
    public const string Returning = "returning";
    public const string Resurrecting = "resurrecting";
    public const string Dormant = "dormant";
    public static readonly string[] Periods = ["day", "week", "month"];

    public static SqlQuery Build(SqlBuilder b, string? eventName, string period, TimeRange scanned, string tz, long firstIndex, long lastIndex)
    {
        if (eventName is not null) b.Where($"e.event = {b.P(eventName)}");
        var active = b.EventsOnly($"e.project_id, e.distinct_id, {PeriodIndex(period, SqlFragments.LocalTime(b, scanned, tz))} AS p", "1, 2, 3");
        var (first, last) = (b.P(firstIndex), b.P(lastIndex));
        const string byPerson = "PARTITION BY a.person ORDER BY a.p";
        return b.Build(
            $"WITH a AS (SELECT DISTINCT COALESCE(pdi.person_id, d.distinct_id) AS person, d.p FROM ({active}) d{b.PersonJoins("d")}{b.PersonWhere()}), " +
            "x AS (SELECT a.p, MIN(a.p) OVER (PARTITION BY a.person) AS first_p, " +
            $"LAG(a.p) OVER ({byPerson}) AS prev_p, LEAD(a.p) OVER ({byPerson}) AS next_p FROM a) " +
            $"SELECT x.p, CASE WHEN x.p = x.first_p THEN '{New}' WHEN x.prev_p = x.p - 1 THEN '{Returning}' ELSE '{Resurrecting}' END AS status, " +
            $"COUNT(*) AS n FROM x WHERE x.p >= {first} AND x.p <= {last} GROUP BY 1, 2 " +
            $"UNION ALL SELECT x.p + 1, '{Dormant}', COUNT(*) FROM x " +
            $"WHERE (x.next_p IS NULL OR x.next_p > x.p + 1) AND x.p + 1 >= {first} AND x.p + 1 <= {last} GROUP BY 1");
    }

    public static string PeriodIndex(string period, string local) => period switch
    {
        "month" => $"CAST(year({local}) * 12 + month({local}) - 1 AS BIGINT)",
        _ => RetentionSql.PeriodIndex(period, local),
    };

    public static long Index(string period, DateTime local)
    {
        if (period == "month") return local.Year * 12L + local.Month - 1;
        var days = (long)Math.Floor((local.Date - RetentionSql.IndexEpoch).TotalDays);
        return period == "week" ? (long)Math.Floor(days / 7.0) : days;
    }

    public static DateTime Start(string period, long index) => period switch
    {
        "month" => new DateTime((int)(index / 12), (int)(index % 12) + 1, 1),
        "week" => RetentionSql.IndexEpoch.AddDays(index * 7),
        _ => RetentionSql.IndexEpoch.AddDays(index),
    };
}
