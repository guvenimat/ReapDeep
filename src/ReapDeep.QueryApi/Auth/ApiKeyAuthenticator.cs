using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ReapDeep.QueryApi.Organization;

namespace ReapDeep.QueryApi.Auth;

public sealed class ApiKeyAuthenticator(ApiKeyRepository keys, ILogger<ApiKeyAuthenticator> log)
{
    static readonly TimeSpan ValidTtl = TimeSpan.FromSeconds(60);
    static readonly TimeSpan InvalidTtl = TimeSpan.FromSeconds(10);
    const int MaxCacheEntries = 10_000;

    readonly ConcurrentDictionary<string, (ProjectContext? Project, DateTime Expires)> _cache = new();

    public static bool HasKey(HttpRequest req) => req.Headers.Authorization.Count > 0 || req.Headers.ContainsKey("X-Api-Key");

    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async ValueTask<ProjectContext?> AuthenticateAsync(HttpContext ctx)
    {
        var key = ReadKey(ctx.Request);
        if (string.IsNullOrEmpty(key)) return null;

        var hash = HashKey(key);
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(hash, out var hit) && hit.Expires > now) return hit.Project;

        var found = await keys.FindActiveAsync(hash, ctx.RequestAborted);
        var project = found is null ? null : ProjectContext.Create(found.ProjectId, found.Timezone, role: null, userId: null, log, found.Scope);
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[hash] = (project, now + (project is null ? InvalidTtl : ValidTtl));
        return project;
    }

    public void Invalidate(string keyHash) => _cache.TryRemove(keyHash, out _);

    static string? ReadKey(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : req.Headers["X-Api-Key"].ToString().Trim();
    }
}
