using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using ReapDeep.Recommender.Flags;
using ReapDeep.Recommender.Projects;
using ReapDeep.Recommender.Recommendations;
using ReapDeep.ServiceDefaults;

namespace ReapDeep.Recommender.Infrastructure;

public static class HostingExtensions
{
    public const string PublicPolicy = "public";

    public static WebApplicationBuilder AddRecommender(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var services = builder.Services;
        builder.AddReapDeepTelemetry("reapdeep-recommender");
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        });
        services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().WithMethods("GET")));
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var cidr in TrustedNetworks(cfg)) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        });

        var perMinute = int.TryParse(cfg["Recommender:RequestsPerMinutePerIp"], out var n) ? n : 6_000;
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
            o.AddPolicy(PublicPolicy, context => perMinute <= 0
                ? RateLimitPartition.GetNoLimiter("off")
                : RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        var pg = new NpgsqlSlimDataSourceBuilder(builder.Configuration.GetConnectionString("Postgres"));
        pg.EnableArrays();
        services.AddSingleton(pg.Build());
        services.AddSingleton<ProjectCache>();
        services.AddHostedService(sp => sp.GetRequiredService<ProjectCache>());
        services.AddSingleton<PersonHistoryStore>();
        services.AddSingleton<RecommendationCache>();
        services.AddHostedService(sp => sp.GetRequiredService<RecommendationCache>());
        services.AddSingleton<FlagCache>();
        services.AddHostedService(sp => sp.GetRequiredService<FlagCache>());
        return builder;
    }

    public static WebApplication UseRecommender(this WebApplication app)
    {
        if (TrustedNetworks(app.Configuration).Length > 0) app.UseForwardedHeaders();
        app.UseCors();
        app.UseRateLimiter();
        app.MapRecommend();
        app.MapFlags();
        return app;
    }

    static string[] TrustedNetworks(IConfiguration cfg) =>
        (cfg["ForwardedHeaders:TrustedNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
