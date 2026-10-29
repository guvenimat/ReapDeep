using ReapDeep.PersonWorker.Infrastructure;

namespace ReapDeep.PersonWorker.Deletion;

public sealed record DeletionOptions(TimeSpan Interval, TimeSpan MinAge, TimeSpan TombstoneDelay)
{
    public static DeletionOptions From(IConfiguration cfg) => new(
        cfg.Seconds("Deletion:IntervalSeconds", 86_400),
        cfg.Seconds("Deletion:MinAgeSeconds", 600),
        cfg.Seconds("Deletion:TombstoneSeconds", 3_600));
}
