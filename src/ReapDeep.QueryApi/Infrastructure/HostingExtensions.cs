using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using MySqlConnector;
using Npgsql;
using ReapDeep.QueryApi.Alerts;
using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Catalog;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Deletion;
using ReapDeep.QueryApi.Insights;
using ReapDeep.QueryApi.Metadata;
using ReapDeep.QueryApi.Organization;
using ReapDeep.QueryApi.Persons;

namespace ReapDeep.QueryApi.Infrastructure;

public static class HostingExtensions
{
    const int LoginAttemptsPerMinute = 10;

    public static WebApplicationBuilder AddQueryApi(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var services = builder.Services;

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();

        var corsOrigins = List(cfg["QueryApi:CorsOrigins"]);
        if (corsOrigins.Length > 0)
            services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var cidr in List(cfg["ForwardedHeaders:TrustedNetworks"])) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthEndpoints.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = LoginAttemptsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        var pg = new NpgsqlSlimDataSourceBuilder(cfg.GetConnectionString("Postgres"));
        pg.EnableArrays();
        services.AddSingleton(pg.Build());
        services.AddSingleton(new MySqlDataSource(cfg.GetConnectionString("StarRocks")!));
        services.AddSingleton<StarRocksClient>();

        services.AddSingleton<AccountRepository>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<ApiKeyAuthenticator>();
        services.AddHostedService<BootstrapOwner>();

        services.AddSingleton<TrendService>();
        services.AddSingleton<FunnelService>();
        services.AddSingleton<RetentionService>();
        services.AddSingleton<PathService>();
        services.AddSingleton<RfmService>();
        services.AddSingleton<LifecycleService>();
        services.AddSingleton<EventService>();

        services.AddSingleton<PersonRepository>();
        services.AddSingleton<MetadataRepository>();
        services.AddSingleton<InsightRepository>();
        services.AddSingleton<DashboardRepository>();
        services.AddSingleton<MemberRepository>();
        services.AddSingleton<ApiKeyRepository>();
        services.AddSingleton<DeletionRequestRepository>();
        services.AddSingleton<CohortRepository>();
        services.AddSingleton<CohortService>();
        services.AddSingleton<CatalogRepository>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<AlertRepository>();
        services.AddSingleton<WebhookSender>();
        services.AddSingleton<AlertService>();
        services.AddHostedService<AlertJob>();
        return builder;
    }

    public static WebApplication UseQueryApi(this WebApplication app)
    {
        if (List(app.Configuration["ForwardedHeaders:TrustedNetworks"]).Length > 0) app.UseForwardedHeaders();
        app.UseExceptionHandler();
        if (List(app.Configuration["QueryApi:CorsOrigins"]).Length > 0) app.UseCors();
        app.UseRateLimiter();
        app.UseMiddleware<ProjectAccessMiddleware>();
        app.MapQueryApi();
        return app;
    }

    static string[] List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
