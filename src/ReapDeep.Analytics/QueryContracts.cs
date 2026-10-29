using System.Text.Json;

namespace ReapDeep.Analytics;

public sealed record Filter(string? Scope, string Property, string Op, JsonElement? Value);
public sealed record Breakdown(string? Scope, string Property, int? Limit, List<long>? CohortIds = null);
public sealed record Series(string Event, string? Metric);
public sealed record FunnelStep(string Event, List<Filter>? Filters);
