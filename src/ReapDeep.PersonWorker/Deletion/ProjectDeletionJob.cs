using Dapper;
using Npgsql;
using ReapDeep.PersonWorker.Infrastructure;

namespace ReapDeep.PersonWorker.Deletion;

public sealed class ProjectDeletionJob(NpgsqlDataSource db, StarRocksDeleter? starRocks, IConfiguration cfg, ILogger<ProjectDeletionJob> log) : BackgroundService
{
    readonly TimeSpan _interval = cfg.Seconds("ProjectDeletion:PollSeconds", 30);
    readonly TimeSpan _delay = cfg.Seconds("ProjectDeletion:DelaySeconds", 300);

    internal sealed record PendingRecord(long Id, long ProjectId, string Name);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (starRocks is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var p in await PendingAsync(ct)) await RunAsync(starRocks, p, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Project deletion job failed; retrying in the next run");
            }
            await Task.Delay(_interval, ct);
        }
    }

    async Task<List<PendingRecord>> PendingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<PendingRecord>("""
            SELECT id, project_id, name FROM project_deletions
            WHERE completed_at IS NULL AND requested_at < now() - make_interval(secs => @Delay)
            ORDER BY requested_at LIMIT 5
            """, new { Delay = _delay.TotalSeconds })).AsList();
    }

    async Task RunAsync(StarRocksDeleter deleter, PendingRecord p, CancellationToken ct)
    {
        string? error = null;
        try
        {
            await deleter.DeleteProjectAsync(p.ProjectId, ct);
            log.LogInformation("Project {Project} ({Name}): analytics data deleted", p.ProjectId, p.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            error = ex.Message;
            log.LogWarning(ex, "Project {Project}: deleting analytics data failed; retrying", p.ProjectId);
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE project_deletions SET completed_at = CASE WHEN @Error IS NULL THEN now() END, last_error = @Error WHERE id = @Id
            """, new { p.Id, Error = error });
    }
}
