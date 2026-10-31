using System.Security.Cryptography;
using System.Text;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Auth;

public sealed class SessionService(AccountRepository accounts, AuthCache cache, IConfiguration cfg, ILogger<SessionService> log)
{
    public const string CookieName = "rd_session";
    static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    const int MaxTokenLength = 128;

    readonly bool _secureCookie = cfg["Auth:SecureCookie"] != "false";

    public async ValueTask<MeResponse?> GetCurrentAsync(HttpContext ctx)
    {
        if (TokenHash(ctx.Request) is not { } hash) return null;
        var key = AuthCacheKeys.Session(hash);
        var (hit, cached) = await cache.GetAsync(key, AppJsonContext.Default.MeResponse);
        if (hit) return cached;

        var me = await accounts.FindSessionAsync(hash, ctx.RequestAborted);
        await cache.SetAsync(key, me, AppJsonContext.Default.MeResponse, CacheTtl, me?.User.Id);
        return me;
    }

    public ProjectContext? ResolveProject(MeResponse me, long projectId) =>
        me.Projects.FirstOrDefault(p => p.Id == projectId) is { } p
            ? ProjectContext.Create(p.Id, p.Timezone, p.Role, me.User.Id, log)
            : null;

    public async Task<MeResponse?> SignInAsync(HttpContext ctx, string email, string password)
    {
        var ct = ctx.RequestAborted;
        var credentials = await accounts.FindCredentialsAsync(NormalizeEmail(email), ct);
        if (!PasswordHasher.Verify(password, credentials?.PasswordHash) || credentials is null) return null;

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(token);
        var expires = DateTime.UtcNow + Lifetime;
        await accounts.CreateSessionAsync(credentials.Id, hash, expires, ct);
        ctx.Response.Cookies.Append(CookieName, token, CookieOptions(expires));
        return await accounts.FindSessionAsync(hash, ct);
    }

    public async Task SignOutAsync(HttpContext ctx)
    {
        if (TokenHash(ctx.Request) is { } hash)
        {
            await accounts.DeleteSessionAsync(hash, ctx.RequestAborted);
            await cache.RemoveAsync(AuthCacheKeys.Session(hash));
        }
        ctx.Response.Cookies.Delete(CookieName, CookieOptions(null));
    }

    public async Task<bool> ChangePasswordAsync(HttpContext ctx, long userId, string currentPassword, string newPassword)
    {
        var changed = await accounts.ChangePasswordAsync(userId, stored => PasswordHasher.Verify(currentPassword, stored),
            PasswordHasher.Hash(newPassword), TokenHash(ctx.Request) ?? "", ctx.RequestAborted);
        if (changed) await InvalidateUserAsync(userId);
        return changed;
    }

    public Task InvalidateUserAsync(long userId) => cache.RemoveUserAsync(userId);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    CookieOptions CookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = _secureCookie,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
    };

    static string? TokenHash(HttpRequest req) =>
        req.Cookies.TryGetValue(CookieName, out var token) && token is { Length: > 0 and <= MaxTokenLength } ? Hash(token) : null;

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
