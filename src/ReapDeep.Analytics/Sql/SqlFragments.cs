namespace ReapDeep.Analytics.Sql;

static class SqlFragments
{
    public const string NoValue = "$none";

    public static string LocalTime(SqlBuilder b, TimeRange range, string tz) => range.FixedOffsetSeconds is { } offset
        ? $"seconds_add(e.timestamp, {offset})"
        : $"convert_tz(e.timestamp, 'UTC', {b.P(tz)})";

    public static string Bucket(SqlBuilder b, string interval, TimeRange range, string tz) =>
        $"date_trunc('{interval}', {LocalTime(b, range, tz)})";

    public static string BreakdownValue(SqlBuilder b, Breakdown bd) =>
        $"COALESCE(CAST({ColumnMapper.Expression(b, bd.Scope, bd.Property)} AS VARCHAR), {b.P(NoValue)})";

    public static int Limit(Breakdown bd) => bd.Limit is null ? 10 : Math.Clamp(bd.Limit.Value, 1, 100);
}
