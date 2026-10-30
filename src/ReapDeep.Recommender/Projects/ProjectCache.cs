using System.Collections.Frozen;
using Dapper;
using Npgsql;

namespace ReapDeep.Recommender.Projects;

public sealed class ProjectCache(NpgsqlDataSource db, ILogger<ProjectCache> log) : BackgroundService
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    internal sealed record ProjectKeyRecord(string PublicKey, long Id);

    volatile FrozenDictionary<string, long>? _projects;

    public bool IsLoaded => _projects is not null;

    public bool TryGetProjectId(string publicKey, out long projectId)
    {
        projectId = 0;
        return _projects?.TryGetValue(publicKey, out projectId) == true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                var rows = await conn.QueryAsync<ProjectKeyRecord>("SELECT public_key, id FROM projects");
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
