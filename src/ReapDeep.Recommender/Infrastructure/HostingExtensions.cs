using System.Text.Json;
using Npgsql;
using ReapDeep.Recommender.Projects;
using ReapDeep.Recommender.Recommendations;

namespace ReapDeep.Recommender.Infrastructure;

public static class HostingExtensions
{
    public static WebApplicationBuilder AddRecommender(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        });
        services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().WithMethods("GET")));

        var pg = new NpgsqlSlimDataSourceBuilder(builder.Configuration.GetConnectionString("Postgres"));
        pg.EnableArrays();
        services.AddSingleton(pg.Build());
        services.AddSingleton<ProjectCache>();
        services.AddHostedService(sp => sp.GetRequiredService<ProjectCache>());
        services.AddSingleton<RecommendationCache>();
        services.AddHostedService(sp => sp.GetRequiredService<RecommendationCache>());
        return builder;
    }

    public static WebApplication UseRecommender(this WebApplication app)
    {
        app.UseCors();
        app.MapRecommend();
        return app;
    }
}
