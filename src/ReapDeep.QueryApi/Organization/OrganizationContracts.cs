namespace ReapDeep.QueryApi.Organization;

public sealed record AddMemberRequest(string? Email, string? Name, string? Password, string? Role);
public sealed record RoleChangeRequest(string? Role);
public sealed record MemberRow(long UserId, string Email, string Name, string Role, DateTime CreatedAt, bool Pending);
public sealed record MembersResponse(List<MemberRow> Members);

public sealed record CreateApiKeyRequest(string? Name, string? Scope);
public sealed record ApiKeyRow(long Id, string Name, string Prefix, string Scope, string? CreatedBy, DateTime CreatedAt, DateTime? RevokedAt);
public sealed record ApiKeysResponse(List<ApiKeyRow> ApiKeys);
public sealed record CreatedApiKey(ApiKeyRow ApiKey, string Key);

public sealed record CreateProjectRequest(string? Name, string? Timezone);
public sealed record DeleteProjectRequest(string? Confirm);
public sealed record ProjectDetail(long Id, string Name, long OrganizationId, string OrganizationName, string Timezone, string PublicKey, DateTime CreatedAt,
    bool AnonymizeIp, int? IngestEventsPerSecond, string? RecommendationSecret = null, bool AiEnabled = false, string? AiModel = null);
public sealed record ProjectSettingsRequest(bool? AnonymizeIp, bool? AiEnabled = null);
public sealed record RecommendationSecretResponse(string RecommendationSecret);
