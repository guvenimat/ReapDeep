using Dapper;
using Npgsql;

namespace ReapDeep.Ingestion.Definitions;

public sealed class DefinitionWriter(DefinitionTracker tracker, NpgsqlDataSource db, ILogger<DefinitionWriter> log) : BackgroundService
{
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    const int MaxBatch = 50_000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, ct);
            }
            catch (OperationCanceledException)
            {
            }
            await FlushAsync();
        }
    }

    async Task FlushAsync()
    {
        var batch = tracker.Drain(MaxBatch);
        if (batch.Count == 0) return;
        try
        {
            await WriteAsync(batch);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to write {Count} event/property definitions; they are retried when seen again", batch.Count);
            tracker.Forget(batch.Select(d => d.Key));
        }
    }

    async Task WriteAsync(List<Definition> batch)
    {
        var now = DateTime.UtcNow;
        var events = batch.Where(d => d.Key.Scope == DefinitionScope.Event).ToList();
        var properties = batch.Where(d => d.Key.Scope != DefinitionScope.Event).ToList();

        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        if (events.Count > 0)
            await conn.ExecuteAsync("""
                INSERT INTO event_definitions (project_id, name, first_seen_at, last_seen_at)
                SELECT p, n, @Now, @Now FROM unnest(@ProjectIds, @Names) AS u(p, n)
                WHERE EXISTS (SELECT 1 FROM projects WHERE id = u.p)
                ON CONFLICT (project_id, name) DO UPDATE SET last_seen_at = GREATEST(event_definitions.last_seen_at, EXCLUDED.last_seen_at)
                """, new
            {
                Now = now,
                ProjectIds = events.Select(e => e.Key.ProjectId).ToArray(),
                Names = events.Select(e => e.Key.Name).ToArray(),
            }, tx);

        if (properties.Count > 0)
            await conn.ExecuteAsync("""
                INSERT INTO property_definitions (project_id, scope, name, property_type, column_name, last_seen_at)
                SELECT p, s, n, ty, c, @Now FROM unnest(@ProjectIds, @Scopes, @Names, @Types, @Columns) AS u(p, s, n, ty, c)
                WHERE EXISTS (SELECT 1 FROM projects WHERE id = u.p)
                ON CONFLICT (project_id, scope, name) DO UPDATE SET
                    last_seen_at = GREATEST(property_definitions.last_seen_at, EXCLUDED.last_seen_at),
                    property_type = COALESCE(property_definitions.property_type, EXCLUDED.property_type),
                    column_name = COALESCE(property_definitions.column_name, EXCLUDED.column_name)
                """, new
            {
                Now = now,
                ProjectIds = properties.Select(p => p.Key.ProjectId).ToArray(),
                Scopes = properties.Select(p => p.Key.Scope == DefinitionScope.PersonProperty ? "person" : "event").ToArray(),
                Names = properties.Select(p => p.Key.Name).ToArray(),
                Types = properties.Select(p => p.Type).ToArray(),
                Columns = properties.Select(p => p.Column).ToArray(),
            }, tx);

        await tx.CommitAsync();
    }
}
