using System.Collections.Frozen;
using Dapper;
using Npgsql;

namespace ReapDeep.Ingestion.Projects;

public sealed record ProjectSettings(long Id, bool AnonymizeIp, int? EventsPerSecond);

public sealed class ProjectCache(NpgsqlDataSource db, ILogger<ProjectCache> log) : BackgroundService
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    internal sealed record ProjectKeyRecord(string PublicKey, long Id, bool AnonymizeIp, int? IngestEventsPerSecond);

    volatile FrozenDictionary<string, ProjectSettings>? _projects;

    public bool IsLoaded => _projects is not null;

    public bool TryGetProject(string publicKey, out ProjectSettings project)
    {
        project = null!;
        return _projects?.TryGetValue(publicKey, out project!) == true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                var rows = await conn.QueryAsync<ProjectKeyRecord>("SELECT public_key, id, anonymize_ip, ingest_events_per_second FROM projects");
                _projects = rows.ToFrozenDictionary(r => r.PublicKey, r => new ProjectSettings(r.Id, r.AnonymizeIp, r.IngestEventsPerSecond));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Failed to load projects");
            }
            await Task.Delay(IsLoaded ? RefreshInterval : RetryInterval, ct);
        }
    }
}
