using MySqlConnector;
using Npgsql;
using ReapDeep.PersonWorker.Cohorts;
using ReapDeep.PersonWorker.Deletion;
using ReapDeep.PersonWorker.Identity;
using ReapDeep.PersonWorker.Outbox;
using ReapDeep.PersonWorker.Recommendations;

namespace ReapDeep.PersonWorker.Infrastructure;

public static class HostingExtensions
{
    public static HostApplicationBuilder AddPersonWorker(this HostApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var services = builder.Services;

        var pg = new NpgsqlSlimDataSourceBuilder(cfg.GetConnectionString("Postgres"));
        pg.EnableArrays();
        services.AddSingleton(pg.Build());

        services.AddSingleton<IdentityResolver>();
        services.AddHostedService<EventConsumer>();
        services.AddHostedService<OutboxPublisher>();

        var starRocks = cfg.GetConnectionString("StarRocks");
        if (!string.IsNullOrEmpty(starRocks))
        {
            services.AddSingleton(new MySqlDataSource(starRocks));
            services.AddSingleton<StarRocksDeleter>();
            services.AddSingleton<StarRocksStore>();
        }
        services.AddSingleton(DeletionOptions.From(cfg));
        services.AddHostedService(sp => new DeletionJob(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetService<StarRocksDeleter>(),
            sp.GetRequiredService<DeletionOptions>(),
            sp.GetRequiredService<ILogger<DeletionJob>>()));

        services.AddSingleton<CohortRepository>();
        services.AddSingleton(CohortOptions.From(cfg));
        services.AddHostedService(sp => new CohortRefreshJob(
            sp.GetRequiredService<CohortRepository>(),
            sp.GetService<StarRocksStore>(),
            sp.GetRequiredService<CohortOptions>(),
            sp.GetRequiredService<ILogger<CohortRefreshJob>>()));

        services.AddSingleton<RecommendationRepository>();
        services.AddSingleton(RecommendationOptions.From(cfg));
        services.AddHostedService(sp => new RecommendationRefreshJob(
            sp.GetRequiredService<RecommendationRepository>(),
            sp.GetService<StarRocksStore>(),
            sp.GetRequiredService<RecommendationOptions>(),
            sp.GetRequiredService<ILogger<RecommendationRefreshJob>>()));
        return builder;
    }
}
