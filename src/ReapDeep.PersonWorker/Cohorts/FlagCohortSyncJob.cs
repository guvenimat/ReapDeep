using Dapper;
using Npgsql;
using ReapDeep.Analytics.Sql;
using ReapDeep.PersonWorker.Infrastructure;

namespace ReapDeep.PersonWorker.Cohorts;

public sealed class FlagCohortSyncJob(NpgsqlDataSource db, StarRocksStore? store, IConfiguration cfg, ILogger<FlagCohortSyncJob> log) : BackgroundService
{
    public const int MaxMembers = 100_000;
    const int InsertBatch = 5_000;
    readonly TimeSpan _interval = cfg.Seconds("Cohorts:FlagSyncSeconds", 15);

    internal sealed record PendingCohortRecord(long Id, long ProjectId, long ActiveVersion);

    const string Referenced = """
        EXISTS (SELECT 1 FROM feature_flags f WHERE f.project_id = c.project_id AND f.enabled
                AND jsonb_path_exists(f.conditions, '$[*] ? ((@.op == "in_cohort" || @.op == "not_in_cohort") && @.value == $id)', jsonb_build_object('id', c.id)))
        """;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (store is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReleaseUnusedAsync(ct);
                foreach (var cohort in await PendingAsync(ct)) await SyncAsync(store, cohort, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Flag cohort sync failed; retrying in the next run");
            }
            await Task.Delay(_interval, ct);
        }
    }

    async Task<List<PendingCohortRecord>> PendingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<PendingCohortRecord>($"""
            SELECT c.id, c.project_id, c.active_version FROM cohorts c
            WHERE c.active_version IS NOT NULL AND c.flag_synced_version IS DISTINCT FROM c.active_version AND {Referenced}
            ORDER BY c.id LIMIT 20
            """)).AsList();
    }

    async Task ReleaseUnusedAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var released = await conn.ExecuteAsync($"""
            WITH unused AS (
                UPDATE cohorts c SET flag_synced_version = NULL, flag_synced_at = now(), flag_sync_error = NULL
                WHERE c.flag_synced_version IS NOT NULL AND NOT {Referenced} RETURNING c.id)
            DELETE FROM flag_cohort_members m USING unused u WHERE m.cohort_id = u.id
            """);
        if (released > 0) log.LogInformation("Released {Rows} flag cohort members no longer used by an enabled flag", released);
    }

    async Task SyncAsync(StarRocksStore starRocks, PendingCohortRecord cohort, CancellationToken ct)
    {
        var ids = await starRocks.QueryAsync(CohortSql.DistinctIds(cohort.ProjectId, cohort.Id, cohort.ActiveVersion, MaxMembers + 1), r => r.GetString(0), ct);
        var tooLarge = ids.Count > MaxMembers;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM flag_cohort_members WHERE cohort_id = @Id", new { cohort.Id }, tx);
        if (!tooLarge)
            foreach (var chunk in ids.Chunk(InsertBatch))
                await conn.ExecuteAsync("INSERT INTO flag_cohort_members (cohort_id, distinct_id) SELECT @Id, unnest(@Ids) ON CONFLICT DO NOTHING",
                    new { cohort.Id, Ids = chunk }, tx);
        await conn.ExecuteAsync("""
            UPDATE cohorts SET flag_synced_version = @Version, flag_synced_at = now(), flag_sync_error = @Error WHERE id = @Id
            """, new { cohort.Id, Version = cohort.ActiveVersion, Error = tooLarge ? $"The cohort has more than {MaxMembers} distinct ids; flags treat everyone as outside it" : null }, tx);
        await tx.CommitAsync(ct);
        if (tooLarge) log.LogWarning("Cohort {Cohort} (project {Project}) is too large for flag targeting", cohort.Id, cohort.ProjectId);
        else log.LogInformation("Cohort {Cohort} (project {Project}): {Ids} distinct ids synced for flag targeting", cohort.Id, cohort.ProjectId, ids.Count);
    }
}
