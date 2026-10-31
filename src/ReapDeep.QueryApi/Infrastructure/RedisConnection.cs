using StackExchange.Redis;

namespace ReapDeep.QueryApi.Infrastructure;

public sealed class RedisConnection : IDisposable
{
    readonly ConnectionMultiplexer? _redis;
    readonly ILogger<RedisConnection> _log;
    DateTime _lastWarning = DateTime.MinValue;

    public RedisConnection(IConfiguration cfg, ILogger<RedisConnection> log)
    {
        _log = log;
        var connection = cfg.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connection))
        {
            log.LogWarning("ConnectionStrings:Redis is not set; sessions, API keys and quotas use PostgreSQL and per-instance state");
            return;
        }
        var options = ConfigurationOptions.Parse(connection);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;
        options.SyncTimeout = 300;
        options.AsyncTimeout = 300;
        _redis = ConnectionMultiplexer.Connect(options);
    }

    public IDatabase? Database => _redis?.GetDatabase();

    public void Warn(Exception ex)
    {
        if (DateTime.UtcNow - _lastWarning < TimeSpan.FromSeconds(30)) return;
        _lastWarning = DateTime.UtcNow;
        _log.LogWarning("Redis is unavailable, falling back: {Message}", ex.Message);
    }

    public void Dispose() => _redis?.Dispose();
}
