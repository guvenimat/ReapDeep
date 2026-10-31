using System.Security.Cryptography;
using System.Text;
using ReapDeep.QueryApi.Infrastructure;
using ReapDeep.QueryApi.Organization;

namespace ReapDeep.QueryApi.Auth;

public sealed class ApiKeyAuthenticator(ApiKeyRepository keys, AuthCache cache, ILogger<ApiKeyAuthenticator> log)
{
    static readonly TimeSpan ValidTtl = TimeSpan.FromSeconds(60);
    static readonly TimeSpan InvalidTtl = TimeSpan.FromSeconds(10);
    public static bool HasKey(HttpRequest req) => req.Headers.Authorization.Count > 0 || req.Headers.ContainsKey("X-Api-Key");

    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async ValueTask<ProjectContext?> AuthenticateAsync(HttpContext ctx)
    {
        var key = ReadKey(ctx.Request);
        if (string.IsNullOrEmpty(key)) return null;

        var hash = HashKey(key);
        var cacheKey = AuthCacheKeys.ApiKey(hash);
        var (hit, found) = await cache.GetAsync(cacheKey, AppJsonContext.Default.ActiveKey);
        if (!hit)
        {
            found = await keys.FindActiveAsync(hash, ctx.RequestAborted);
            await cache.SetAsync(cacheKey, found, AppJsonContext.Default.ActiveKey, found is null ? InvalidTtl : ValidTtl);
        }
        return found is null ? null : ProjectContext.Create(found.ProjectId, found.Timezone, role: null, userId: null, log, found.Scope);
    }

    public Task InvalidateAsync(string keyHash) => cache.RemoveAsync(AuthCacheKeys.ApiKey(keyHash));

    public static string? KeyHashOf(HttpRequest req) => ReadKey(req) is { Length: > 0 } key ? HashKey(key) : null;

    static string? ReadKey(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : req.Headers["X-Api-Key"].ToString().Trim();
    }
}
