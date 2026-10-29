using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReapDeep.Ingestion.Capture;

public sealed record CaptureRequest(
    [property: JsonPropertyName("api_key")] string? ApiKey,
    [property: JsonPropertyName("sent_at")] DateTimeOffset? SentAt,
    [property: JsonPropertyName("batch")] List<CaptureEvent>? Batch);

public sealed record CaptureEvent(
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("distinct_id")] string? DistinctId,
    [property: JsonPropertyName("event_id")] string? EventId,
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp,
    [property: JsonPropertyName("properties")] Dictionary<string, JsonElement>? Properties);

public sealed record CaptureResponse(
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("rejected")] int Rejected);
