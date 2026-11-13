using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Infrastructure;
using StackExchange.Redis;

namespace ReapDeep.QueryApi.Analytics;

public static class AnalyticsCacheKeys
{
    public const string Header = "X-Cache";
    public const string Hit = "HIT";
    public const string Miss = "MISS";

    public static bool BypassRead(string? cacheControl) =>
        cacheControl is { Length: > 0 } value && (value.Contains("no-cache", StringComparison.OrdinalIgnoreCase) || value.Contains("no-store", StringComparison.OrdinalIgnoreCase));

    public static string Build(long projectId, string endpoint, string timeZoneId, string body, IEnumerable<CohortRef> cohorts)
    {
        var text = new StringBuilder()
            .Append(timeZoneId).Append('\n')
            .AppendJoin(',', cohorts.OrderBy(c => c.Id).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Id}:{c.Version}")))
            .Append('\n')
            .Append(body)
            .ToString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return string.Create(CultureInfo.InvariantCulture, $"query:{projectId}:{endpoint}:{hash}");
    }
}

public sealed class AnalyticsCache(RedisConnection redis, CohortService cohorts, IConfiguration cfg)
{
    readonly TimeSpan _ttl = TimeSpan.FromSeconds(cfg.GetValue("QueryApi:ResultCacheSeconds", 45));

    public bool Enabled => _ttl > TimeSpan.Zero && redis.Database is not null;

    public async Task<TResponse> GetOrRunAsync<TRequest, TResponse>(
        HttpContext http, ProjectContext project, string endpoint, TRequest req, JsonTypeInfo<TRequest> requestType,
        IEnumerable<Filter>? filters, Breakdown? breakdown, JsonTypeInfo<TResponse> responseType,
        Func<Task<TResponse>> run, Func<TResponse, double, TResponse> withTook, CancellationToken ct) where TResponse : class
    {
        if (!Enabled || redis.Database is not { } db) return await run();

        var sw = Stopwatch.StartNew();
        var refs = await cohorts.ResolveAsync(project, filters, breakdown, ct);
        var key = AnalyticsCacheKeys.Build(project.ProjectId, endpoint, project.TimeZoneId,
            JsonSerializer.Serialize(req, requestType), refs.Values);

        try
        {
            var cached = AnalyticsCacheKeys.BypassRead(http.Request.Headers.CacheControl.ToString()) ? RedisValue.Null : await db.StringGetAsync(key);
            if (!cached.IsNull &&JsonSerializer.Deserialize(cached.ToString(), responseType) is { } hit)
            {
                http.Response.Headers[AnalyticsCacheKeys.Header] = AnalyticsCacheKeys.Hit;
                return withTook(hit, sw.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            redis.Warn(ex);
        }

        var result = await run();
        http.Response.Headers[AnalyticsCacheKeys.Header] = AnalyticsCacheKeys.Miss;
        try
        {
            await db.StringSetAsync(key, JsonSerializer.Serialize(result, responseType), _ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            redis.Warn(ex);
        }
        return result;
    }
}
