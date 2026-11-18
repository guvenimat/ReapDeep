using System.Collections.Frozen;
using Dapper;
using Npgsql;
using ReapDeep.Analytics.Flags;

namespace ReapDeep.Recommender.Flags;

public sealed record ServerFlag(string Key, string Salt, int RolloutPercentage, List<FlagVariant> Variants, List<FlagCondition> Conditions);

public sealed record FlagDefinitionsResponse(List<ServerFlag> Flags);

public sealed class FlagCache(NpgsqlDataSource db, IConfiguration cfg, ILogger<FlagCache> log) : BackgroundService
{
    readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(int.TryParse(cfg["Recommender:FlagRefreshSeconds"], out var s) && s > 0 ? s : 5);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    internal sealed record VersionRecord(long Flags, DateTime? Updated);
    internal sealed record FlagRecord(long ProjectId, string Key, string Salt, int RolloutPercentage, string Variants, string Conditions);

    volatile FrozenDictionary<long, CompiledFlag[]>? _flags;
    volatile FrozenDictionary<long, ServerFlag[]> _definitions = FrozenDictionary<long, ServerFlag[]>.Empty;
    VersionRecord? _version;

    public bool IsLoaded => _flags is not null;

    public IReadOnlyList<CompiledFlag> Get(long projectId) => _flags?.GetValueOrDefault(projectId) ?? [];

    public IReadOnlyList<ServerFlag> Definitions(long projectId) => _definitions.GetValueOrDefault(projectId) ?? [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Failed to refresh the feature flag cache");
            }
            await Task.Delay(IsLoaded ? _refreshInterval : RetryInterval, ct);
        }
    }

    async Task RefreshAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var version = await conn.QuerySingleAsync<VersionRecord>("SELECT count(*) AS flags, max(updated_at) AS updated FROM feature_flags");
        if (IsLoaded && version == _version) return;

        var rows = await conn.QueryAsync<FlagRecord>("""
            SELECT project_id, key, salt, rollout_percentage, variants::text AS variants, conditions::text AS conditions
            FROM feature_flags WHERE enabled ORDER BY project_id, key
            """);
        var compiled = new Dictionary<long, List<CompiledFlag>>();
        var definitions = new Dictionary<long, List<ServerFlag>>();
        foreach (var r in rows)
        {
            try
            {
                var (variants, conditions) = (FlagRules.ParseVariants(r.Variants), FlagRules.ParseConditions(r.Conditions));
                var flag = new CompiledFlag(r.Key, r.Salt, r.RolloutPercentage, variants, conditions);
                if (!compiled.TryGetValue(r.ProjectId, out var list)) compiled[r.ProjectId] = list = [];
                list.Add(flag);
                if (!definitions.TryGetValue(r.ProjectId, out var defs)) definitions[r.ProjectId] = defs = [];
                defs.Add(new ServerFlag(r.Key, r.Salt, r.RolloutPercentage, variants, conditions));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Project {Project}: flag {Flag} could not be loaded and is served as off", r.ProjectId, r.Key);
            }
        }
        _definitions = definitions.ToFrozenDictionary(p => p.Key, p => p.Value.ToArray());
        _flags = compiled.ToFrozenDictionary(p => p.Key, p => p.Value.ToArray());
        _version = version;
        log.LogInformation("Loaded {Flags} enabled feature flags in {Projects} projects", compiled.Sum(p => p.Value.Count), compiled.Count);
    }
}
