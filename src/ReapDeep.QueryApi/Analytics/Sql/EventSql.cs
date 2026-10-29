namespace ReapDeep.QueryApi.Analytics.Sql;

public static class EventSql
{
    public static SqlQuery Build(SqlBuilder b, string? eventName, string? distinctId,
        IReadOnlyList<string>? personDistinctIds, string? personIdScan, int limit)
    {
        var person = b.Person();
        if (eventName is not null) b.Where($"e.event = {b.P(eventName)}");
        if (distinctId is not null) b.Where($"e.distinct_id = {b.P(distinctId)}");
        if (personDistinctIds is not null) b.Where($"e.distinct_id IN ({string.Join(", ", personDistinctIds.Select(d => b.P(d)))})");
        if (personIdScan is not null) b.Where($"{person} = {b.P(personIdScan)}");
        return b.Build(b.Select(
            $"e.event_id, e.event, e.timestamp, e.distinct_id, {person} AS person, CAST(e.properties AS VARCHAR), " +
            string.Join(", ", ColumnMapper.EventColumnList.Select(c => $"CAST(e.{c} AS VARCHAR)")),
            orderBy: "e.timestamp DESC", limit: limit));
    }
}
