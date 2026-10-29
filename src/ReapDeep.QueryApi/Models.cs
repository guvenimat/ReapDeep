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
    List<Filter>? Filters, Breakdown? Breakdown, double? Sampling, bool Explain);

public sealed record FunnelStepResult(int Step, string Event, long Count, double ConversionFromFirst, double ConversionFromPrevious);
public sealed record FunnelResult(string? BreakdownValue, List<FunnelStepResult> Steps);
public sealed record FunnelResponse(long WindowSeconds, double Sampling, List<FunnelResult> Results, double TookMs);

public sealed record EventRow(
    string EventId, string Event, DateTime Timestamp, string DistinctId, string ResolvedPersonId,
    Dictionary<string, string> Columns, JsonElement? Properties);
public sealed record EventsResponse(List<EventRow> Events, double TookMs);

public sealed record PersonRow(string Id, bool IsIdentified, DateTime CreatedAt, string[] DistinctIds, JsonElement Properties);
public sealed record PersonsResponse(List<PersonRow> Persons, string? NextCursor);

public sealed record EventDefinition(string Name, DateTime LastSeenAt);
public sealed record PropertyDefinition(string Name, string? Type, DateTime LastSeenAt);
public sealed record MetadataResponse(List<EventDefinition> Events, List<string> EventColumns, List<PropertyDefinition> EventProperties, List<PropertyDefinition> PersonProperties);

public sealed record ExplainResponse(List<string> Sql, List<string> Plans);
public sealed record ErrorResponse(string Error);
public sealed record HealthResponse(string Postgres, string Starrocks);

public sealed record LoginRequest(string? Email, string? Password);
public sealed record UserInfo(long Id, string Email, string Name);
public sealed record ProjectInfo(long Id, string Name, long OrganizationId, string OrganizationName, string Role, string Timezone);
public sealed record MeResponse(UserInfo User, List<ProjectInfo> Projects);

public sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);
public sealed record AddMemberRequest(string? Email, string? Name, string? Password, string? Role);
public sealed record RoleChangeRequest(string? Role);
public sealed record MemberRow(long UserId, string Email, string Name, string Role, DateTime CreatedAt);
public sealed record MembersResponse(List<MemberRow> Members);
public sealed record CreateApiKeyRequest(string? Name);
public sealed record ApiKeyRow(long Id, string Name, string Prefix, string? CreatedBy, DateTime CreatedAt, DateTime? RevokedAt);
public sealed record ApiKeysResponse(List<ApiKeyRow> ApiKeys);
public sealed record CreatedApiKey(ApiKeyRow ApiKey, string Key);

public sealed record InsightBody(string? Name, string? Kind, JsonElement? Query);
public sealed record InsightRow(long Id, string Name, string Kind, JsonElement Query, string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record InsightsResponse(List<InsightRow> Insights);
public sealed record DashboardBody(string? Name, string? Description, List<long>? InsightIds);
public sealed record DashboardRow(long Id, string Name, string Description, string? CreatedBy, DateTime UpdatedAt, int Items);
public sealed record DashboardsResponse(List<DashboardRow> Dashboards);
public sealed record DashboardDetail(long Id, string Name, string Description, DateTime UpdatedAt, List<InsightRow> Items);

public sealed record DeletionRequestBody(string? TargetType, string? TargetId);
public sealed record DeletionRequestRow(long Id, string TargetType, string TargetId, string? RequestedBy, DateTime RequestedAt, DateTime? ProcessedAt);
public sealed record DeletionRequestsResponse(List<DeletionRequestRow> DeletionRequests);

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
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(PasswordChangeRequest))]
[JsonSerializable(typeof(AddMemberRequest))]
[JsonSerializable(typeof(RoleChangeRequest))]
[JsonSerializable(typeof(MemberRow))]
[JsonSerializable(typeof(MembersResponse))]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(ApiKeysResponse))]
[JsonSerializable(typeof(CreatedApiKey))]
[JsonSerializable(typeof(InsightBody))]
[JsonSerializable(typeof(InsightRow))]
[JsonSerializable(typeof(InsightsResponse))]
[JsonSerializable(typeof(DashboardBody))]
[JsonSerializable(typeof(DashboardsResponse))]
[JsonSerializable(typeof(DashboardDetail))]
[JsonSerializable(typeof(DeletionRequestBody))]
[JsonSerializable(typeof(DeletionRequestRow))]
[JsonSerializable(typeof(DeletionRequestsResponse))]
internal partial class AppJsonContext : JsonSerializerContext;

public sealed class QueryException(string message) : Exception(message);
