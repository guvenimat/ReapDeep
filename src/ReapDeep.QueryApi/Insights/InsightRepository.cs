using System.Data;
using Dapper;
using Npgsql;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Insights;

public sealed class InsightRepository(NpgsqlDataSource db)
{
    internal sealed record InsightRecord(long Id, string Name, string Kind, string Query, string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt)
    {
        public InsightRow ToContract() => new(Id, Name, Kind, JsonText.Parse(Query), CreatedBy, CreatedAt, UpdatedAt);
    }

    internal const string Columns = "i.id, i.name, i.kind, i.query::text AS query, u.email AS created_by, i.created_at, i.updated_at";

    const string Select = $"SELECT {Columns} FROM insights i LEFT JOIN users u ON u.id = i.created_by WHERE i.project_id = @ProjectId";

    public async Task<List<InsightRow>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<InsightRecord>(Select + " ORDER BY i.updated_at DESC LIMIT 500", new { ProjectId = projectId });
        return rows.Select(r => r.ToContract()).ToList();
    }

    public async Task<InsightRow?> GetAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetAsync(conn, projectId, id);
    }

    public async Task<InsightRow> CreateAsync(long projectId, string name, string kind, string query, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO insights (project_id, name, kind, query, created_by)
            VALUES (@ProjectId, @Name, @Kind, @Query::jsonb, @CreatedBy) RETURNING id
            """, new { ProjectId = projectId, Name = name, Kind = kind, Query = query, CreatedBy = createdBy });
        return (await GetAsync(conn, projectId, id))!;
    }

    public async Task<InsightRow?> UpdateAsync(long projectId, long id, string? name, string? query, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE insights SET name = COALESCE(@Name::text, name), query = COALESCE(@Query::jsonb, query), updated_at = now()
            WHERE id = @Id AND project_id = @ProjectId
            """, new { Name = name, Query = query, Id = id, ProjectId = projectId });
        return updated == 0 ? null : await GetAsync(conn, projectId, id);
    }

    public async Task<bool> DeleteAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM insights WHERE id = @Id AND project_id = @ProjectId", new { Id = id, ProjectId = projectId }) > 0;
    }

    static async Task<InsightRow?> GetAsync(IDbConnection conn, long projectId, long id) =>
        (await conn.QuerySingleOrDefaultAsync<InsightRecord>(Select + " AND i.id = @Id", new { ProjectId = projectId, Id = id }))?.ToContract();
}
