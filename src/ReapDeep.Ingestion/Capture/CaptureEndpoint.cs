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

    static async Task<IResult> Capture(HttpContext ctx, ProjectCache projects, DefinitionTracker definitions, EventProducer producer, ILoggerFactory loggers)
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

        var deliveries = new List<Task>(batch.Count);
        var accepted = new List<CaptureEvent>(batch.Count);
        foreach (var e in batch)
        {
            if (!IsValid(e) || timestamps.Resolve(e.Timestamp) is not { } ts) continue;
            var eventId = e.EventId is { Length: > 0 and <= MaxEventIdLength } id ? id : Guid.CreateVersion7().ToString();
            var value = EventMessage.Build(projectId, e.Event!, e.DistinctId!, eventId, ts, now, ip, ua, e.Properties);
            deliveries.Add(producer.ProduceAsync(value, ctx.RequestAborted));
            accepted.Add(e);
        }

        try
        {
            await Task.WhenAll(deliveries);
        }
        catch (Exception ex) when (EventProducer.IsDeliveryFailure(ex))
        {
            loggers.CreateLogger(typeof(CaptureEndpoint).FullName!).LogError(ex, "Failed to write to Kafka");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        foreach (var e in accepted) definitions.Observe(projectId, e.Event!, e.Properties);
        return Results.Json(new CaptureResponse(accepted.Count, batch.Count - accepted.Count), AppJsonContext.Default.CaptureResponse,
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
