using MySqlConnector;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.PersonWorker.Deletion;

public sealed class StarRocksDeleter(MySqlDataSource db)
{
    const int ChunkSize = 500;
    const int CommandTimeoutSeconds = 600;

    public async Task DeleteEventsAsync(long projectId, IEnumerable<string> distinctIds, CancellationToken ct)
    {
        foreach (var chunk in distinctIds.Order(StringComparer.Ordinal).Chunk(ChunkSize))
            await ExecuteAsync(DeletionSql.DeleteEvents(projectId, chunk), ct);
    }

    public async Task DeleteCohortMembershipsAsync(long projectId, IEnumerable<string> distinctIds, CancellationToken ct)
    {
        foreach (var chunk in distinctIds.Order(StringComparer.Ordinal).Chunk(ChunkSize))
            await ExecuteAsync(CohortSql.DeleteDistinctIds(projectId, chunk), ct);
    }

    public async Task DeleteProjectAsync(long projectId, CancellationToken ct)
    {
        foreach (var table in DeletionSql.ProjectTables)
            await ExecuteAsync(DeletionSql.DeleteProject(table, projectId), ct);
    }

    public async Task PurgeTombstonesAsync(DateTime olderThanUtc, CancellationToken ct)
    {
        foreach (var table in DeletionSql.TombstoneTables)
            await ExecuteAsync(DeletionSql.PurgeTombstones(table, olderThanUtc), ct);
    }

    async Task ExecuteAsync(SqlQuery query, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand(query.Sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var (name, value) in query.Parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
