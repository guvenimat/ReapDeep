namespace ReapDeep.Analytics.Sql;

public static class StickinessSql
{
    public static readonly string[] Intervals = ["day", "week"];

    public static SqlQuery Build(SqlBuilder b, string eventName, IReadOnlyList<string>? events, string interval, TimeRange range, string tz)
    {
        b.Where(SqlFragments.EventIs(b, eventName, events));
        var active = b.EventsOnly($"e.project_id, e.distinct_id, {LifecycleSql.PeriodIndex(interval, SqlFragments.LocalTime(b, range, tz))} AS p", "1, 2, 3");
        return b.Build(
            $"WITH a AS (SELECT DISTINCT COALESCE(pdi.person_id, d.distinct_id) AS person, d.p FROM ({active}) d{b.PersonJoins("d")}{b.PersonWhere()}), " +
            "c AS (SELECT person, COUNT(*) AS periods FROM a GROUP BY person) " +
            "SELECT periods, COUNT(*) AS persons FROM c GROUP BY periods ORDER BY periods");
    }
}
