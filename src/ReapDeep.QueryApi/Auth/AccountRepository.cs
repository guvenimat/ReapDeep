using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Auth;

public sealed class AccountRepository(NpgsqlDataSource db)
{
    public sealed record Credentials(long Id, string PasswordHash);

    public async Task<Credentials?> FindCredentialsAsync(string email, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Credentials>(
            "SELECT id, password_hash FROM users WHERE email = @Email", new { Email = email });
    }

    public async Task<MeResponse?> FindSessionAsync(string tokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var user = await conn.QuerySingleOrDefaultAsync<UserInfo>("""
            SELECT u.id, u.email, u.name FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = @TokenHash AND s.expires_at > now()
            """, new { TokenHash = tokenHash });
        if (user is null) return null;

        var projects = await conn.QueryAsync<ProjectInfo>("""
            SELECT p.id, p.name, o.id AS organization_id, o.name AS organization_name, m.role, p.timezone
            FROM organization_members m
            JOIN organizations o ON o.id = m.organization_id
            JOIN projects p ON p.organization_id = o.id
            WHERE m.user_id = @UserId ORDER BY o.id, p.id
            """, new { UserId = user.Id });
        return new MeResponse(user, projects.AsList());
    }

    public async Task CreateSessionAsync(long userId, string tokenHash, DateTime expiresAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            DELETE FROM sessions WHERE user_id = @UserId AND expires_at < now();
            INSERT INTO sessions (token_hash, user_id, expires_at) VALUES (@TokenHash, @UserId, @ExpiresAt);
            """, new { UserId = userId, TokenHash = tokenHash, ExpiresAt = expiresAt });
    }

    public async Task DeleteSessionAsync(string tokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM sessions WHERE token_hash = @TokenHash", new { TokenHash = tokenHash });
    }

    public async Task<bool> ChangePasswordAsync(long userId, Func<string?, bool> verifyCurrent, string newHash, string keepTokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = await conn.ExecuteScalarAsync<string?>(
            "SELECT password_hash FROM users WHERE id = @UserId FOR UPDATE", new { UserId = userId }, tx);
        if (!verifyCurrent(current)) return false;

        await conn.ExecuteAsync("""
            UPDATE users SET password_hash = @Hash WHERE id = @UserId;
            DELETE FROM sessions WHERE user_id = @UserId AND token_hash <> @Keep;
            """, new { UserId = userId, Hash = newHash, Keep = keepTokenHash }, tx);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> CreateFirstOwnerAsync(string email, string name, string passwordHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var created = await conn.ExecuteScalarAsync<long?>("""
            WITH u AS (
                INSERT INTO users (email, name, password_hash)
                SELECT @Email, @Name, @Hash WHERE NOT EXISTS (SELECT 1 FROM users)
                RETURNING id
            )
            INSERT INTO organization_members (organization_id, user_id, role)
            SELECT o.id, u.id, 'owner' FROM organizations o CROSS JOIN u
            RETURNING user_id
            """, new { Email = email, Name = name, Hash = passwordHash });
        return created is not null;
    }
}
