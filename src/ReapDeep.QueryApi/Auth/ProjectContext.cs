namespace ReapDeep.QueryApi.Auth;

public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    public static readonly string[] All = [Owner, Admin, Member];

    public static bool CanManage(string? role) => role is Owner or Admin;
}

public static class KeyScopes
{
    public const string Read = "read";
    public const string CatalogWrite = "catalog_write";

    public static readonly string[] All = [Read, CatalogWrite];
}

public sealed record ProjectContext(long ProjectId, string TimeZoneId, TimeZoneInfo TimeZone, string? Role, long? UserId, string? KeyScope = null)
{
    const string ItemKey = nameof(ProjectContext);

    public bool CanManage => Roles.CanManage(Role);

    public bool IsOwner => Role == Roles.Owner;

    public bool CanWriteCatalog => (UserId is not null && CanManage) || KeyScope == KeyScopes.CatalogWrite;

    public static ValueTask<ProjectContext?> BindAsync(HttpContext ctx) => ValueTask.FromResult(Get(ctx));

    public static ProjectContext? Get(HttpContext ctx) => ctx.Items[ItemKey] as ProjectContext;

    public static void Set(HttpContext ctx, ProjectContext project) => ctx.Items[ItemKey] = project;

    public static ProjectContext Create(long projectId, string timeZoneId, string? role, long? userId, ILogger log, string? keyScope = null)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var tz)) return new ProjectContext(projectId, timeZoneId, tz, role, userId, keyScope);
        log.LogWarning("Project {Project}: unknown time zone {TimeZone}, falling back to UTC", projectId, timeZoneId);
        return new ProjectContext(projectId, "UTC", TimeZoneInfo.Utc, role, userId, keyScope);
    }
}
