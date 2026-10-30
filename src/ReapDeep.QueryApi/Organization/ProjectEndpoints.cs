using System.Security.Cryptography;
using ReapDeep.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Organization;

public static class ProjectEndpoints
{
    const string PublicKeyPrefix = "pk_";
    const string DefaultTimezone = "Europe/Istanbul";

    public static void MapProjects(this RouteGroupBuilder api)
    {
        api.MapGet("/project", Current);
        api.MapPost("/projects", Create).RequireOwner();
    }

    static async Task<IResult> Current(ProjectContext project, ProjectRepository projects, CancellationToken ct) =>
        await projects.GetAsync(project.ProjectId, ct) is { } detail
            ? ApiResults.Ok(detail, AppJsonContext.Default.ProjectDetail)
            : ApiResults.NotFound("Project not found");

    static async Task<IResult> Create(CreateProjectRequest req, ProjectContext project, ProjectRepository projects, SessionService sessions, CancellationToken ct)
    {
        var name = Ensure.NotBlank(req.Name, "name", 100);
        var timezone = req.Timezone?.Trim() is { Length: > 0 } tz ? tz : DefaultTimezone;
        Ensure.That(TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _), $"Unknown timezone: {timezone}");
        var key = PublicKeyPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        if (await projects.CreateAsync(project.ProjectId, name, timezone, key, ct) is not { } created)
            return ApiResults.Conflict($"A project named '{name}' already exists in this organization");
        sessions.InvalidateUser(project.UserId!.Value);
        return ApiResults.Created(created, AppJsonContext.Default.ProjectDetail);
    }
}
