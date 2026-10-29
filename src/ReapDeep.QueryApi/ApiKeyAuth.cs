using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ReapDeep.QueryApi;

public sealed record ProjectContext(long ProjectId, string TimeZoneId, TimeZoneInfo TimeZone, string? Role, long? UserId)
{
    public bool CanManage => Role is "owner" or "admin";

    public static ProjectContext From(HttpContext ctx) => (ProjectContext)ctx.Items[nameof(ProjectContext)]!;

    public static ProjectContext Create(long projectId, string tzId, string? role, long? userId, ILogger log)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out var tz)) return new ProjectContext(projectId, tzId, tz, role, userId);
        log.LogWarning("Project {Project}: unknown time zone {Tz}, falling back to UTC", projectId, tzId);
        return new ProjectContext(projectId, "UTC", TimeZoneInfo.Utc, role, userId);
    }
}

public sealed class ApiKeyAuth(NpgsqlDataSource db, ILogger<ApiKeyAuth> log)
{
    static readonly TimeSpan ValidTtl = TimeSpan.FromSeconds(60);
    static readonly TimeSpan InvalidTtl = TimeSpan.FromSeconds(10);
    const int MaxEntries = 10_000;

    readonly ConcurrentDictionary<string, (ProjectContext? Project, DateTime Expires)> _cache = new();

    public async ValueTask<ProjectContext?> Authenticate(HttpContext ctx)
    {
        var key = ReadKey(ctx.Request);
        if (string.IsNullOrEmpty(key)) return null;

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(hash, out var hit) && hit.Expires > now) return hit.Project;

        var project = await Load(hash, ctx.RequestAborted);
        if (_cache.Count >= MaxEntries) _cache.Clear();
        _cache[hash] = (project, now + (project is null ? InvalidTtl : ValidTtl));
        return project;
    }

    public static bool HasKey(HttpRequest req) =>
        req.Headers.Authorization.Count > 0 || req.Headers.ContainsKey("X-Api-Key");

    static string? ReadKey(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth["Bearer ".Length..].Trim();
        return req.Headers["X-Api-Key"].ToString().Trim();
    }

    async Task<ProjectContext?> Load(string hash, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT k.project_id, p.timezone FROM api_keys k JOIN projects p ON p.id = k.project_id
            WHERE k.key_hash = @h AND k.revoked_at IS NULL
            """);
        cmd.Parameters.AddWithValue("h", hash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ProjectContext.Create(r.GetInt64(0), r.GetString(1), null, null, log) : null;
    }
}
