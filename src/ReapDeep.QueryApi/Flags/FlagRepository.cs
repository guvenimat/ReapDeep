using Dapper;
using Npgsql;
using ReapDeep.Analytics.Flags;

namespace ReapDeep.QueryApi.Flags;

public sealed class FlagRepository(NpgsqlDataSource db)
{
    internal sealed record FlagRecord(
        long Id, string Key, string Name, string Description, bool Enabled, int RolloutPercentage, string Variants, string Conditions,
        string? CreatedBy, string? UpdatedBy, DateTime CreatedAt, DateTime UpdatedAt)
    {
        public FeatureFlag ToContract() => new(Id, Key, Name, Description, Enabled, RolloutPercentage, FlagRules.ParseVariants(Variants),
            FlagRules.ParseConditions(Conditions), CreatedBy, UpdatedBy, CreatedAt, UpdatedAt);
    }

    public sealed record FlagValues(string Name, string Description, bool Enabled, int RolloutPercentage, string Variants, string Conditions);

    const string Select = """
        SELECT f.id, f.key, f.name, f.description, f.enabled, f.rollout_percentage, f.variants::text AS variants, f.conditions::text AS conditions,
               c.email AS created_by, u.email AS updated_by, f.created_at, f.updated_at
        FROM feature_flags f
        LEFT JOIN users c ON c.id = f.created_by
        LEFT JOIN users u ON u.id = f.updated_by
        WHERE f.project_id = @ProjectId
        """;

    public async Task<List<FeatureFlag>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FlagRecord>(Select + " ORDER BY f.key", new { ProjectId = projectId });
        return rows.Select(r => r.ToContract()).ToList();
    }

    public async Task<FeatureFlag?> GetAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, projectId, id);
    }

    public async Task<int> CountAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM feature_flags WHERE project_id = @ProjectId", new { ProjectId = projectId });
    }

    public async Task<FeatureFlag?> CreateAsync(long projectId, string key, FlagValues v, string salt, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long?>("""
            INSERT INTO feature_flags (project_id, key, name, description, enabled, rollout_percentage, variants, conditions, salt, created_by, updated_by)
            VALUES (@ProjectId, @Key, @Name, @Description, @Enabled, @RolloutPercentage, @Variants::jsonb, @Conditions::jsonb, @Salt, @CreatedBy, @CreatedBy)
            ON CONFLICT (project_id, key) DO NOTHING
            RETURNING id
            """, new { ProjectId = projectId, Key = key, v.Name, v.Description, v.Enabled, v.RolloutPercentage, v.Variants, v.Conditions, Salt = salt,
            CreatedBy = createdBy });
        return id is null ? null : await GetAsync(conn, projectId, id.Value);
    }

    public async Task<FeatureFlag?> UpdateAsync(long projectId, long id, FlagValues v, long updatedBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE feature_flags SET name = @Name, description = @Description, enabled = @Enabled, rollout_percentage = @RolloutPercentage,
                   variants = @Variants::jsonb, conditions = @Conditions::jsonb, updated_by = @UpdatedBy, updated_at = now()
            WHERE id = @Id AND project_id = @ProjectId
              AND (variants = @Variants::jsonb OR NOT EXISTS (SELECT 1 FROM experiments x WHERE x.flag_id = feature_flags.id AND x.status = 'running'))
            """, new { v.Name, v.Description, v.Enabled, v.RolloutPercentage, v.Variants, v.Conditions, UpdatedBy = updatedBy, Id = id, ProjectId = projectId });
        return updated == 0 ? null : await GetAsync(conn, projectId, id);
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM feature_flags WHERE id = @Id AND project_id = @ProjectId", new { Id = id, ProjectId = projectId }) > 0;
    }

    static async Task<FeatureFlag?> GetAsync(NpgsqlConnection conn, long projectId, long id) =>
        (await conn.QuerySingleOrDefaultAsync<FlagRecord>(Select + " AND f.id = @Id", new { ProjectId = projectId, Id = id }))?.ToContract();
}
