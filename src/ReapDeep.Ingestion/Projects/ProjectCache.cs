using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace ReapDeep.Ingestion.Projects;

public sealed record ProjectSettings(long Id, bool AnonymizeIp, int? EventsPerSecond);

public sealed class ProjectCache(NpgsqlDataSource db, ILogger<ProjectCache> log) : BackgroundService
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    internal sealed record ProjectKeyRecord(string PublicKey, long Id, bool AnonymizeIp, int? IngestEventsPerSecond);
    internal sealed record ServerKeyRecord(string KeyHash, long Id, bool AnonymizeIp, int? IngestEventsPerSecond);

    volatile FrozenDictionary<string, ProjectSettings>? _projects;
    volatile FrozenDictionary<string, ProjectSettings> _serverKeys = FrozenDictionary<string, ProjectSettings>.Empty;

    public bool IsLoaded => _projects is not null;

    public bool TryGetProject(string publicKey, out ProjectSettings project)
    {
        project = null!;
        return _projects?.TryGetValue(publicKey, out project!) == true;
    }

    public bool TryGetProjectBySecret(string serverKey, out ProjectSettings project) =>
        _serverKeys.TryGetValue(HashKey(serverKey), out project!);

    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                var rows = await conn.QueryAsync<ProjectKeyRecord>("SELECT public_key, id, anonymize_ip, ingest_events_per_second FROM projects");
                var serverKeys = await conn.QueryAsync<ServerKeyRecord>("""
                    SELECT k.key_hash, p.id, p.anonymize_ip, p.ingest_events_per_second
                    FROM api_keys k JOIN projects p ON p.id = k.project_id
                    WHERE k.scope = 'server' AND k.revoked_at IS NULL
                    """);
                _serverKeys = serverKeys.ToFrozenDictionary(r => r.KeyHash, r => new ProjectSettings(r.Id, r.AnonymizeIp, r.IngestEventsPerSecond));
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
