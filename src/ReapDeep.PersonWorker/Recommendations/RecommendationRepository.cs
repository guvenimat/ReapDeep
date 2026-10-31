using Dapper;
using Npgsql;

namespace ReapDeep.PersonWorker.Recommendations;

public sealed class RecommendationRepository(NpgsqlDataSource db)
{
    const int LockClass = 0x524543;

    public async Task<List<long>> DueProjectsAsync(TimeSpan interval, int modelCount, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<long>("""
            SELECT p.id FROM projects p
            WHERE EXISTS (SELECT 1 FROM items i WHERE i.project_id = p.id)
              AND (
                (SELECT count(*) FROM recommendation_runs r WHERE r.project_id = p.id) < @Models
                OR (SELECT min(r.calculated_at) FROM recommendation_runs r WHERE r.project_id = p.id) < now() - make_interval(secs => @Seconds)
              )
            ORDER BY p.id
            """, new { Models = modelCount, Seconds = (int)interval.TotalSeconds })).AsList();
    }

    public async Task<NpgsqlConnection?> TryLockAsync(long projectId, CancellationToken ct)
    {
        var conn = await db.OpenConnectionAsync(ct);
        if (await conn.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@Class, @Project)", new { Class = LockClass, Project = (int)projectId }))
            return conn;
        await conn.DisposeAsync();
        return null;
    }

    public static Task UnlockAsync(NpgsqlConnection conn, long projectId) =>
        conn.ExecuteAsync("SELECT pg_advisory_unlock(@Class, @Project)", new { Class = LockClass, Project = (int)projectId });

    public async Task ReplaceAsync(long projectId, string model, string[] sources, string[] recommendations, int calculationMs, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM item_recommendations WHERE project_id = @Project AND model = @Model",
            new { Project = projectId, Model = model }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO item_recommendations (project_id, model, source_item_id, recommended_items, calculated_at)
            SELECT @Project, @Model, s, r::jsonb, now() FROM unnest(@Sources, @Recommendations) AS t(s, r)
            """, new { Project = projectId, Model = model, Sources = sources, Recommendations = recommendations }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO recommendation_runs (project_id, model, calculated_at, calculation_ms, source_items)
            VALUES (@Project, @Model, now(), @Ms, @Count)
            ON CONFLICT (project_id, model) DO UPDATE SET calculated_at = now(), calculation_ms = EXCLUDED.calculation_ms, source_items = EXCLUDED.source_items
            """, new { Project = projectId, Model = model, Ms = calculationMs, Count = sources.Length }, tx);
        await tx.CommitAsync(ct);
    }

    public async Task ReplaceHistoryAsync(long projectId, string[] people, string[] histories, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM person_item_history WHERE project_id = @Project", new { Project = projectId }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO person_item_history (project_id, person_key, items, calculated_at)
            SELECT @Project, k, h::jsonb, now() FROM unnest(@People, @Histories) AS t(k, h)
            """, new { Project = projectId, People = people, Histories = histories }, tx);
        await tx.CommitAsync(ct);
    }
}
