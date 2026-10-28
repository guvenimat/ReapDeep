using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ReapDeep.QueryApi;

public sealed record ProjectContext(long ProjectId, string TimeZoneId, TimeZoneInfo TimeZone)
{
    public static ProjectContext From(HttpContext ctx) => (ProjectContext)ctx.Items[nameof(ProjectContext)]!;
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
        if (!await r.ReadAsync(ct)) return null;

        var tzId = r.GetString(1);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out var tz))
        {
            log.LogWarning("Project {Project}: unknown time zone {Tz}, falling back to UTC", r.GetInt64(0), tzId);
            (tzId, tz) = ("UTC", TimeZoneInfo.Utc);
        }
        return new ProjectContext(r.GetInt64(0), tzId, tz);
    }
}
