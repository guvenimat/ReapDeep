using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReapDeep.QueryApi;

public sealed record Filter(string? Scope, string Property, string Op, JsonElement? Value);
public sealed record Breakdown(string? Scope, string Property, int? Limit);

public sealed record Series(string Event, string? Metric);
public sealed record TrendRequest(
    List<Series>? Series, string? DateFrom, string? DateTo, string? Interval,
    List<Filter>? Filters, Breakdown? Breakdown, bool Deduplicate, bool Explain);

public sealed record TrendPoint(string Bucket, long Count);
public sealed record TrendResult(string Event, string Metric, string? BreakdownValue, long Total, List<TrendPoint> Data);
public sealed record TrendResponse(string Interval, string Timezone, List<TrendResult> Results, double TookMs);

public sealed record FunnelStep(string Event, List<Filter>? Filters);
public sealed record FunnelRequest(
    List<FunnelStep>? Steps, string? DateFrom, string? DateTo, long? WindowSeconds,
    List<Filter>? Filters, Breakdown? Breakdown, bool Explain);

public sealed record FunnelStepResult(int Step, string Event, long Count, double ConversionFromFirst, double ConversionFromPrevious);
public sealed record FunnelResult(string? BreakdownValue, List<FunnelStepResult> Steps);
public sealed record FunnelResponse(long WindowSeconds, List<FunnelResult> Results, double TookMs);

public sealed record EventRow(
    string EventId, string Event, DateTime Timestamp, string DistinctId, string ResolvedPersonId,
    Dictionary<string, string> Columns, JsonElement? Properties);
public sealed record EventsResponse(List<EventRow> Events, double TookMs);

public sealed record PersonRow(string Id, bool IsIdentified, DateTime CreatedAt, string[] DistinctIds, JsonElement Properties);
public sealed record PersonsResponse(List<PersonRow> Persons, string? NextCursor);

public sealed record NameCount(string Name, long Count);
public sealed record MetadataResponse(List<NameCount> Events, List<string> EventColumns, List<NameCount> EventProperties, List<NameCount> PersonProperties);

public sealed record ExplainResponse(List<string> Sql, List<string> Plans);
public sealed record ErrorResponse(string Error);
public sealed record HealthResponse(string Postgres, string Starrocks);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TrendRequest))]
[JsonSerializable(typeof(TrendResponse))]
[JsonSerializable(typeof(FunnelRequest))]
[JsonSerializable(typeof(FunnelResponse))]
[JsonSerializable(typeof(EventsResponse))]
[JsonSerializable(typeof(PersonsResponse))]
[JsonSerializable(typeof(PersonRow))]
[JsonSerializable(typeof(MetadataResponse))]
[JsonSerializable(typeof(ExplainResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal partial class AppJsonContext : JsonSerializerContext;

public sealed class QueryException(string message) : Exception(message);
