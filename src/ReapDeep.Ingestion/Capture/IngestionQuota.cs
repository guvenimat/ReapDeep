using System.Globalization;
using ReapDeep.Ingestion.Infrastructure;
using StackExchange.Redis;

namespace ReapDeep.Ingestion.Capture;

public sealed class IngestionQuota(RedisConnection redis, IConfiguration cfg)
{
    public const int MinimumLimit = 1000;
    const int DefaultLimit = 2000;
    const string Reserve = """
        local used = tonumber(redis.call('GET', KEYS[1]) or '0')
        local n = tonumber(ARGV[1])
        if used + n > tonumber(ARGV[2]) then return 0 end
        redis.call('INCRBY', KEYS[1], n)
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return 1
        """;

    readonly int _defaultLimit = cfg.GetValue("Ingestion:EventsPerSecondPerProject", DefaultLimit);

    public static string WindowKey(long projectId, DateTime utcNow) =>
        string.Create(CultureInfo.InvariantCulture, $"ingest:{projectId}:{utcNow.Ticks / TimeSpan.TicksPerSecond}");

    public static int? EffectiveLimit(int? projectLimit, int defaultLimit) => (projectLimit ?? defaultLimit) switch
    {
        <= 0 => null,
        var limit => Math.Max(limit, MinimumLimit),
    };

    public async Task<bool> TryReserveAsync(long projectId, int? projectLimit, int events)
    {
        if (events == 0 || EffectiveLimit(projectLimit, _defaultLimit) is not { } limit || redis.Database is not { } db) return true;
        try
        {
            var result = await db.ScriptEvaluateAsync(Reserve, [WindowKey(projectId, DateTime.UtcNow)], [events, limit, 2000]);
            return (long)result == 1;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            redis.Warn(ex);
            return true;
        }
    }
}
