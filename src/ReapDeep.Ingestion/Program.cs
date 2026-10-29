using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using ReapDeep.Ingestion;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().WithMethods("POST")));
var trustedProxies = (builder.Configuration["ForwardedHeaders:TrustedNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var cidr in trustedProxies) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
});

var pg = new NpgsqlSlimDataSourceBuilder(builder.Configuration.GetConnectionString("Postgres"));
pg.EnableArrays();
builder.Services.AddSingleton(pg.Build());
builder.Services.AddSingleton<ProjectCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProjectCache>());
builder.Services.AddSingleton<DefinitionTracker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DefinitionTracker>());

var topic = builder.Configuration["Kafka:Topic"] ?? "events";
builder.Services.AddSingleton(sp => new ProducerBuilder<Null, byte[]>(new ProducerConfig
{
    BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
    Acks = Acks.All,
    EnableIdempotence = true,
    LingerMs = 5,
    CompressionType = CompressionType.Lz4,
    MessageTimeoutMs = 10_000,
})
.SetErrorHandler((_, e) => sp.GetRequiredService<ILogger<Program>>().LogWarning("Kafka: {Reason}", e.Reason))
.Build());

var app = builder.Build();
if (trustedProxies.Length > 0) app.UseForwardedHeaders();
app.UseCors();

app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<IProducer<Null, byte[]>>().Flush(TimeSpan.FromSeconds(10)));

app.MapGet("/healthz", () => "ok");

app.MapPost("/capture", async (HttpContext ctx, ProjectCache projects, DefinitionTracker definitions, IProducer<Null, byte[]> producer, ILogger<Program> log) =>
{
    CaptureRequest? req;
    try
    {
        req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AppJsonContext.Default.CaptureRequest, ctx.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON");
    }
    if (req?.Batch is not { Count: > 0 } batch || string.IsNullOrEmpty(req.ApiKey))
        return Results.BadRequest("api_key and batch are required");
    if (batch.Count > 1000)
        return Results.BadRequest("batch can contain at most 1000 events");

    if (!projects.IsLoaded) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!projects.TryGetProjectId(req.ApiKey, out var projectId)) return Results.Unauthorized();

    var now = DateTime.UtcNow;
    var skew = req.SentAt is { } sentAt ? now - sentAt.UtcDateTime : TimeSpan.Zero;
    var ip = ClientIp(ctx);
    var ua = UserAgentParser.Parse(ctx.Request.Headers.UserAgent);

    var deliveries = new List<Task<DeliveryResult<Null, byte[]>>>(batch.Count);
    var rejected = 0;
    var accepted = new List<(string Event, Dictionary<string, JsonElement>? Properties)>(batch.Count);
    foreach (var e in batch)
    {
        if (string.IsNullOrEmpty(e.Event) || string.IsNullOrEmpty(e.DistinctId) ||
            Encoding.UTF8.GetByteCount(e.Event) > EventMessage.MaxIdBytes ||
            Encoding.UTF8.GetByteCount(e.DistinctId) > EventMessage.MaxIdBytes)
        {
            rejected++;
            continue;
        }

        var ts = e.Timestamp is { } t ? t.UtcDateTime + skew : now;
        if (ts > now.AddHours(1)) ts = now;
        if (ts < now.AddDays(-364)) { rejected++; continue; }

        var eventId = e.EventId is { Length: > 0 and <= 64 } id ? id : Guid.CreateVersion7().ToString();
        var value = EventMessage.Build(projectId, e.Event, e.DistinctId, eventId, ts, now, ip, ua, e.Properties);
        deliveries.Add(producer.ProduceAsync(topic, new Message<Null, byte[]> { Value = value }, ctx.RequestAborted));
        accepted.Add((e.Event, e.Properties));
    }

    try
    {
        await Task.WhenAll(deliveries);
    }
    catch (Exception ex) when (ex is ProduceException<Null, byte[]> or KafkaException)
    {
        log.LogError(ex, "Failed to write to Kafka");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    foreach (var (name, props) in accepted) definitions.Observe(projectId, name, props);
    return Results.Json(new CaptureResponse(deliveries.Count, rejected), AppJsonContext.Default.CaptureResponse, statusCode: StatusCodes.Status202Accepted);
});

app.Run();

static string? ClientIp(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress is { } a ? (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString() : null;
