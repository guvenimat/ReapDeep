using StackExchange.Redis;

namespace ReapDeep.Ingestion.Capture;

public sealed class EventDeduplicator : IDisposable
{
    readonly ConnectionMultiplexer? _redis;
    readonly TimeSpan _window;
    readonly ILogger<EventDeduplicator> _log;
    DateTime _lastWarning = DateTime.MinValue;

    public EventDeduplicator(IConfiguration cfg, ILogger<EventDeduplicator> log)
    {
        _log = log;
        _window = TimeSpan.FromSeconds(Math.Max(1, cfg.GetValue("Ingestion:DedupSeconds", 600)));
        var connection = cfg.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connection))
        {
            log.LogWarning("ConnectionStrings:Redis is not set; duplicate events are not filtered at ingestion");
            return;
        }
        var options = ConfigurationOptions.Parse(connection);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;
        options.SyncTimeout = 500;
        options.AsyncTimeout = 500;
        _redis = ConnectionMultiplexer.Connect(options);
    }

    public static string Key(long projectId, string eventId) => $"dedup:{projectId}:{eventId}";

    public async Task<bool[]> ClaimAsync(long projectId, IReadOnlyList<string?> eventIds)
    {
        var fresh = new bool[eventIds.Count];
        Array.Fill(fresh, true);
        if (_redis is null || eventIds.All(id => id is null)) return fresh;
        try
        {
            var db = _redis.GetDatabase();
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
            Warn(ex);
            Array.Fill(fresh, true);
            return fresh;
        }
    }

    public async Task ReleaseAsync(long projectId, IEnumerable<string> eventIds)
    {
        if (_redis is null) return;
        try
        {
            var keys = eventIds.Select(id => (RedisKey)Key(projectId, id)).ToArray();
            if (keys.Length > 0) await _redis.GetDatabase().KeyDeleteAsync(keys);
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
        _log.LogWarning("Redis is unavailable, duplicate events are let through: {Message}", ex.Message);
    }

    public void Dispose() => _redis?.Dispose();
}
