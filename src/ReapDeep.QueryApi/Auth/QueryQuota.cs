using System.Collections.Concurrent;
using System.Globalization;
using ReapDeep.QueryApi.Infrastructure;
using StackExchange.Redis;

namespace ReapDeep.QueryApi.Auth;

public sealed record QuotaDecision(bool Allowed, string? Scope, int RetryAfterSeconds);

public static class QuotaRules
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static string WindowKey(string scope, string id, DateTime now) =>
        $"quota:{scope}:{id}:{now.Ticks / Window.Ticks}";

    public static int RetryAfter(DateTime now) => (int)Math.Ceiling((Window.Ticks - now.Ticks % Window.Ticks) / (double)TimeSpan.TicksPerSecond);

    public static string Principal(ProjectContext project, string? keyHash) =>
        project.UserId is { } user ? $"user-{user}" : $"key-{keyHash ?? "unknown"}";

    public static QuotaDecision Decide(long principalCount, long projectCount, int principalLimit, int projectLimit, DateTime now) =>
        principalCount > principalLimit ? new(false, "principal", RetryAfter(now))
        : projectCount > projectLimit ? new(false, "project", RetryAfter(now))
        : new(true, null, 0);
}

public sealed class QueryQuota(RedisConnection redis, IConfiguration cfg)
{
    const string Increment = "local c = redis.call('INCR', KEYS[1]) if c == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end return c";

    readonly int _principalLimit = cfg.GetValue("QueryApi:RequestsPerMinutePerPrincipal", 300);
    readonly int _projectLimit = cfg.GetValue("QueryApi:RequestsPerMinutePerProject", 1200);
    readonly ConcurrentDictionary<string, long> _local = new();

    public async Task<QuotaDecision> CheckAsync(ProjectContext project, string principal)
    {
        var now = DateTime.UtcNow;
        var principalKey = QuotaRules.WindowKey("principal", $"{project.ProjectId}:{principal}", now);
        var projectKey = QuotaRules.WindowKey("project", project.ProjectId.ToString(CultureInfo.InvariantCulture), now);
        var (a, b) = await CountAsync(principalKey, projectKey, now);
        return QuotaRules.Decide(a, b, _principalLimit, _projectLimit, now);
    }

    async Task<(long, long)> CountAsync(string principalKey, string projectKey, DateTime now)
    {
        if (redis.Database is { } db)
        {
            try
            {
                var ttl = (long)(QuotaRules.Window.TotalMilliseconds * 2);
                var a = (long)await db.ScriptEvaluateAsync(Increment, [principalKey], [ttl]);
                var b = (long)await db.ScriptEvaluateAsync(Increment, [projectKey], [ttl]);
                return (a, b);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                redis.Warn(ex);
            }
        }
        if (_local.Count > 50_000) _local.Clear();
        return (_local.AddOrUpdate(principalKey, 1, (_, v) => v + 1), _local.AddOrUpdate(projectKey, 1, (_, v) => v + 1));
    }
}

public sealed class QueryQuotaMiddleware(RequestDelegate next, QueryQuota quota)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ProjectContext.Get(ctx) is not { } project)
        {
            await next(ctx);
            return;
        }
        var decision = await quota.CheckAsync(project, QuotaRules.Principal(project, project.UserId is null ? ApiKeyAuthenticator.KeyHashOf(ctx.Request) : null));
        if (!decision.Allowed)
        {
            ctx.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await ApiResults.Error(StatusCodes.Status429TooManyRequests, decision.Scope == "project"
                ? "This project is over its request quota for this minute"
                : "Too many requests; slow down and retry after the time in Retry-After").ExecuteAsync(ctx);
            return;
        }
        await next(ctx);
    }
}
