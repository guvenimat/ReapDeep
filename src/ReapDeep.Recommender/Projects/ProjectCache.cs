using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace ReapDeep.Recommender.Projects;

public sealed class ProjectCache(NpgsqlDataSource db, ILogger<ProjectCache> log) : BackgroundService
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    internal sealed record ProjectKeyRecord(string PublicKey, long Id, string? RecommendationSecret);
    internal sealed record ServerKeyRecord(string KeyHash, long ProjectId);

    volatile FrozenDictionary<string, long>? _projects;
    volatile FrozenDictionary<long, string> _secrets = FrozenDictionary<long, string>.Empty;
    volatile FrozenDictionary<string, long> _serverKeys = FrozenDictionary<string, long>.Empty;

    public bool IsLoaded => _projects is not null;

    public bool TryGetProjectId(string publicKey, out long projectId)
    {
        projectId = 0;
        return _projects?.TryGetValue(publicKey, out projectId) == true;
    }

    public bool TryGetProjectIdBySecret(string serverKey, out long projectId) =>
        _serverKeys.TryGetValue(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serverKey))), out projectId);

    public string? RecommendationSecret(long projectId) => _secrets.GetValueOrDefault(projectId);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                var rows = (await conn.QueryAsync<ProjectKeyRecord>("SELECT public_key, id, recommendation_secret FROM projects")).AsList();
                _serverKeys = (await conn.QueryAsync<ServerKeyRecord>(
                        "SELECT key_hash, project_id FROM api_keys WHERE scope = 'server' AND revoked_at IS NULL"))
                    .ToFrozenDictionary(r => r.KeyHash, r => r.ProjectId);
                _secrets = rows.Where(r => !string.IsNullOrEmpty(r.RecommendationSecret)).ToFrozenDictionary(r => r.Id, r => r.RecommendationSecret!);
                _projects = rows.ToFrozenDictionary(r => r.PublicKey, r => r.Id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Failed to load projects");
            }
            await Task.Delay(IsLoaded ? RefreshInterval : RetryInterval, ct);
        }
    }
}
