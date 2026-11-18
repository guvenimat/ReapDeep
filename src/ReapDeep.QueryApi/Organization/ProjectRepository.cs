using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Organization;

public sealed class ProjectRepository(NpgsqlDataSource db)
{
    const string Select = """
        SELECT p.id, p.name, p.organization_id, o.name AS organization_name, p.timezone, p.public_key, p.created_at, p.anonymize_ip, p.ingest_events_per_second, p.recommendation_secret
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

    public async Task SetAnonymizeIpAsync(long projectId, bool anonymize, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE projects SET anonymize_ip = @Anonymize WHERE id = @Id", new { Id = projectId, Anonymize = anonymize });
    }

    public async Task SetRecommendationSecretAsync(long projectId, string secret, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE projects SET recommendation_secret = @Secret WHERE id = @Id", new { Id = projectId, Secret = secret });
    }

    public sealed record DeletedProject(List<string> KeyHashes, List<long> MemberIds);
    public readonly record struct DeleteResult(bool LastProject, DeletedProject? Deleted);

    public async Task<DeleteResult> DeleteAsync(long projectId, long requestedBy, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var org = await conn.ExecuteScalarAsync<long?>("SELECT organization_id FROM projects WHERE id = @Id FOR UPDATE", new { Id = projectId }, tx);
        if (org is null) return new DeleteResult(false, null);
        var siblings = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM projects WHERE organization_id = @Org", new { Org = org.Value }, tx);
        if (siblings <= 1) return new DeleteResult(true, null);
        var keys = (await conn.QueryAsync<string>("SELECT key_hash FROM api_keys WHERE project_id = @Id", new { Id = projectId }, tx)).AsList();
        var members = (await conn.QueryAsync<long>("SELECT user_id FROM organization_members WHERE organization_id = @Org", new { Org = org.Value }, tx)).AsList();
        await conn.ExecuteAsync("""
            INSERT INTO project_deletions (project_id, organization_id, name, requested_by)
            SELECT id, organization_id, name, @UserId FROM projects WHERE id = @Id;
            DELETE FROM projects WHERE id = @Id;
            """, new { Id = projectId, UserId = requestedBy }, tx);
        await tx.CommitAsync(ct);
        return new DeleteResult(false, new DeletedProject(keys, members));
    }
}
