using Dapper;
using Npgsql;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Flags;

namespace ReapDeep.QueryApi.Flags;

public sealed class FlagRepository(NpgsqlDataSource db)
{
    internal sealed record FlagRecord(
        long Id, string Key, string Name, string Description, bool Enabled, int RolloutPercentage, string Variants, string Conditions,
        string? CreatedBy, string? UpdatedBy, DateTime CreatedAt, DateTime UpdatedAt, string? LockedByExperiment)
    {
        public FeatureFlag ToContract() => new(Id, Key, Name, Description, Enabled, RolloutPercentage, FlagRules.ParseVariants(Variants),
            FlagRules.ParseConditions(Conditions), CreatedBy, UpdatedBy, CreatedAt, UpdatedAt, LockedByExperiment);
    }

    public sealed record FlagValues(string Name, string Description, bool Enabled, int RolloutPercentage, string Variants, string Conditions);

    const string Select = """
        SELECT f.id, f.key, f.name, f.description, f.enabled, f.rollout_percentage, f.variants::text AS variants, f.conditions::text AS conditions,
               c.email AS created_by, u.email AS updated_by, f.created_at, f.updated_at,
               (SELECT x.name FROM experiments x WHERE x.flag_id = f.id AND x.status = 'running' ORDER BY x.id LIMIT 1) AS locked_by_experiment
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

    public async Task<FeatureFlag?> CreateAsync(long projectId, string key, FlagValues v, string salt, long createdBy, long[] cohortIds, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockCohortsAsync(conn, tx, projectId, cohortIds, ct);
        var id = await conn.ExecuteScalarAsync<long?>("""
            INSERT INTO feature_flags (project_id, key, name, description, enabled, rollout_percentage, variants, conditions, salt, created_by, updated_by)
            VALUES (@ProjectId, @Key, @Name, @Description, @Enabled, @RolloutPercentage, @Variants::jsonb, @Conditions::jsonb, @Salt, @CreatedBy, @CreatedBy)
            ON CONFLICT (project_id, key) DO NOTHING
            RETURNING id
            """, new { ProjectId = projectId, Key = key, v.Name, v.Description, v.Enabled, v.RolloutPercentage, v.Variants, v.Conditions, Salt = salt,
            CreatedBy = createdBy }, tx);
        await tx.CommitAsync(ct);
        return id is null ? null : await GetAsync(conn, projectId, id.Value);
    }

    public async Task<FeatureFlag?> UpdateAsync(long projectId, long id, FlagValues v, long updatedBy, long[] cohortIds, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockCohortsAsync(conn, tx, projectId, cohortIds, ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE feature_flags SET name = @Name, description = @Description, enabled = @Enabled, rollout_percentage = @RolloutPercentage,
                   variants = @Variants::jsonb, conditions = @Conditions::jsonb, updated_by = @UpdatedBy, updated_at = now()
            WHERE id = @Id AND project_id = @ProjectId
              AND (variants = @Variants::jsonb OR NOT EXISTS (SELECT 1 FROM experiments x WHERE x.flag_id = feature_flags.id AND x.status = 'running'))
            """, new { v.Name, v.Description, v.Enabled, v.RolloutPercentage, v.Variants, v.Conditions, UpdatedBy = updatedBy, Id = id, ProjectId = projectId }, tx);
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        await tx.CommitAsync(ct);
        return await GetAsync(conn, projectId, id);
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM feature_flags WHERE id = @Id AND project_id = @ProjectId", new { Id = id, ProjectId = projectId }) > 0;
    }

    static async Task<FeatureFlag?> GetAsync(NpgsqlConnection conn, long projectId, long id) =>
        (await conn.QuerySingleOrDefaultAsync<FlagRecord>(Select + " AND f.id = @Id", new { ProjectId = projectId, Id = id }))?.ToContract();

    static async Task LockCohortsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long projectId, long[] cohortIds, CancellationToken ct)
    {
        if (cohortIds.Length == 0) return;
        var found = new List<long>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id FROM cohorts WHERE project_id = $1 AND id = ANY($2) AND deleted_at IS NULL FOR SHARE", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
            cmd.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = cohortIds });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found.Add(reader.GetInt64(0));
        }
        var missing = cohortIds.Distinct().Except(found).ToList();
        if (missing.Count == 0) return;
        await tx.RollbackAsync(ct);
        throw new ValidationException($"Unknown cohort in this project: {string.Join(", ", missing)}");
    }

    public async Task<List<long>> ExistingCohortsAsync(long projectId, long[] ids, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<long>("SELECT id FROM cohorts WHERE project_id = @ProjectId AND id = ANY(@Ids) AND deleted_at IS NULL",
            new { ProjectId = projectId, Ids = ids })).AsList();
    }

    public async Task<List<string>> NamesUsingCohortAsync(long projectId, long cohortId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT f.key FROM feature_flags f
            WHERE f.project_id = $1 AND EXISTS (
                SELECT 1 FROM jsonb_array_elements(f.conditions) c
                WHERE c->>'op' IN ('in_cohort', 'not_in_cohort') AND c->>'value' = $2
            )
            ORDER BY f.key
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = projectId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cohortId.ToString() });
        var keys = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) keys.Add(reader.GetString(0));
        return keys;
    }
}
