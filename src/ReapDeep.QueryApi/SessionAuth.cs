using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ReapDeep.QueryApi;

public sealed class SessionAuth(NpgsqlDataSource db, IConfiguration cfg, ILogger<SessionAuth> log)
{
    public const string CookieName = "rd_session";
    static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    const int MaxEntries = 10_000;

    readonly ConcurrentDictionary<string, (MeResponse? Me, DateTime Expires)> _cache = new();
    readonly bool _secureCookie = cfg["Auth:SecureCookie"] != "false";

    public async ValueTask<MeResponse?> Current(HttpContext ctx)
    {
        if (TokenHash(ctx.Request) is not { } hash) return null;
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(hash, out var hit) && hit.Expires > now) return hit.Me;

        var me = await Load(hash, ctx.RequestAborted);
        if (_cache.Count >= MaxEntries) _cache.Clear();
        _cache[hash] = (me, now + CacheTtl);
        return me;
    }

    public ProjectContext? Project(MeResponse me, long projectId) =>
        me.Projects.FirstOrDefault(p => p.Id == projectId) is { } p ? ProjectContext.Create(p.Id, p.Timezone, p.Role, log) : null;

    public async Task<MeResponse?> Login(HttpContext ctx, string email, string password)
    {
        var ct = ctx.RequestAborted;
        long? userId = null;
        string? stored = null;
        await using (var cmd = db.CreateCommand("SELECT id, password_hash FROM users WHERE email = @e"))
        {
            cmd.Parameters.AddWithValue("e", NormalizeEmail(email));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) (userId, stored) = (r.GetInt64(0), r.GetString(1));
        }
        if (!Passwords.Verify(password, stored) || userId is not { } uid) return null;

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(token);
        var expires = DateTime.UtcNow + Lifetime;
        await using (var cmd = db.CreateCommand("""
            DELETE FROM sessions WHERE user_id = @u AND expires_at < now();
            INSERT INTO sessions (token_hash, user_id, expires_at) VALUES (@h, @u, @x);
            """))
        {
            cmd.Parameters.AddWithValue("u", uid);
            cmd.Parameters.AddWithValue("h", hash);
            cmd.Parameters.AddWithValue("x", expires);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = _secureCookie,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
        });
        return await Load(hash, ct);
    }

    public async Task Logout(HttpContext ctx)
    {
        if (TokenHash(ctx.Request) is { } hash)
        {
            _cache.TryRemove(hash, out _);
            await using var cmd = db.CreateCommand("DELETE FROM sessions WHERE token_hash = @h");
            cmd.Parameters.AddWithValue("h", hash);
            await cmd.ExecuteNonQueryAsync(ctx.RequestAborted);
        }
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = _secureCookie, SameSite = SameSiteMode.Strict });
    }

    public static async Task EnsureBootstrapUser(NpgsqlDataSource db, IConfiguration cfg, ILogger log)
    {
        var email = cfg["Auth:BootstrapEmail"];
        var password = cfg["Auth:BootstrapPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return;

        await using var cmd = db.CreateCommand("""
            WITH u AS (
                INSERT INTO users (email, name, password_hash)
                SELECT @e, @n, @h WHERE NOT EXISTS (SELECT 1 FROM users)
                RETURNING id
            )
            INSERT INTO organization_members (organization_id, user_id, role)
            SELECT o.id, u.id, 'owner' FROM organizations o CROSS JOIN u
            RETURNING user_id
            """);
        cmd.Parameters.AddWithValue("e", NormalizeEmail(email));
        cmd.Parameters.AddWithValue("n", email.Split('@')[0]);
        cmd.Parameters.AddWithValue("h", Passwords.Hash(password));
        if (await cmd.ExecuteScalarAsync() is not null)
            log.LogInformation("Created bootstrap user {Email} as owner of all organizations", NormalizeEmail(email));
    }

    async Task<MeResponse?> Load(string hash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        UserInfo? user = null;
        await using (var cmd = new NpgsqlCommand("""
            SELECT u.id, u.email, u.name FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = @h AND s.expires_at > now()
            """, conn))
        {
            cmd.Parameters.AddWithValue("h", hash);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) user = new UserInfo(r.GetInt64(0), r.GetString(1), r.GetString(2));
        }
        if (user is null) return null;

        var projects = new List<ProjectInfo>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT p.id, p.name, o.id, o.name, m.role, p.timezone
            FROM organization_members m
            JOIN organizations o ON o.id = m.organization_id
            JOIN projects p ON p.organization_id = o.id
            WHERE m.user_id = @u ORDER BY o.name, p.name, p.id
            """, conn))
        {
            cmd.Parameters.AddWithValue("u", user.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                projects.Add(new ProjectInfo(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        }
        return new MeResponse(user, projects);
    }

    static string? TokenHash(HttpRequest req) =>
        req.Cookies.TryGetValue(CookieName, out var token) && token is { Length: > 0 and <= 128 } ? Hash(token) : null;

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
