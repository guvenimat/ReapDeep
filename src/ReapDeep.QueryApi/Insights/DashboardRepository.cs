using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Insights;

public enum SetItemsResult
{
    Updated,
    NotFound,
    UnknownInsight,
}

public sealed class DashboardRepository(NpgsqlDataSource db)
{
    internal sealed record DashboardRecord(long Id, string Name, string Description, DateTime UpdatedAt);

    public async Task<List<DashboardRow>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<DashboardRow>("""
            SELECT d.id, d.name, d.description, u.email AS created_by, d.updated_at,
                   (SELECT count(*)::int FROM dashboard_items di WHERE di.dashboard_id = d.id) AS items
            FROM dashboards d LEFT JOIN users u ON u.id = d.created_by
            WHERE d.project_id = @ProjectId ORDER BY d.updated_at DESC LIMIT 200
            """, new { ProjectId = projectId })).AsList();
    }

    public async Task<DashboardDetail?> GetAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var dashboard = await conn.QuerySingleOrDefaultAsync<DashboardRecord>(
            "SELECT id, name, description, updated_at FROM dashboards WHERE id = @Id AND project_id = @ProjectId",
            new { Id = id, ProjectId = projectId });
        if (dashboard is null) return null;

        var items = await conn.QueryAsync<InsightRepository.InsightRecord>($"""
            SELECT {InsightRepository.Columns}
            FROM dashboard_items di JOIN insights i ON i.id = di.insight_id LEFT JOIN users u ON u.id = i.created_by
            WHERE di.dashboard_id = @Id ORDER BY di.position
            """, new { Id = id });
        return new DashboardDetail(dashboard.Id, dashboard.Name, dashboard.Description, dashboard.UpdatedAt, items.Select(i => i.ToContract()).ToList());
    }

    public async Task<long> CreateAsync(long projectId, string name, string description, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO dashboards (project_id, name, description, created_by)
            VALUES (@ProjectId, @Name, @Description, @CreatedBy) RETURNING id
            """, new { ProjectId = projectId, Name = name, Description = description, CreatedBy = createdBy });
    }

    public async Task<bool> UpdateAsync(long projectId, long id, string? name, string? description, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE dashboards SET name = COALESCE(@Name::text, name), description = COALESCE(@Description::text, description), updated_at = now()
            WHERE id = @Id AND project_id = @ProjectId
            """, new { Name = name, Description = description, Id = id, ProjectId = projectId }) > 0;
    }

    public async Task<SetItemsResult> SetItemsAsync(long projectId, long id, long[] insightIds, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM dashboards WHERE id = @Id AND project_id = @ProjectId FOR UPDATE)", new { Id = id, ProjectId = projectId }, tx);
        if (!exists) return SetItemsResult.NotFound;

        var known = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM insights WHERE project_id = @ProjectId AND id = ANY(@Ids)", new { ProjectId = projectId, Ids = insightIds }, tx);
        if (known != insightIds.Length) return SetItemsResult.UnknownInsight;

        await conn.ExecuteAsync("""
            DELETE FROM dashboard_items WHERE dashboard_id = @Id;
            INSERT INTO dashboard_items (dashboard_id, insight_id, position)
            SELECT @Id, item, ord FROM unnest(@Ids) WITH ORDINALITY AS u(item, ord);
            UPDATE dashboards SET updated_at = now() WHERE id = @Id;
            """, new { Id = id, Ids = insightIds }, tx);
        await tx.CommitAsync(ct);
        return SetItemsResult.Updated;
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM dashboards WHERE id = @Id AND project_id = @ProjectId", new { Id = id, ProjectId = projectId }) > 0;
    }
}
