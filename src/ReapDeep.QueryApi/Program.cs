using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using MySqlConnector;
using Npgsql;
using ReapDeep.QueryApi;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

var corsOrigins = (builder.Configuration["QueryApi:CorsOrigins"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (corsOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var trustedProxies = (builder.Configuration["ForwardedHeaders:TrustedNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var cidr in trustedProxies) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(Endpoints.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var pg = new NpgsqlSlimDataSourceBuilder(builder.Configuration.GetConnectionString("Postgres"));
pg.EnableArrays();
var pgSource = pg.Build();
builder.Services.AddSingleton(pgSource);

builder.Services.AddSingleton(new MySqlDataSource(builder.Configuration.GetConnectionString("StarRocks")!));

builder.Services.AddSingleton<ApiKeyAuth>();
builder.Services.AddSingleton<SessionAuth>();

var app = builder.Build();
if (trustedProxies.Length > 0) app.UseForwardedHeaders();
if (corsOrigins.Length > 0) app.UseCors();
app.UseRateLimiter();
app.MapQueryApi();

for (var attempt = 1; ; attempt++)
{
    try
    {
        await SessionAuth.EnsureBootstrapUser(pgSource, app.Configuration, app.Logger);
        break;
    }
    catch (NpgsqlException ex) when (attempt < 30)
    {
        app.Logger.LogWarning("Bootstrap user check failed ({Message}), retrying", ex.Message);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}

app.Run();
