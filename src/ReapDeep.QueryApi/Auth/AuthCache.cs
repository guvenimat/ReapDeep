using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ReapDeep.QueryApi.Infrastructure;
using StackExchange.Redis;

namespace ReapDeep.QueryApi.Auth;

public static class AuthCacheKeys
{
    public static string Session(string tokenHash) => $"auth:session:{tokenHash}";
    public static string UserSessions(long userId) => $"auth:user:{userId}";
    public static string ApiKey(string keyHash) => $"auth:key:{keyHash}";
    public const string Missing = "-";
}

public sealed class AuthCache(RedisConnection redis)
{
    public async Task<(bool Hit, T? Value)> GetAsync<T>(string key, JsonTypeInfo<T> type) where T : class
    {
        if (redis.Database is not { } db) return (false, null);
        try
        {
            var value = await db.StringGetAsync(key);
            if (value.IsNull) return (false, null);
            var text = value.ToString();
            return (true, text == AuthCacheKeys.Missing ? null : JsonSerializer.Deserialize(text, type));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            redis.Warn(ex);
            return (false, null);
        }
    }

    public async Task SetAsync<T>(string key, T? value, JsonTypeInfo<T> type, TimeSpan ttl, long? userId = null) where T : class
    {
        if (redis.Database is not { } db) return;
        try
        {
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
            redis.Warn(ex);
        }
    }

    public async Task RemoveAsync(params string[] keys)
    {
        if (redis.Database is not { } db || keys.Length == 0) return;
        try
        {
            await db.KeyDeleteAsync([.. keys.Select(k => (RedisKey)k)]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            redis.Warn(ex);
        }
    }

    public async Task RemoveUserAsync(long userId)
    {
        if (redis.Database is not { } db) return;
        try
        {
            var set = AuthCacheKeys.UserSessions(userId);
            var members = await db.SetMembersAsync(set);
            await db.KeyDeleteAsync([.. members.Select(m => (RedisKey)m.ToString()), set]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            redis.Warn(ex);
        }
    }
}
