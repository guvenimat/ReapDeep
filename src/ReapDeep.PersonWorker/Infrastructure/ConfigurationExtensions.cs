namespace ReapDeep.PersonWorker.Infrastructure;

public static class ConfigurationExtensions
{
    public static TimeSpan Seconds(this IConfiguration cfg, string key, int fallback) =>
        TimeSpan.FromSeconds(int.TryParse(cfg[key], out var s) && s > 0 ? s : fallback);
}
