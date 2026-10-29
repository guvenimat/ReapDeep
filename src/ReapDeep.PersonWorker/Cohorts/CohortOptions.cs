using ReapDeep.PersonWorker.Infrastructure;

namespace ReapDeep.PersonWorker.Cohorts;

public sealed record CohortOptions(TimeSpan PollInterval, TimeSpan MaxRefreshInterval, TimeSpan VersionGrace, int Concurrency)
{
    public static CohortOptions From(IConfiguration cfg) => new(
        cfg.Seconds("Cohorts:PollSeconds", 15),
        cfg.Seconds("Cohorts:MaxRefreshSeconds", 86_400),
        cfg.Seconds("Cohorts:VersionGraceSeconds", 60),
        int.TryParse(cfg["Cohorts:Concurrency"], out var c) && c > 0 ? c : 2);
}
