using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Organization;

public enum AddMemberResult
{
    Added,
    AlreadyMember,
    PasswordRequired,
}

public sealed class MemberRepository(NpgsqlDataSource db)
{
    internal sealed record MembershipRecord(long OrganizationId, long UserId, string Role);

    const string Select = """
        SELECT u.id AS user_id, u.email, u.name, m.role, m.created_at
        FROM organization_members m JOIN users u ON u.id = m.user_id
        WHERE m.organization_id = (SELECT organization_id FROM projects WHERE id = @ProjectId)
        """;

    public async Task<List<MemberRow>> ListAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<MemberRow>(Select + " ORDER BY u.email", new { ProjectId = projectId })).AsList();
    }

    public async Task<MemberRow?> GetAsync(long projectId, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MemberRow>(Select + " AND u.id = @UserId", new { ProjectId = projectId, UserId = userId });
    }

    public async Task<(AddMemberResult Result, long UserId)> AddAsync(long projectId, string email, string name, string? passwordHash, string role,
        CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var userId = await conn.ExecuteScalarAsync<long?>("SELECT id FROM users WHERE email = @Email", new { Email = email }, tx);
        if (userId is null)
        {
            if (passwordHash is null) return (AddMemberResult.PasswordRequired, 0);
            userId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO users (email, name, password_hash) VALUES (@Email, @Name, @Hash) RETURNING id",
                new { Email = email, Name = name, Hash = passwordHash }, tx);
        }

        var added = await conn.ExecuteAsync("""
            INSERT INTO organization_members (organization_id, user_id, role)
            SELECT organization_id, @UserId, @Role FROM projects WHERE id = @ProjectId
            ON CONFLICT DO NOTHING
            """, new { UserId = userId.Value, Role = role, ProjectId = projectId }, tx);
        if (added == 0) return (AddMemberResult.AlreadyMember, userId.Value);

        await tx.CommitAsync(ct);
        return (AddMemberResult.Added, userId.Value);
    }

    public async Task<MembershipError> ChangeAsync(long projectId, string actorRole, long userId, string? newRole, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var members = (await conn.QueryAsync<MembershipRecord>("""
            SELECT organization_id, user_id, role FROM organization_members
            WHERE organization_id = (SELECT organization_id FROM projects WHERE id = @ProjectId)
            FOR UPDATE
            """, new { ProjectId = projectId }, tx)).AsList();
        var error = MembershipRules.CanChange(actorRole, members.ToDictionary(m => m.UserId, m => m.Role), userId, newRole);
        if (error != MembershipError.None) return error;

        var organizationId = members[0].OrganizationId;
        if (newRole is null)
            await conn.ExecuteAsync("DELETE FROM organization_members WHERE organization_id = @OrganizationId AND user_id = @UserId",
                new { OrganizationId = organizationId, UserId = userId }, tx);
        else
            await conn.ExecuteAsync("UPDATE organization_members SET role = @Role WHERE organization_id = @OrganizationId AND user_id = @UserId",
                new { Role = newRole, OrganizationId = organizationId, UserId = userId }, tx);
        await tx.CommitAsync(ct);
        return MembershipError.None;
    }
}
