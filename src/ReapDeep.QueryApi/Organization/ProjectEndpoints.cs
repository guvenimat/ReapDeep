using System.Security.Cryptography;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Ai;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Organization;

public static class ProjectEndpoints
{
    const string PublicKeyPrefix = "pk_";
    const string RecommendationSecretPrefix = "rsk_";
    const string DefaultTimezone = "Europe/Istanbul";

    public static void MapProjects(this RouteGroupBuilder api)
    {
        api.MapGet("/project", Current);
        api.MapPatch("/project", UpdateSettings).RequireManager();
        api.MapPost("/projects", Create).RequireOwner();
        api.MapDelete("/project", Delete).RequireOwner();
        api.MapPost("/project/recommendation-secret", RotateRecommendationSecret).RequireManager();
    }

    static async Task<IResult> Current(ProjectContext project, ProjectRepository projects, AiGateway ai, AiAccessRepository access, CancellationToken ct)
    {
        if (await projects.GetAsync(project.ProjectId, ct) is not { } detail) return ApiResults.NotFound("Project not found");
        var features = ai.Configured ? (await access.ForProjectAsync(project.ProjectId, ct))?.Effective ?? [] : [];
        return ApiResults.Ok((project.CanManage && project.UserId is not null ? detail : detail with { RecommendationSecret = null })
            with { AiModel = ai.Configured ? ai.ModelLabel : null, AiFeatures = features }, AppJsonContext.Default.ProjectDetail);
    }

    static async Task<IResult> UpdateSettings(ProjectSettingsRequest req, ProjectContext project, ProjectRepository projects, AiGateway ai,
        AiAccessRepository access, CancellationToken ct)
    {
        Ensure.That(req.AnonymizeIp is not null, "anonymize_ip is required");
        await projects.SetAnonymizeIpAsync(project.ProjectId, req.AnonymizeIp!.Value, ct);
        return await Current(project, projects, ai, access, ct);
    }

    static async Task<IResult> RotateRecommendationSecret(ProjectContext project, ProjectRepository projects, CancellationToken ct)
    {
        var secret = RecommendationSecretPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await projects.SetRecommendationSecretAsync(project.ProjectId, secret, ct);
        return ApiResults.Ok(new RecommendationSecretResponse(secret), AppJsonContext.Default.RecommendationSecretResponse);
    }

    static async Task<IResult> Delete(DeleteProjectRequest req, ProjectContext project, ProjectRepository projects, SessionService sessions,
        ApiKeyAuthenticator apiKeys, CancellationToken ct)
    {
        if (await projects.GetAsync(project.ProjectId, ct) is not { } current) return ApiResults.NotFound("Project not found");
        Ensure.That(string.Equals(req.Confirm?.Trim(), current.Name, StringComparison.Ordinal), "confirm must be the project's name");
        return await projects.DeleteAsync(project.ProjectId, project.UserId!.Value, ct) switch
        {
            { LastProject: true } => ApiResults.Conflict("The last project of an organization cannot be deleted"),
            { Deleted: { } deleted } => await Done(deleted),
            _ => ApiResults.NotFound("Project not found"),
        };

        async Task<IResult> Done(ProjectRepository.DeletedProject deleted)
        {
            foreach (var hash in deleted.KeyHashes) await apiKeys.InvalidateAsync(hash);
            foreach (var user in deleted.MemberIds) await sessions.InvalidateUserAsync(user);
            return Results.Accepted();
        }
    }

    static async Task<IResult> Create(CreateProjectRequest req, ProjectContext project, ProjectRepository projects, SessionService sessions, CancellationToken ct)
    {
        var name = Ensure.NotBlank(req.Name, "name", 100);
        var timezone = req.Timezone?.Trim() is { Length: > 0 } tz ? tz : DefaultTimezone;
        Ensure.That(TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _), $"Unknown timezone: {timezone}");
        var key = PublicKeyPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        if (await projects.CreateAsync(project.ProjectId, name, timezone, key, ct) is not { } created)
            return ApiResults.Conflict($"A project named '{name}' already exists in this organization");
        await sessions.InvalidateUserAsync(project.UserId!.Value);
        return ApiResults.Created(created, AppJsonContext.Default.ProjectDetail);
    }
}
