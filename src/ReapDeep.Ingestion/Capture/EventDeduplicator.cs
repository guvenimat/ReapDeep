using ReapDeep.Ingestion.Infrastructure;
using StackExchange.Redis;

namespace ReapDeep.Ingestion.Capture;

public sealed class EventDeduplicator(RedisConnection redis, IConfiguration cfg)
{
    readonly TimeSpan _window = TimeSpan.FromSeconds(Math.Max(1, cfg.GetValue("Ingestion:DedupSeconds", 600)));

    public static string Key(long projectId, string eventId) => $"dedup:{projectId}:{eventId}";

    public async Task<bool[]> ClaimAsync(long projectId, IReadOnlyList<string?> eventIds)
    {
        var fresh = new bool[eventIds.Count];
        Array.Fill(fresh, true);
        if (redis.Database is not { } db || eventIds.All(id => id is null)) return fresh;
        try
        {
            var batch = db.CreateBatch();
            var claims = new Task<bool>?[eventIds.Count];
            for (var i = 0; i < eventIds.Count; i++)
                if (eventIds[i] is { } id) claims[i] = batch.StringSetAsync(Key(projectId, id), 1, _window, When.NotExists);
            batch.Execute();
            for (var i = 0; i < claims.Length; i++)
                if (claims[i] is { } claim) fresh[i] = await claim;
            return fresh;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            redis.Warn(ex);
            Array.Fill(fresh, true);
            return fresh;
        }
    }

    public async Task ReleaseAsync(long projectId, IReadOnlyCollection<string> eventIds)
    {
        if (eventIds.Count == 0 || redis.Database is not { } db) return;
        try
        {
            await db.KeyDeleteAsync(eventIds.Select(id => (RedisKey)Key(projectId, id)).ToArray());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            redis.Warn(ex);
        }
    }
}
