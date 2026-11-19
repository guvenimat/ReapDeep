using System.Diagnostics;
using System.Text.Json;
using ReapDeep.Analytics.Cohorts;
using ReapDeep.Analytics.Sql;
using ReapDeep.PersonWorker.Infrastructure;

namespace ReapDeep.PersonWorker.Cohorts;

public sealed class CohortRefreshJob(CohortRepository cohorts, StarRocksStore? store, CohortOptions options, ILogger<CohortRefreshJob> log) : BackgroundService
{
    const int StaticChunkSize = 500;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (store is null)
        {
            log.LogWarning("ConnectionStrings:StarRocks is not set; cohorts will not be calculated");
            return;
        }
        log.LogInformation("Cohort job: polling every {Poll}, refresh at most every {Max}, {Concurrency} at a time",
            options.PollInterval, options.MaxRefreshInterval, options.Concurrency);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RemoveDeletedAsync(store, ct);
                await DropExpiredVersionsAsync(store, ct);
                var due = await cohorts.DueAsync(options.MaxRefreshInterval, options.Concurrency, ct);
                await Task.WhenAll(due.Select(c => RefreshAsync(store, c, ct)));
                if (due.Count == options.Concurrency) continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Cohort job failed; retrying in the next run");
            }
            await Task.Delay(options.PollInterval, ct);
        }
    }

    async Task RefreshAsync(StarRocksStore store, CohortRepository.DueCohortRecord cohort, CancellationToken ct)
    {
        await using var lockConn = await cohorts.TryLockAsync(cohort.Id, ct);
        if (lockConn is null) return;

        await CohortProjectLock.EnterRefreshAsync(lockConn, cohort.ProjectId);
        var sw = Stopwatch.StartNew();
        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            await cohorts.MarkCalculatingAsync(cohort.Id, ct);
            if (cohort.Kind == "static") await WriteStaticAsync(store, cohort, version, ct);
            else await store.ExecuteAsync(CohortSql.Materialize(cohort.ProjectId, cohort.Id, version, await DefinitionAsync(cohort, ct), DateTime.UtcNow), ct);

            var members = await store.ScalarAsync(CohortSql.PersonCount(cohort.ProjectId, cohort.Id, version), ct);
            if (await cohorts.CompleteAsync(cohort, version, members, (int)sw.ElapsedMilliseconds, ct))
            {
                try
                {
                    await store.ExecuteAsync(CohortSql.Snapshot(cohort.ProjectId, cohort.Id, version, DateTime.UtcNow), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning(ex, "Cohort {Cohort} (project {Project}) was calculated but its history snapshot failed", cohort.Id, cohort.ProjectId);
                }
                log.LogInformation("Cohort {Cohort} (project {Project}): {Members} persons in {Ms} ms", cohort.Id, cohort.ProjectId, members, sw.ElapsedMilliseconds);
            }
            else
                await store.ExecuteAsync(CohortSql.DeleteVersion(cohort.ProjectId, cohort.Id, version), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Cohort {Cohort} (project {Project}) failed", cohort.Id, cohort.ProjectId);
            await store.ExecuteAsync(CohortSql.DeleteVersion(cohort.ProjectId, cohort.Id, version), ct);
            await cohorts.FailAsync(cohort.Id, ex.Message, ct);
        }
        finally
        {
            await CohortProjectLock.ExitRefreshAsync(lockConn, cohort.ProjectId);
            await CohortRepository.UnlockAsync(lockConn, cohort.Id);
        }
    }

    async Task WriteStaticAsync(StarRocksStore store, CohortRepository.DueCohortRecord cohort, long version, CancellationToken ct)
    {
        var ids = await cohorts.StaticMembersAsync(cohort.Id, ct);
        foreach (var chunk in ids.Chunk(StaticChunkSize))
            await store.ExecuteAsync(CohortSql.InsertStatic(cohort.ProjectId, cohort.Id, version, chunk), ct);
        if (ids.Count > 0) await store.ExecuteAsync(CohortSql.ExpandStatic(cohort.ProjectId, cohort.Id, version), ct);
    }

    async Task DropExpiredVersionsAsync(StarRocksStore store, CancellationToken ct)
    {
        foreach (var v in await cohorts.ExpiredVersionsAsync(options.VersionGrace, ct))
        {
            await store.ExecuteAsync(CohortSql.DeleteVersion(v.ProjectId, v.CohortId, v.Version), ct);
            await cohorts.ForgetVersionAsync(v, ct);
        }
    }

    async Task RemoveDeletedAsync(StarRocksStore store, CancellationToken ct)
    {
        foreach (var c in await cohorts.DeletedAsync(ct))
        {
            await store.ExecuteAsync(CohortSql.DeleteCohort(c.ProjectId, c.Id), ct);
            await store.ExecuteAsync(CohortSql.DeleteHistory(c.ProjectId, c.Id), ct);
            await cohorts.RemoveAsync(c.Id, ct);
            log.LogInformation("Cohort {Cohort} (project {Project}) removed", c.Id, c.ProjectId);
        }
    }

    async Task<CohortDefinition> DefinitionAsync(CohortRepository.DueCohortRecord cohort, CancellationToken ct)
    {
        var definition = JsonSerializer.Deserialize(cohort.Definition, CohortJsonContext.Default.CohortDefinition)
            ?? throw new InvalidOperationException($"Cohort {cohort.Id} has no definition");
        var names = definition.Groups?.SelectMany(g => g.Conditions ?? [])
            .Where(c => c.Action is { Length: > 0 }).Select(c => c.Action!)
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (names.Length == 0) return definition;

        var found = await cohorts.ActionEventsAsync(cohort.ProjectId, names, ct);
        return definition with
        {
            Groups = definition.Groups!.Select(g => g with
            {
                Conditions = g.Conditions!.Select(c => c.Action is { Length: > 0 } action
                    ? c with { Events = found.TryGetValue(action, out var events) ? events.ToList() : throw new InvalidOperationException($"Unknown action: '{action}'") }
                    : c).ToList(),
            }).ToList(),
        };
    }
}
