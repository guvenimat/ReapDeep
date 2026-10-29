namespace ReapDeep.QueryApi.Auth;

public sealed record LoginRequest(string? Email, string? Password);
public sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);

public sealed record UserInfo(long Id, string Email, string Name);
public sealed record ProjectInfo(long Id, string Name, long OrganizationId, string OrganizationName, string Role, string Timezone);
public sealed record MeResponse(UserInfo User, List<ProjectInfo> Projects);
