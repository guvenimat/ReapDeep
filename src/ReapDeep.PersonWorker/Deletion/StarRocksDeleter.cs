using MySqlConnector;

namespace ReapDeep.PersonWorker.Deletion;

public sealed class StarRocksDeleter(MySqlDataSource db)
{
    const int ChunkSize = 500;
    const int CommandTimeoutSeconds = 600;
    static readonly string[] TombstoneTables = ["person_distinct_ids", "persons"];

    public async Task DeleteEventsAsync(long projectId, IEnumerable<string> distinctIds, CancellationToken ct)
    {
        foreach (var chunk in distinctIds.Order(StringComparer.Ordinal).Chunk(ChunkSize))
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            var names = chunk.Select((_, i) => "@d" + i).ToArray();
            await using var cmd = new MySqlCommand(
                $"DELETE FROM analytics.events WHERE project_id = @p AND distinct_id IN ({string.Join(", ", names)})", conn)
            { CommandTimeout = CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("p", projectId);
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task PurgeTombstonesAsync(DateTime olderThanUtc, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        foreach (var table in TombstoneTables)
        {
            await using var cmd = new MySqlCommand($"DELETE FROM analytics.{table} WHERE is_deleted = 1 AND updated_at < @cutoff", conn)
            { CommandTimeout = CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("cutoff", olderThanUtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
