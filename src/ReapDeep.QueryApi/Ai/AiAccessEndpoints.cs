using System.Text.Json;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Ai;

public sealed class PlatformAdmins(IConfiguration cfg)
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";

    readonly HashSet<string> _admins = Emails(cfg["Platform:AdminEmails"]);
    readonly HashSet<string> _viewers = Emails(cfg["Platform:ViewerEmails"]);

    static HashSet<string> Emails(string? list) => (list ?? "")
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(SessionService.NormalizeEmail).ToHashSet(StringComparer.Ordinal);

    public bool IsAdmin(string? email) => email is not null && _admins.Contains(SessionService.NormalizeEmail(email));

    public string? RoleOf(string? email) =>
        email is null ? null
        : _admins.Contains(SessionService.NormalizeEmail(email)) ? Admin
        : _viewers.Contains(SessionService.NormalizeEmail(email)) ? Viewer
        : null;
}

public sealed record PlatformAuditEntry(long Id, string ActorEmail, long? OrganizationId, string OrganizationName, string Action, JsonElement Before,
    JsonElement After, DateTime CreatedAt);
public sealed record PlatformAuditResponse(List<PlatformAuditEntry> Entries);

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
            var role = ctx.RequestServices.GetRequiredService<PlatformAdmins>().RoleOf(me.User.Email);
            if (role is null) return ApiResults.Forbidden("Only the platform team can see customers");
            if (role != PlatformAdmins.Admin && !HttpMethods.IsGet(ctx.Request.Method))
                return ApiResults.Forbidden("Platform viewers can look but not change customers' settings");
            ctx.Items[ActorKey] = me.User.Email;
            return await next(ic);
        });
        platform.MapGet("/organizations", ListOrganizations);
        platform.MapPut("/organizations/{id:long}/ai", SetPlatform);
        platform.MapGet("/audit", Audit);
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

    const string ActorKey = "platform-actor";

    static async Task<IResult> SetPlatform(long id, AiAccessBody req, HttpContext ctx, AiAccessRepository access, AiGateway ai, CancellationToken ct)
    {
        var rows = await access.ListAsync(ct);
        if (rows.FirstOrDefault(r => r.OrganizationId == id)?.ToState() is not { } current) return ApiResults.NotFound("Organization not found");
        var features = req.Features is null ? current.PlatformFeatures : AiFeatures.Validate(req.Features);
        var enabled = req.Enabled ?? current.PlatformEnabled;
        await access.SetPlatformAsync(id, enabled, [.. features], ct);
        if (enabled != current.PlatformEnabled || !features.SequenceEqual(AiFeatures.All.Where(current.PlatformFeatures.Contains)))
            await access.AuditAsync((string)ctx.Items[ActorKey]!, id, current.OrganizationName, "ai_access",
                State(current.PlatformEnabled, [.. AiFeatures.All.Where(current.PlatformFeatures.Contains)]), State(enabled, features), ct);
        return await ListOrganizations(access, ai, ct);
    }

    static string State(bool enabled, List<string> features) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = enabled,
            ["features"] = new System.Text.Json.Nodes.JsonArray(features.Select(f => (System.Text.Json.Nodes.JsonNode?)f).ToArray()),
        }.ToJsonString();

    static async Task<IResult> Audit(AiAccessRepository access, int? limit, CancellationToken ct) =>
        ApiResults.Ok(new PlatformAuditResponse(await access.AuditLogAsync(Math.Clamp(limit ?? 50, 1, 200), ct)), AppJsonContext.Default.PlatformAuditResponse);

    public static AiAccessResponse Response(AiAccessState s, AiGateway ai) =>
        new(s.PlatformEnabled, s.Allowed, s.CustomerEnabled, [.. AiFeatures.All.Where(s.CustomerFeatures.Contains)], s.Effective, ai.Configured ? ai.ModelLabel : null);
}
