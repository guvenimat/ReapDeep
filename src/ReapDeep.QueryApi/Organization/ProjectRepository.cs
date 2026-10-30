using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Organization;

public sealed class ProjectRepository(NpgsqlDataSource db)
{
    const string Select = """
        SELECT p.id, p.name, p.organization_id, o.name AS organization_name, p.timezone, p.public_key, p.created_at
        FROM projects p JOIN organizations o ON o.id = p.organization_id
        """;

    public async Task<ProjectDetail?> GetAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ProjectDetail>(Select + " WHERE p.id = @Id", new { Id = projectId });
    }

    public async Task<ProjectDetail?> CreateAsync(long currentProjectId, string name, string timezone, string publicKey, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long?>("""
            INSERT INTO projects (organization_id, name, public_key, timezone)
            SELECT c.organization_id, @Name, @PublicKey, @Timezone FROM projects c
            WHERE c.id = @CurrentProjectId
              AND NOT EXISTS (SELECT 1 FROM projects x WHERE x.organization_id = c.organization_id AND lower(x.name) = lower(@Name))
            RETURNING id
            """, new { CurrentProjectId = currentProjectId, Name = name, PublicKey = publicKey, Timezone = timezone });
        return id is null ? null : await conn.QuerySingleAsync<ProjectDetail>(Select + " WHERE p.id = @Id", new { Id = id.Value });
    }
}
