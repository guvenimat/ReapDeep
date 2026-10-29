using System.Text.Json;
using ReapDeep.Analytics;

namespace ReapDeep.QueryApi.Analytics;

public sealed record TrendRequest(
    List<Series>? Series, string? DateFrom, string? DateTo, string? Interval,
    List<Filter>? Filters, Breakdown? Breakdown, bool Deduplicate, bool Explain);
public sealed record TrendPoint(string Bucket, long Count);
public sealed record TrendResult(string Event, string Metric, string? BreakdownValue, long Total, List<TrendPoint> Data);
public sealed record TrendResponse(string Interval, string Timezone, List<TrendResult> Results, double TookMs);

public sealed record FunnelRequest(
    List<FunnelStep>? Steps, string? DateFrom, string? DateTo, long? WindowSeconds,
    List<Filter>? Filters, Breakdown? Breakdown, double? Sampling, bool Explain);
public sealed record FunnelStepResult(int Step, string Event, long Count, double ConversionFromFirst, double ConversionFromPrevious);
public sealed record FunnelResult(string? BreakdownValue, List<FunnelStepResult> Steps);
public sealed record FunnelResponse(long WindowSeconds, double Sampling, List<FunnelResult> Results, double TookMs);

public sealed record RetentionRequest(
    string? StartEvent, string? ReturnEvent, string? Period, int? Periods, string? DateFrom, string? DateTo,
    List<Filter>? Filters, bool Explain);
public sealed record RetentionCohort(string CohortStart, long Size, List<long?> Counts);
public sealed record RetentionResponse(string Period, int Periods, string Timezone, List<RetentionCohort> Cohorts, List<double?> Average, double TookMs);

public sealed record PathsRequest(
    string? StartEvent, int? Depth, int? WindowSeconds, bool? PageviewPaths, string? DateFrom, string? DateTo,
    List<Filter>? Filters, bool Explain);
public sealed record PathNode(string Name, long Persons);
public sealed record PathStep(int Step, long Persons, List<PathNode> Nodes);
public sealed record PathSequence(List<string> Nodes, long Persons);
public sealed record PathsResponse(string StartEvent, int Depth, long Persons, List<PathStep> Steps, List<PathSequence> Paths, double TookMs);

public sealed record EventsQuery(string? Event, string? DistinctId, string? PersonId, string? DateFrom, string? DateTo, int? Limit);
public sealed record EventRow(
    string EventId, string Event, DateTime Timestamp, string DistinctId, string ResolvedPersonId,
    Dictionary<string, string> Columns, JsonElement? Properties);
public sealed record EventsResponse(List<EventRow> Events, double TookMs);
