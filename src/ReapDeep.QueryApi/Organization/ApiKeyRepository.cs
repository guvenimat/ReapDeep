using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Organization;

public sealed class ApiKeyRepository(NpgsqlDataSource db)
{
    public sealed record ActiveKey(long ProjectId, string Timezone);

    const string Select = """
        SELECT k.id, k.name, k.key_prefix AS prefix, u.email AS created_by, k.created_at, k.revoked_at
        FROM api_keys k LEFT JOIN users u ON u.id = k.created_by
        WHERE k.project_id = @ProjectId
        """;

    public async Task<ActiveKey?> FindActiveAsync(string keyHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ActiveKey>("""
            SELECT k.project_id, p.timezone FROM api_keys k JOIN projects p ON p.id = k.project_id
            WHERE k.key_hash = @KeyHash AND k.revoked_at IS NULL
            """, new { KeyHash = keyHash });
    }

    public async Task<List<ApiKeyRow>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ApiKeyRow>(Select + " ORDER BY k.revoked_at IS NOT NULL, k.id DESC", new { ProjectId = projectId })).AsList();
    }

    public async Task<ApiKeyRow> CreateAsync(long projectId, string name, string prefix, string keyHash, long createdBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO api_keys (project_id, name, key_prefix, key_hash, created_by)
            VALUES (@ProjectId, @Name, @Prefix, @KeyHash, @CreatedBy) RETURNING id
            """, new { ProjectId = projectId, Name = name, Prefix = prefix, KeyHash = keyHash, CreatedBy = createdBy });
        return await conn.QuerySingleAsync<ApiKeyRow>(Select + " AND k.id = @Id", new { ProjectId = projectId, Id = id });
    }

    public async Task<string?> RevokeAsync(long projectId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>("""
            UPDATE api_keys SET revoked_at = COALESCE(revoked_at, now())
            WHERE id = @Id AND project_id = @ProjectId RETURNING key_hash
            """, new { Id = id, ProjectId = projectId });
    }
}
