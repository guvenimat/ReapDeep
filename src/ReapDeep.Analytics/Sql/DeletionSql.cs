namespace ReapDeep.Analytics.Sql;

public static class DeletionSql
{
    public static readonly string[] TombstoneTables = ["analytics.person_distinct_ids", "analytics.persons"];

    public static SqlQuery DeleteEvents(long projectId, IReadOnlyList<string> distinctIds)
    {
        var b = new SqlBuilder(projectId, DateTime.UtcNow, DateTime.UtcNow);
        return b.Build($"DELETE FROM analytics.events WHERE project_id = {b.P(projectId)} AND distinct_id IN ({string.Join(", ", distinctIds.Select(d => b.P(d)))})");
    }

    public static SqlQuery PurgeTombstones(string table, DateTime olderThanUtc)
    {
        if (!TombstoneTables.Contains(table)) throw new ArgumentException($"Unknown tombstone table: {table}", nameof(table));
        return new SqlQuery($"DELETE FROM {table} WHERE is_deleted = 1 AND updated_at < @cutoff", [("@cutoff", olderThanUtc)]);
    }
}
