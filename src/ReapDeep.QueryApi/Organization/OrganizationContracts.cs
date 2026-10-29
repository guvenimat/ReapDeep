namespace ReapDeep.QueryApi.Organization;

public sealed record AddMemberRequest(string? Email, string? Name, string? Password, string? Role);
public sealed record RoleChangeRequest(string? Role);
public sealed record MemberRow(long UserId, string Email, string Name, string Role, DateTime CreatedAt);
public sealed record MembersResponse(List<MemberRow> Members);

public sealed record CreateApiKeyRequest(string? Name);
public sealed record ApiKeyRow(long Id, string Name, string Prefix, string? CreatedBy, DateTime CreatedAt, DateTime? RevokedAt);
public sealed record ApiKeysResponse(List<ApiKeyRow> ApiKeys);
public sealed record CreatedApiKey(ApiKeyRow ApiKey, string Key);
