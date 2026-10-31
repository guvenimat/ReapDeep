using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using StackExchange.Redis;

namespace ReapDeep.QueryApi.Auth;

public static class AuthCacheKeys
{
    public static string Session(string tokenHash) => $"auth:session:{tokenHash}";
    public static string UserSessions(long userId) => $"auth:user:{userId}";
    public static string ApiKey(string keyHash) => $"auth:key:{keyHash}";
    public const string Missing = "-";
}

public sealed class AuthCache : IDisposable
{
    readonly ConnectionMultiplexer? _redis;
    readonly ILogger<AuthCache> _log;
    DateTime _lastWarning = DateTime.MinValue;

    public AuthCache(IConfiguration cfg, ILogger<AuthCache> log)
    {
        _log = log;
        var connection = cfg.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connection))
        {
            log.LogWarning("ConnectionStrings:Redis is not set; sessions and API keys are read from PostgreSQL on every request");
            return;
        }
        var options = ConfigurationOptions.Parse(connection);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;
        options.SyncTimeout = 300;
        options.AsyncTimeout = 300;
        _redis = ConnectionMultiplexer.Connect(options);
    }

    public bool IsEnabled => _redis is not null;

    public async Task<(bool Hit, T? Value)> GetAsync<T>(string key, JsonTypeInfo<T> type) where T : class
    {
        if (_redis is null) return (false, null);
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(key);
            if (value.IsNull) return (false, null);
            var text = value.ToString();
            return (true, text == AuthCacheKeys.Missing ? null : JsonSerializer.Deserialize(text, type));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            Warn(ex);
            return (false, null);
        }
    }

    public async Task SetAsync<T>(string key, T? value, JsonTypeInfo<T> type, TimeSpan ttl, long? userId = null) where T : class
    {
        if (_redis is null) return;
        try
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(key, value is null ? AuthCacheKeys.Missing : JsonSerializer.Serialize(value, type), ttl);
            if (userId is { } id)
            {
                var set = AuthCacheKeys.UserSessions(id);
                await db.SetAddAsync(set, key);
                await db.KeyExpireAsync(set, ttl + TimeSpan.FromMinutes(1));
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Warn(ex);
        }
    }

    public async Task RemoveAsync(params string[] keys)
    {
        if (_redis is null || keys.Length == 0) return;
        try
        {
            await _redis.GetDatabase().KeyDeleteAsync([.. keys.Select(k => (RedisKey)k)]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Warn(ex);
        }
    }

    public async Task RemoveUserAsync(long userId)
    {
        if (_redis is null) return;
        try
        {
            var db = _redis.GetDatabase();
            var set = AuthCacheKeys.UserSessions(userId);
            var members = await db.SetMembersAsync(set);
            await db.KeyDeleteAsync([.. members.Select(m => (RedisKey)m.ToString()), set]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            Warn(ex);
        }
    }

    void Warn(Exception ex)
    {
        if (DateTime.UtcNow - _lastWarning < TimeSpan.FromSeconds(30)) return;
        _lastWarning = DateTime.UtcNow;
        _log.LogWarning("Redis is unavailable, authentication reads PostgreSQL directly: {Message}", ex.Message);
    }

    public void Dispose() => _redis?.Dispose();
}
