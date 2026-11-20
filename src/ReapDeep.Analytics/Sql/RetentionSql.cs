namespace ReapDeep.Analytics.Sql;

public static class RetentionSql
{
    public static readonly DateTime IndexEpoch = new(1970, 1, 5);

    public static string PeriodIndex(string period, string local) => period == "week"
        ? $"CAST(floor(datediff({local}, '1970-01-05') / 7) AS BIGINT)"
        : $"CAST(datediff({local}, '1970-01-05') AS BIGINT)";

    public static SqlQuery Build(SqlBuilder b, string startEvent, IReadOnlyList<string>? startEvents,
        string returnEvent, IReadOnlyList<string>? returnEvents, string period, int periods,
        TimeRange range, string tz, DateTime cohortEndUtc)
    {
        var starts = startEvents is { Count: > 0 } ? startEvents : [startEvent];
        var returns = returnEvents is { Count: > 0 } ? returnEvents : [returnEvent];
        var names = starts.Concat(returns).Distinct().ToList();
        b.Where($"e.event IN ({string.Join(", ", names.Select(b.P))})");
        var start = SqlFragments.EventIs(b, startEvent, startEvents);
        var ret = SqlFragments.EventIs(b, returnEvent, returnEvents);
        var ev = b.Select($"{b.Person()} AS person, {PeriodIndex(period, SqlFragments.LocalTime(b, range, tz))} AS p, " +
                          $"{start} AND e.timestamp < {b.P(cohortEndUtc)} AS is_start, {ret} AS is_ret");
        return b.Build(
            $"WITH ev AS ({ev}), " +
            "cohort AS (SELECT person, MIN(p) AS c FROM ev WHERE is_start GROUP BY person), " +
            "act AS (SELECT DISTINCT person, p FROM ev WHERE is_ret) " +
            "SELECT c.c AS cohort, a.p - c.c AS k, COUNT(*) AS n FROM cohort c JOIN act a ON a.person = c.person " +
            $"AND a.p >= c.c AND a.p <= c.c + {periods} GROUP BY 1, 2 " +
            "UNION ALL SELECT c AS cohort, -1 AS k, COUNT(*) AS n FROM cohort GROUP BY c");
    }
}
