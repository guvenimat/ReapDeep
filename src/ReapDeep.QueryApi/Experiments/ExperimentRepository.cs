using Dapper;
using Npgsql;
using ReapDeep.Analytics.Experiments;
using ReapDeep.Analytics.Flags;

namespace ReapDeep.QueryApi.Experiments;

public sealed class ExperimentRepository(NpgsqlDataSource db)
{
    internal sealed record ExperimentRecord(
        long Id, string Name, string Description, long FlagId, string FlagKey, bool FlagEnabled, string Variants, string? ControlVariant, string Status,
        string Metric, string SecondaryMetrics, DateTime? StartDate, DateTime? EndDate, string? CreatedBy, string? UpdatedBy, DateTime CreatedAt, DateTime UpdatedAt)
    {
        public Experiment ToContract()
        {
            var variants = FlagRules.ParseVariants(Variants);
            var control = ControlVariant is { } c && variants.Any(v => v.Key == c) ? c : variants.FirstOrDefault()?.Key ?? "";
            return new(Id, Name, Description, FlagId, FlagKey, FlagEnabled, variants, control, Status, ExperimentRules.ParseMetric(Metric),
                ExperimentRules.ParseMetrics(SecondaryMetrics), StartDate, EndDate, CreatedBy, UpdatedBy, CreatedAt, UpdatedAt);
        }
    }

    public sealed record ExperimentValues(string Name, string Description, long FlagId, string ControlVariant, string Metric, string SecondaryMetrics,
        DateTime? StartDate, DateTime? EndDate);

    const string Select = """
        SELECT x.id, x.name, x.description, x.flag_id, f.key AS flag_key, f.enabled AS flag_enabled, f.variants::text AS variants, x.control_variant,
               x.status, x.metric::text AS metric, x.secondary_metrics::text AS secondary_metrics, x.start_date, x.end_date, c.email AS created_by,
               u.email AS updated_by, x.created_at, x.updated_at
        FROM experiments x
        JOIN feature_flags f ON f.id = x.flag_id AND f.project_id = x.project_id
        LEFT JOIN users c ON c.id = x.created_by
        LEFT JOIN users u ON u.id = x.updated_by
        WHERE x.project_id = @ProjectId
        """;

    public async Task<List<Experiment>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ExperimentRecord>(Select + " ORDER BY x.created_at DESC, x.id DESC", new { ProjectId = projectId });
        return rows.Select(r => r.ToContract()).ToList();
    }

    public async Task<Experiment?> GetAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, projectId, id);
    }

    public async Task<int> CountAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM experiments WHERE project_id = @ProjectId", new { ProjectId = projectId });
    }

    public async Task<List<string>> NamesForFlagAsync(long projectId, long flagId, bool runningOnly, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var names = await conn.QueryAsync<string>(
            "SELECT name FROM experiments WHERE project_id = @ProjectId AND flag_id = @FlagId AND (status = 'running' OR NOT @RunningOnly) ORDER BY id",
            new { ProjectId = projectId, FlagId = flagId, RunningOnly = runningOnly });
        return names.ToList();
    }

    public async Task<Experiment?> CreateAsync(long projectId, ExperimentValues v, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO experiments (project_id, flag_id, name, description, metric, secondary_metrics, control_variant, start_date, end_date, created_by, updated_by)
            VALUES (@ProjectId, @FlagId, @Name, @Description, @Metric::jsonb, @SecondaryMetrics::jsonb, @ControlVariant, @StartDate, @EndDate, @CreatedBy, @CreatedBy)
            RETURNING id
            """, new { ProjectId = projectId, v.FlagId, v.Name, v.Description, v.Metric, v.SecondaryMetrics, v.ControlVariant, v.StartDate, v.EndDate,
            CreatedBy = createdBy });
        return await GetAsync(conn, projectId, id);
    }

    public async Task<Experiment?> UpdateAsync(long projectId, long id, ExperimentValues v, long updatedBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE experiments SET name = @Name, description = @Description, flag_id = @FlagId, metric = @Metric::jsonb,
                   secondary_metrics = @SecondaryMetrics::jsonb, control_variant = @ControlVariant,
                   start_date = @StartDate, end_date = @EndDate, updated_by = @UpdatedBy, updated_at = now()
            WHERE id = @Id AND project_id = @ProjectId AND (flag_id = @FlagId OR status = 'draft')
            """, new { v.Name, v.Description, v.FlagId, v.Metric, v.SecondaryMetrics, v.ControlVariant, v.StartDate, v.EndDate, UpdatedBy = updatedBy,
            Id = id, ProjectId = projectId });
        return updated == 0 ? null : await GetAsync(conn, projectId, id);
    }

    public async Task<Experiment?> StartAsync(long projectId, long id, long updatedBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE experiments SET status = 'running', start_date = COALESCE(start_date, now()), updated_by = @UpdatedBy, updated_at = now()
            WHERE id = @Id AND project_id = @ProjectId AND status = 'draft' AND (end_date IS NULL OR end_date > COALESCE(start_date, now()))
            """, new { UpdatedBy = updatedBy, Id = id, ProjectId = projectId });
        return updated == 0 ? null : await GetAsync(conn, projectId, id);
    }

    public async Task<Experiment?> StopAsync(long projectId, long id, long updatedBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE experiments SET status = 'stopped', end_date = GREATEST(LEAST(COALESCE(end_date, now()), now()), start_date + interval '1 second'),
                   updated_by = @UpdatedBy, updated_at = now()
            WHERE id = @Id AND project_id = @ProjectId AND status = 'running'
            """, new { UpdatedBy = updatedBy, Id = id, ProjectId = projectId });
        return updated == 0 ? null : await GetAsync(conn, projectId, id);
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM experiments WHERE id = @Id AND project_id = @ProjectId AND status <> 'running'",
            new { Id = id, ProjectId = projectId }) > 0;
    }

    static async Task<Experiment?> GetAsync(NpgsqlConnection conn, long projectId, long id) =>
        (await conn.QuerySingleOrDefaultAsync<ExperimentRecord>(Select + " AND x.id = @Id", new { ProjectId = projectId, Id = id }))?.ToContract();
}
