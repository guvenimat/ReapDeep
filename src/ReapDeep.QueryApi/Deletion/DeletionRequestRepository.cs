using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Deletion;

public sealed class DeletionRequestRepository(NpgsqlDataSource db)
{
    const string Select = """
        SELECT r.id, r.target_type, r.target_id, u.email AS requested_by, r.requested_at, r.processed_at
        FROM deletion_requests r LEFT JOIN users u ON u.id = r.requested_by
        WHERE r.project_id = @ProjectId
        """;

    public async Task<List<DeletionRequestRow>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<DeletionRequestRow>(Select + " ORDER BY r.id DESC LIMIT 100", new { ProjectId = projectId })).AsList();
    }

    public async Task<DeletionRequestRow> CreateOrGetPendingAsync(long projectId, string targetType, string targetId, long requestedBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>("""
            WITH existing AS (
                SELECT id FROM deletion_requests
                WHERE project_id = @ProjectId AND target_type = @TargetType AND target_id = @TargetId AND processed_at IS NULL
                LIMIT 1
            ), created AS (
                INSERT INTO deletion_requests (project_id, target_type, target_id, requested_by)
                SELECT @ProjectId, @TargetType, @TargetId, @RequestedBy WHERE NOT EXISTS (SELECT 1 FROM existing)
                RETURNING id
            )
            SELECT id FROM created UNION ALL SELECT id FROM existing
            """, new { ProjectId = projectId, TargetType = targetType, TargetId = targetId, RequestedBy = requestedBy });
        return await conn.QuerySingleAsync<DeletionRequestRow>(Select + " AND r.id = @Id", new { ProjectId = projectId, Id = id });
    }
}
