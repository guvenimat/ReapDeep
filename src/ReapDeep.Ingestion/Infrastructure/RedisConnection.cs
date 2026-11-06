using StackExchange.Redis;

namespace ReapDeep.Ingestion.Infrastructure;

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
            log.LogWarning("ConnectionStrings:Redis is not set; duplicate events are not filtered and the ingestion quota is not enforced");
            return;
        }
        var options = ConfigurationOptions.Parse(connection);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;
        options.SyncTimeout = 500;
        options.AsyncTimeout = 500;
        _redis = ConnectionMultiplexer.Connect(options);
    }

    public IDatabase? Database => _redis?.GetDatabase();

    public void Warn(Exception ex)
    {
        if (DateTime.UtcNow - _lastWarning < TimeSpan.FromSeconds(30)) return;
        _lastWarning = DateTime.UtcNow;
        _log.LogWarning("Redis is unavailable, duplicate events are let through and the ingestion quota is skipped: {Message}", ex.Message);
    }

    public void Dispose() => _redis?.Dispose();
}
