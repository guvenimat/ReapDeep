using Dapper;
using Npgsql;

namespace ReapDeep.PersonWorker.Deletion;

public sealed class DeletionJob(NpgsqlDataSource db, StarRocksDeleter? starRocks, DeletionOptions options, ILogger<DeletionJob> log) : BackgroundService
{
    const int BatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (starRocks is null)
        {
            log.LogWarning("ConnectionStrings:StarRocks is not set; deletion requests will not be processed");
            return;
        }
        log.LogInformation("Deletion job: every {Interval}, requests older than {MinAge}", options.Interval, options.MinAge);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (await ProcessBatchAsync(starRocks, ct) == BatchSize)
                {
                }
                await starRocks.PurgeTombstonesAsync(DateTime.UtcNow - options.TombstoneDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Deletion job failed; retrying in the next run");
            }
            await Task.Delay(options.Interval, ct);
        }
    }

    async Task<int> ProcessBatchAsync(StarRocksDeleter starRocks, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var requests = (await conn.QueryAsync<DeletionRequest>("""
            SELECT id, project_id, target_type, target_id FROM deletion_requests
            WHERE processed_at IS NULL AND requested_at < @Cutoff
            ORDER BY id LIMIT @Limit FOR UPDATE SKIP LOCKED
            """, new { Cutoff = DateTime.UtcNow - options.MinAge, Limit = BatchSize }, tx)).AsList();
        if (requests.Count == 0) return 0;

        var eventDistinctIds = 0;
        var mappings = 0;
        var persons = 0;
        var staticMembers = 0;
        foreach (var project in requests.GroupBy(r => r.ProjectId))
        {
            var batch = new DeletionBatch(conn, tx, project.Key, project.ToList());
            await batch.ResolveAndLockAsync();
            await starRocks.DeleteEventsAsync(project.Key, batch.DistinctIds, ct);
            await starRocks.DeleteCohortMembershipsAsync(project.Key, batch.DistinctIds, ct);
            var result = await batch.ApplyAsync();
            eventDistinctIds += batch.DistinctIds.Count;
            mappings += result.DistinctIds;
            persons += result.Persons;
            staticMembers += result.StaticCohortMembers;
        }

        await conn.ExecuteAsync("UPDATE deletion_requests SET processed_at = now() WHERE id = ANY(@Ids)",
            new { Ids = requests.Select(r => r.Id).ToArray() }, tx);
        await tx.CommitAsync(ct);

        log.LogInformation("Processed {Requests} deletion requests: events and cohort memberships of {EventDistinctIds} distinct_ids, {DistinctIds} distinct_id mappings, {Persons} persons and {StaticMembers} static cohort members deleted",
            requests.Count, eventDistinctIds, mappings, persons, staticMembers);
        return requests.Count;
    }
}
