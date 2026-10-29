using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using ReapDeep.Ingestion.Capture;
using ReapDeep.Ingestion.Definitions;
using ReapDeep.Ingestion.Kafka;
using ReapDeep.Ingestion.Projects;

namespace ReapDeep.Ingestion.Infrastructure;

public static class HostingExtensions
{
    const long MaxRequestBodyBytes = 2 * 1024 * 1024;

    public static WebApplicationBuilder AddIngestion(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var services = builder.Services;

        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
        services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().WithMethods("POST")));
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var cidr in TrustedNetworks(cfg)) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        });

        var pg = new NpgsqlSlimDataSourceBuilder(cfg.GetConnectionString("Postgres"));
        pg.EnableArrays();
        services.AddSingleton(pg.Build());
        services.AddSingleton<EventProducer>();
        services.AddSingleton<ProjectCache>();
        services.AddHostedService(sp => sp.GetRequiredService<ProjectCache>());
        services.AddSingleton<DefinitionTracker>();
        services.AddHostedService<DefinitionWriter>();
        return builder;
    }

    public static WebApplication UseIngestion(this WebApplication app)
    {
        if (TrustedNetworks(app.Configuration).Length > 0) app.UseForwardedHeaders();
        app.UseCors();
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<EventProducer>().Flush());
        app.MapCapture();
        return app;
    }

    static string[] TrustedNetworks(IConfiguration cfg) =>
        (cfg["ForwardedHeaders:TrustedNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
