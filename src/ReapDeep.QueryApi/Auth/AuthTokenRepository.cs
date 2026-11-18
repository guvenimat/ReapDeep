using Dapper;
using Npgsql;

namespace ReapDeep.QueryApi.Auth;

public sealed class AuthTokenRepository(NpgsqlDataSource db)
{
    public sealed record TokenInfoRecord(long UserId, string Email, string Purpose);
    public sealed record InviteContextRecord(string Inviter, string Organization, bool Pending);

    public async Task<long?> FindUserIdAsync(string email, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long?>("SELECT id FROM users WHERE email = @Email", new { Email = email });
    }

    public async Task<bool> CreateAsync(long userId, string purpose, string tokenHash, TimeSpan lifetime, int maxPerHour, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var created = await conn.ExecuteAsync("""
            INSERT INTO auth_tokens (token_hash, user_id, purpose, expires_at)
            SELECT @TokenHash, @UserId, @Purpose, now() + make_interval(secs => @Seconds)
            WHERE (SELECT count(*) FROM auth_tokens WHERE user_id = @UserId AND purpose = @Purpose AND created_at > now() - interval '1 hour') < @MaxPerHour
            """, new { TokenHash = tokenHash, UserId = userId, Purpose = purpose, Seconds = lifetime.TotalSeconds, MaxPerHour = maxPerHour });
        return created > 0;
    }

    public async Task<TokenInfoRecord?> PeekAsync(string tokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TokenInfoRecord>("""
            SELECT t.user_id, u.email, t.purpose FROM auth_tokens t JOIN users u ON u.id = t.user_id
            WHERE t.token_hash = @TokenHash AND t.used_at IS NULL AND t.expires_at > now()
            """, new { TokenHash = tokenHash });
    }

    public async Task<long?> ConsumeAsync(string tokenHash, string passwordHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var userId = await conn.ExecuteScalarAsync<long?>("""
            UPDATE auth_tokens SET used_at = now()
            WHERE token_hash = @TokenHash AND used_at IS NULL AND expires_at > now()
            RETURNING user_id
            """, new { TokenHash = tokenHash }, tx);
        if (userId is null) return null;

        await conn.ExecuteAsync("""
            UPDATE users SET password_hash = @Hash WHERE id = @UserId;
            UPDATE auth_tokens SET used_at = now() WHERE user_id = @UserId AND used_at IS NULL;
            DELETE FROM sessions WHERE user_id = @UserId;
            """, new { UserId = userId.Value, Hash = passwordHash }, tx);
        await tx.CommitAsync(ct);
        return userId;
    }

    public async Task<InviteContextRecord?> InviteContextAsync(long projectId, long inviterId, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<InviteContextRecord>("""
            SELECT CASE WHEN i.name <> '' THEN i.name ELSE i.email END AS inviter, o.name AS organization, u.password_hash IS NULL AS pending
            FROM projects p JOIN organizations o ON o.id = p.organization_id
            JOIN organization_members m ON m.organization_id = o.id AND m.user_id = @UserId
            JOIN users u ON u.id = m.user_id
            JOIN users i ON i.id = @InviterId
            WHERE p.id = @ProjectId
            """, new { ProjectId = projectId, InviterId = inviterId, UserId = userId });
    }
}
