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

public sealed record RfmRequest(int? WindowDays, string? PurchaseEvent, string? AmountProperty, List<Filter>? Filters, bool Explain);
public sealed record RfmCell(int R, int F, long Persons, string Segment);
public sealed record RfmSegmentResult(
    string Key, string Name, string Description, long Persons, double Share,
    double? AvgRecencyDays, double? AvgFrequency, double? AvgMonetary, double Revenue, double RevenueShare);
public sealed record RfmScoreRange(int Score, long? Min, long? Max);
public sealed record RfmThresholds(List<RfmScoreRange> Recency, List<RfmScoreRange> Frequency);
public sealed record RfmResponse(
    int WindowDays, string PurchaseEvent, string AmountProperty, DateTime CalculatedAt, long Customers, double Revenue,
    List<string> Currencies, List<RfmCell> Grid, List<RfmSegmentResult> Segments, RfmThresholds Thresholds, double TookMs);

public sealed record LifecycleRequest(
    string? Event, string? Period, int? LookbackDays, string? DateFrom, string? DateTo, List<Filter>? Filters, bool Explain);
public sealed record LifecyclePeriod(string Start, long New, long Returning, long Resurrecting, long Dormant);
public sealed record LifecycleResponse(string? Event, string Period, int LookbackDays, string Timezone, List<LifecyclePeriod> Periods, double TookMs);
