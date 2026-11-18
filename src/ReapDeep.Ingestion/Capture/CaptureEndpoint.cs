using System.Net;
using Microsoft.AspNetCore.Http.Features;
using System.Text;
using System.Text.Json;
using ReapDeep.Ingestion.Definitions;
using ReapDeep.Ingestion.Infrastructure;
using ReapDeep.Ingestion.Kafka;
using ReapDeep.Ingestion.Projects;

namespace ReapDeep.Ingestion.Capture;

public static class CaptureEndpoint
{
    const int MaxBatchSize = 1000;
    public const int MaxServerBatchSize = 5000;
    const long MaxServerBodyBytes = 10 * 1024 * 1024;
    public const int MaxEventIdBytes = 64;
    public const string IpProperty = "$ip";
    const string BearerPrefix = "Bearer ";

    public static void MapCapture(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", (ProjectCache projects) => projects.IsLoaded
            ? Results.Text("ok")
            : Results.Text("project keys are not loaded", statusCode: StatusCodes.Status503ServiceUnavailable));
        app.MapPost("/capture", Capture);
        app.MapPost("/batch", Batch);
    }

    static async Task<IResult> Capture(HttpContext ctx, ProjectCache projects, DefinitionTracker definitions, EventProducer producer, EventDeduplicator dedup,
        IngestionQuota quota, ILoggerFactory loggers)
    {
        if (await ReadAsync(ctx) is not { } req) return Results.BadRequest("Invalid JSON");
        if (req.Batch is not { Count: > 0 } batch || string.IsNullOrEmpty(req.ApiKey)) return Results.BadRequest("api_key and batch are required");
        if (batch.Count > MaxBatchSize) return Results.BadRequest($"batch can contain at most {MaxBatchSize} events");

        if (!projects.IsLoaded) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!projects.TryGetProject(req.ApiKey, out var project)) return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var timestamps = EventTimestamps.For(now, req.SentAt);
        var ip = IpAnonymizer.Format(ctx.Connection.RemoteIpAddress, project.AnonymizeIp);
        var ua = UserAgentParser.Parse(ctx.Request.Headers.UserAgent);

        var valid = new List<Accepted>(batch.Count);
        foreach (var e in batch)
        {
            if (!IsValid(e) || timestamps.Resolve(e.Timestamp) is not { } ts) continue;
            valid.Add(new Accepted(e, ts, ClientEventId(e.EventId), ip));
        }
        return await IngestAsync(ctx, project, valid, batch.Count, now, ua, definitions, producer, dedup, quota, loggers);
    }

    static async Task<IResult> Batch(HttpContext ctx, ProjectCache projects, DefinitionTracker definitions, EventProducer producer, EventDeduplicator dedup,
        IngestionQuota quota, ILoggerFactory loggers)
    {
        if (!projects.IsLoaded) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (ctx.Request.Headers.Origin.Count > 0)
            return Results.Text("Server keys must stay on the server; this request came from a browser", statusCode: StatusCodes.Status403Forbidden);
        if (ServerKey(ctx.Request) is not { } key || !projects.TryGetProjectBySecret(key, out var project))
            return Results.Text("A server key is required: Authorization: Bearer sk_...", statusCode: StatusCodes.Status401Unauthorized);
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxServerBodyBytes;
        if (await ReadAsync(ctx) is not { } req) return Results.BadRequest("Invalid JSON");
        if (req.Batch is not { Count: > 0 } batch) return Results.BadRequest("batch is required");
        if (batch.Count > MaxServerBatchSize) return Results.BadRequest($"batch can contain at most {MaxServerBatchSize} events");

        var now = DateTime.UtcNow;
        var valid = new List<Accepted>(batch.Count);
        foreach (var e in batch)
        {
            if (!IsValid(e) || ServerTimestamp(e.Timestamp, now) is not { } ts) continue;
            valid.Add(new Accepted(e, ts, ClientEventId(e.EventId), EventIp(e.Properties, project.AnonymizeIp)));
        }
        return await IngestAsync(ctx, project, valid, batch.Count, now, default, definitions, producer, dedup, quota, loggers);
    }

    readonly record struct Accepted(CaptureEvent Event, DateTime Timestamp, string? ClientId, string? Ip);

    static async Task<IResult> IngestAsync(HttpContext ctx, ProjectSettings project, List<Accepted> valid, int received, DateTime now,
        UserAgentParser.Result ua, DefinitionTracker definitions, EventProducer producer, EventDeduplicator dedup, IngestionQuota quota, ILoggerFactory loggers)
    {
        var projectId = project.Id;
        if (!await quota.TryReserveAsync(projectId, project.EventsPerSecond, valid.Count))
        {
            IngestionMetrics.Throttled.Add(valid.Count, new KeyValuePair<string, object?>("project_id", projectId));
            ctx.Response.Headers.RetryAfter = "1";
            return Results.Text("This project is over its ingestion quota for this second", statusCode: StatusCodes.Status429TooManyRequests);
        }

        var fresh = await dedup.ClaimAsync(projectId, valid.Select(v => v.ClientId).ToList());

        var deliveries = new List<(Task Delivery, string? ClientId)>(valid.Count);
        var accepted = new List<CaptureEvent>(valid.Count);
        var duplicates = 0;
        for (var i = 0; i < valid.Count; i++)
        {
            var (e, ts, clientId, ip) = valid[i];
            if (!fresh[i])
            {
                duplicates++;
                continue;
            }
            var value = EventMessage.Build(projectId, e.Event!, e.DistinctId!, clientId ?? Guid.CreateVersion7().ToString(), ts, now, ip, ua, e.Properties);
            deliveries.Add((producer.ProduceAsync(EventProducer.Key(projectId, e.DistinctId!), value), clientId));
            accepted.Add(e);
        }

        try
        {
            await Task.WhenAll(deliveries.Select(d => d.Delivery));
        }
        catch (Exception ex) when (EventProducer.IsDeliveryFailure(ex))
        {
            await dedup.ReleaseAsync(projectId, FailedClaims(deliveries));
            IngestionMetrics.KafkaFailures.Add(1);
            loggers.CreateLogger(typeof(CaptureEndpoint).FullName!).LogError(ex, "Failed to write to Kafka");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        foreach (var e in accepted) definitions.Observe(projectId, e.Event!, e.Properties);
        var tag = new KeyValuePair<string, object?>("project_id", projectId);
        IngestionMetrics.Accepted.Add(accepted.Count, tag);
        if (duplicates > 0) IngestionMetrics.Duplicates.Add(duplicates, tag);
        if (received > valid.Count) IngestionMetrics.Rejected.Add(received - valid.Count, tag);
        return Results.Json(new CaptureResponse(accepted.Count + duplicates, received - valid.Count, duplicates), AppJsonContext.Default.CaptureResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    public static string? ServerKey(HttpRequest req)
    {
        var header = req.Headers.Authorization.ToString();
        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) && header.Length > BearerPrefix.Length
            ? header[BearerPrefix.Length..].Trim()
            : null;
    }

    public static DateTime? ServerTimestamp(DateTimeOffset? timestamp, DateTime now)
    {
        if (timestamp is not { } t) return now;
        var ts = t.UtcDateTime;
        return ts > now + EventTimestamps.MaxFuture || ts < now - EventTimestamps.MaxAge ? null : ts;
    }

    public static string? EventIp(Dictionary<string, JsonElement>? properties, bool anonymize)
    {
        if (properties is null || !properties.Remove(IpProperty, out var value)) return null;
        return value.ValueKind == JsonValueKind.String && IPAddress.TryParse(value.GetString(), out var address) ? IpAnonymizer.Format(address, anonymize) : null;
    }

    public static string? ClientEventId(string? eventId) =>
        eventId is { Length: > 0 } id && Encoding.UTF8.GetByteCount(id) <= MaxEventIdBytes ? id : null;

    public static List<string> FailedClaims(IEnumerable<(Task Delivery, string? ClientId)> deliveries) =>
        deliveries.Where(d => d.ClientId is not null && !d.Delivery.IsCompletedSuccessfully).Select(d => d.ClientId!).ToList();

    static async Task<CaptureRequest?> ReadAsync(HttpContext ctx)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, AppJsonContext.Default.CaptureRequest, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static bool IsValid(CaptureEvent e) =>
        !string.IsNullOrEmpty(e.Event) && !string.IsNullOrEmpty(e.DistinctId) &&
        Encoding.UTF8.GetByteCount(e.Event) <= EventMessage.MaxIdBytes &&
        Encoding.UTF8.GetByteCount(e.DistinctId) <= EventMessage.MaxIdBytes;
}
