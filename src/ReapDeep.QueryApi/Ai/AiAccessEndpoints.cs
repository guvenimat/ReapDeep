using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Ai;

public sealed class PlatformAdmins(IConfiguration cfg)
{
    readonly HashSet<string> _emails = (cfg["Platform:AdminEmails"] ?? "")
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(SessionService.NormalizeEmail).ToHashSet(StringComparer.Ordinal);

    public bool IsAdmin(string? email) => email is not null && _emails.Contains(SessionService.NormalizeEmail(email));
}

public static class AiAccessEndpoints
{
    public static void MapAiAccess(this RouteGroupBuilder api)
    {
        api.MapGet("/organization/ai", Get);
        api.MapPut("/organization/ai", SetCustomer).RequireManager();
    }

    public static void MapPlatform(this WebApplication app)
    {
        var platform = app.MapGroup("/api/v1/platform").AddEndpointFilter(async (ic, next) =>
        {
            var ctx = ic.HttpContext;
            if (await ctx.RequestServices.GetRequiredService<SessionService>().GetCurrentAsync(ctx) is not { } me)
                return ApiResults.Unauthorized("Not signed in");
            return ctx.RequestServices.GetRequiredService<PlatformAdmins>().IsAdmin(me.User.Email)
                ? await next(ic)
                : ApiResults.Forbidden("Only platform administrators can manage customers");
        });
        platform.MapGet("/organizations", ListOrganizations);
        platform.MapPut("/organizations/{id:long}/ai", SetPlatform);
    }

    static async Task<IResult> Get(ProjectContext project, AiAccessRepository access, AiGateway ai, CancellationToken ct) =>
        await access.ForProjectAsync(project.ProjectId, ct) is { } state
            ? ApiResults.Ok(Response(state, ai), AppJsonContext.Default.AiAccessResponse)
            : ApiResults.NotFound("Project not found");

    static async Task<IResult> SetCustomer(AiAccessBody req, ProjectContext project, AiAccessRepository access, AiGateway ai, CancellationToken ct)
    {
        if (await access.ForProjectAsync(project.ProjectId, ct) is not { } state) return ApiResults.NotFound("Project not found");
        var features = req.Features is null ? state.CustomerFeatures : AiFeatures.Validate(req.Features);
        await access.SetCustomerAsync(state.OrganizationId, req.Enabled ?? state.CustomerEnabled, [.. features], ct);
        return await Get(project, access, ai, ct);
    }

    static async Task<IResult> ListOrganizations(AiAccessRepository access, AiGateway ai, CancellationToken ct)
    {
        var rows = await access.ListAsync(ct);
        return ApiResults.Ok(new PlatformOrganizationsResponse([.. rows.Select(r =>
        {
            var s = r.ToState();
            return new PlatformOrganization(r.OrganizationId, r.OrganizationName, r.Projects, r.Members, s.PlatformEnabled,
                [.. AiFeatures.All.Where(s.PlatformFeatures.Contains)], s.CustomerEnabled, s.CustomerFeatures, s.Effective);
        })], ai.Configured ? ai.ModelLabel : null), AppJsonContext.Default.PlatformOrganizationsResponse);
    }

    static async Task<IResult> SetPlatform(long id, AiAccessBody req, AiAccessRepository access, AiGateway ai, CancellationToken ct)
    {
        var rows = await access.ListAsync(ct);
        if (rows.FirstOrDefault(r => r.OrganizationId == id)?.ToState() is not { } current) return ApiResults.NotFound("Organization not found");
        var features = req.Features is null ? current.PlatformFeatures : AiFeatures.Validate(req.Features);
        await access.SetPlatformAsync(id, req.Enabled ?? current.PlatformEnabled, [.. features], ct);
        return await ListOrganizations(access, ai, ct);
    }

    public static AiAccessResponse Response(AiAccessState s, AiGateway ai) =>
        new(s.PlatformEnabled, s.Allowed, s.CustomerEnabled, [.. AiFeatures.All.Where(s.CustomerFeatures.Contains)], s.Effective, ai.Configured ? ai.ModelLabel : null);
}
