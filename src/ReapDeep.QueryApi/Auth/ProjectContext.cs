namespace ReapDeep.QueryApi.Auth;

public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    public static readonly string[] All = [Owner, Admin, Member];

    public static bool CanManage(string? role) => role is Owner or Admin;
}

public sealed record ProjectContext(long ProjectId, string TimeZoneId, TimeZoneInfo TimeZone, string? Role, long? UserId)
{
    const string ItemKey = nameof(ProjectContext);

    public bool CanManage => Roles.CanManage(Role);

    public bool IsOwner => Role == Roles.Owner;

    public static ValueTask<ProjectContext?> BindAsync(HttpContext ctx) => ValueTask.FromResult(Get(ctx));

    public static ProjectContext? Get(HttpContext ctx) => ctx.Items[ItemKey] as ProjectContext;

    public static void Set(HttpContext ctx, ProjectContext project) => ctx.Items[ItemKey] = project;

    public static ProjectContext Create(long projectId, string timeZoneId, string? role, long? userId, ILogger log)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var tz)) return new ProjectContext(projectId, timeZoneId, tz, role, userId);
        log.LogWarning("Project {Project}: unknown time zone {TimeZone}, falling back to UTC", projectId, timeZoneId);
        return new ProjectContext(projectId, "UTC", TimeZoneInfo.Utc, role, userId);
    }
}
