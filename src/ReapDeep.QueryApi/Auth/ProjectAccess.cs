using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Auth;

public sealed class ProjectAccessMetadata
{
    public static readonly ProjectAccessMetadata Instance = new();
}

public sealed class CatalogEndpointMetadata
{
    public static readonly CatalogEndpointMetadata Instance = new();
}

public sealed class ProjectAccessMiddleware(RequestDelegate next, ApiKeyAuthenticator apiKeys, SessionService sessions)
{
    public const string ProjectHeader = "X-Project-Id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.GetEndpoint()?.Metadata.GetMetadata<ProjectAccessMetadata>() is null)
        {
            await next(ctx);
            return;
        }

        var (project, error) = await ResolveAsync(ctx);
        if (project is null)
        {
            await error!.ExecuteAsync(ctx);
            return;
        }
        if (project.KeyScope == KeyScopes.CatalogWrite && ctx.GetEndpoint()?.Metadata.GetMetadata<CatalogEndpointMetadata>() is null)
        {
            await ApiResults.Forbidden("A catalog_write key can only be used for the catalog endpoints").ExecuteAsync(ctx);
            return;
        }
        ProjectContext.Set(ctx, project);
        await next(ctx);
    }

    async ValueTask<(ProjectContext? Project, IResult? Error)> ResolveAsync(HttpContext ctx)
    {
        if (ApiKeyAuthenticator.HasKey(ctx.Request))
            return await apiKeys.AuthenticateAsync(ctx) is { } byKey
                ? (byKey, null)
                : (null, ApiResults.Unauthorized("Invalid or missing API key"));

        if (await sessions.GetCurrentAsync(ctx) is not { } me) return (null, ApiResults.Unauthorized("Not signed in"));
        if (!long.TryParse(ctx.Request.Headers[ProjectHeader].ToString(), out var projectId))
            return (null, ApiResults.BadRequest($"{ProjectHeader} header is required"));
        return sessions.ResolveProject(me, projectId) is { } project
            ? (project, null)
            : (null, ApiResults.Forbidden("You do not have access to this project"));
    }
}

public static class ProjectAccessConventions
{
    public static RouteGroupBuilder RequireProjectAccess(this RouteGroupBuilder group) => group.WithMetadata(ProjectAccessMetadata.Instance);

    public static RouteHandlerBuilder RequireUser(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter((ic, next) => ProjectContext.Get(ic.HttpContext)?.UserId is not null
            ? next(ic)
            : ValueTask.FromResult<object?>(ApiResults.Forbidden("This action requires a signed-in user")));

    public static RouteHandlerBuilder AllowCatalogKeys(this RouteHandlerBuilder route) => route.WithMetadata(CatalogEndpointMetadata.Instance);

    public static RouteHandlerBuilder RequireCatalogWriter(this RouteHandlerBuilder route) =>
        route.AllowCatalogKeys().AddEndpointFilter((ic, next) => ProjectContext.Get(ic.HttpContext) is { CanWriteCatalog: true }
            ? next(ic)
            : ValueTask.FromResult<object?>(ApiResults.Forbidden("This action requires the owner or admin role, or a catalog_write API key")));

    public static RouteHandlerBuilder RequireManager(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter((ic, next) => ProjectContext.Get(ic.HttpContext) is { UserId: not null, CanManage: true }
            ? next(ic)
            : ValueTask.FromResult<object?>(ApiResults.Forbidden("This action requires the owner or admin role")));
}
