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
    const int MaxEventIdLength = 64;

    public static void MapCapture(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => "ok");
        app.MapPost("/capture", Capture);
    }

    static async Task<IResult> Capture(HttpContext ctx, ProjectCache projects, DefinitionTracker definitions, EventProducer producer, EventDeduplicator dedup,
        ILoggerFactory loggers)
    {
        if (await ReadAsync(ctx) is not { } req) return Results.BadRequest("Invalid JSON");
        if (req.Batch is not { Count: > 0 } batch || string.IsNullOrEmpty(req.ApiKey)) return Results.BadRequest("api_key and batch are required");
        if (batch.Count > MaxBatchSize) return Results.BadRequest($"batch can contain at most {MaxBatchSize} events");

        if (!projects.IsLoaded) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!projects.TryGetProjectId(req.ApiKey, out var projectId)) return Results.Unauthorized();

        var now = DateTime.UtcNow;
        var timestamps = EventTimestamps.For(now, req.SentAt);
        var ip = ClientIp(ctx);
        var ua = UserAgentParser.Parse(ctx.Request.Headers.UserAgent);

        var valid = new List<(CaptureEvent Event, DateTime Timestamp, string? ClientId)>(batch.Count);
        foreach (var e in batch)
        {
            if (!IsValid(e) || timestamps.Resolve(e.Timestamp) is not { } ts) continue;
            valid.Add((e, ts, e.EventId is { Length: > 0 and <= MaxEventIdLength } id ? id : null));
        }
        var fresh = await dedup.ClaimAsync(projectId, valid.Select(v => v.ClientId).ToList());

        var deliveries = new List<Task>(valid.Count);
        var accepted = new List<CaptureEvent>(valid.Count);
        var claimed = new List<string>(valid.Count);
        var duplicates = 0;
        for (var i = 0; i < valid.Count; i++)
        {
            var (e, ts, clientId) = valid[i];
            if (!fresh[i])
            {
                duplicates++;
                continue;
            }
            if (clientId is not null) claimed.Add(clientId);
            var value = EventMessage.Build(projectId, e.Event!, e.DistinctId!, clientId ?? Guid.CreateVersion7().ToString(), ts, now, ip, ua, e.Properties);
            deliveries.Add(producer.ProduceAsync(value, ctx.RequestAborted));
            accepted.Add(e);
        }

        try
        {
            await Task.WhenAll(deliveries);
        }
        catch (Exception ex) when (EventProducer.IsDeliveryFailure(ex))
        {
            await dedup.ReleaseAsync(projectId, claimed);
            loggers.CreateLogger(typeof(CaptureEndpoint).FullName!).LogError(ex, "Failed to write to Kafka");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        foreach (var e in accepted) definitions.Observe(projectId, e.Event!, e.Properties);
        return Results.Json(new CaptureResponse(accepted.Count + duplicates, batch.Count - valid.Count, duplicates), AppJsonContext.Default.CaptureResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

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

    static string? ClientIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } a ? (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString() : null;
}
