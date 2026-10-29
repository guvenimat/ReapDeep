namespace ReapDeep.PersonWorker.Deletion;

public sealed record DeletionOptions(TimeSpan Interval, TimeSpan MinAge, TimeSpan TombstoneDelay)
{
    public static DeletionOptions From(IConfiguration cfg) => new(
        Seconds(cfg, "Deletion:IntervalSeconds", 86_400),
        Seconds(cfg, "Deletion:MinAgeSeconds", 600),
        Seconds(cfg, "Deletion:TombstoneSeconds", 3_600));

    static TimeSpan Seconds(IConfiguration cfg, string key, int fallback) =>
        TimeSpan.FromSeconds(int.TryParse(cfg[key], out var s) && s > 0 ? s : fallback);
}
