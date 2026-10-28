using System.Collections.Frozen;
using Npgsql;

namespace ReapDeep.Ingestion;

public sealed class ProjectCache(NpgsqlDataSource db, ILogger<ProjectCache> log) : BackgroundService
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

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
                var map = new Dictionary<string, long>();
                await using var cmd = db.CreateCommand("SELECT public_key, id FROM projects");
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    map[reader.GetString(0)] = reader.GetInt64(1);

                _projects = map.ToFrozenDictionary();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Failed to load projects");
            }

            await Task.Delay(IsLoaded ? RefreshInterval : TimeSpan.FromSeconds(2), ct);
        }
    }
}
