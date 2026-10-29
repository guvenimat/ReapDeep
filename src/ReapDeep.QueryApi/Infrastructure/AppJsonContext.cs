using System.Text.Json.Serialization;
using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Deletion;
using ReapDeep.QueryApi.Health;
using ReapDeep.QueryApi.Insights;
using ReapDeep.QueryApi.Metadata;
using ReapDeep.QueryApi.Organization;
using ReapDeep.QueryApi.Persons;

namespace ReapDeep.QueryApi.Infrastructure;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ExplainResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(TrendRequest))]
[JsonSerializable(typeof(TrendResponse))]
[JsonSerializable(typeof(FunnelRequest))]
[JsonSerializable(typeof(FunnelResponse))]
[JsonSerializable(typeof(RetentionRequest))]
[JsonSerializable(typeof(RetentionResponse))]
[JsonSerializable(typeof(PathsRequest))]
[JsonSerializable(typeof(PathsResponse))]
[JsonSerializable(typeof(EventsResponse))]
[JsonSerializable(typeof(PersonRow))]
[JsonSerializable(typeof(PersonsResponse))]
[JsonSerializable(typeof(MetadataResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(PasswordChangeRequest))]
[JsonSerializable(typeof(MeResponse))]
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
[JsonSerializable(typeof(CohortBody))]
[JsonSerializable(typeof(CohortMembersBody))]
[JsonSerializable(typeof(CohortRow))]
[JsonSerializable(typeof(CohortsResponse))]
[JsonSerializable(typeof(CohortPreviewRequest))]
[JsonSerializable(typeof(CohortPreviewResponse))]
[JsonSerializable(typeof(CohortPersonsResponse))]
[JsonSerializable(typeof(CohortUsageResponse))]
internal partial class AppJsonContext : JsonSerializerContext;
