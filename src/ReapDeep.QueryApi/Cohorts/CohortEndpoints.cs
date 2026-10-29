using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Cohorts;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Cohorts;

public static class CohortEndpoints
{
    const int DefaultPersonsLimit = 50;
    const int MaxPersonsLimit = 200;

    public static void MapCohorts(this RouteGroupBuilder api)
    {
        api.MapGet("/cohorts", List);
        api.MapGet("/cohorts/{id:long}", Get);
        api.MapGet("/cohorts/{id:long}/persons", Persons);
        api.MapGet("/cohorts/{id:long}/usage", Usage);
        api.MapPost("/cohorts", Create).RequireUser();
        api.MapPost("/cohorts/preview", Preview).RequireUser();
        api.MapPut("/cohorts/{id:long}", Update).RequireUser();
        api.MapPut("/cohorts/{id:long}/members", ReplaceMembers).RequireUser();
        api.MapPost("/cohorts/{id:long}/recalculate", Recalculate).RequireUser();
        api.MapDelete("/cohorts/{id:long}", Delete).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, CohortRepository cohorts, CancellationToken ct) =>
        ApiResults.Ok(new CohortsResponse(await cohorts.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.CohortsResponse);

    static async Task<IResult> Get(long id, ProjectContext project, CohortRepository cohorts, CancellationToken ct) =>
        await cohorts.GetAsync(project.ProjectId, id, ct) is { } c
            ? ApiResults.Ok(c.Row, AppJsonContext.Default.CohortRow)
            : ApiResults.NotFound("Cohort not found");

    static async Task<IResult> Persons(long id, ProjectContext project, CohortService service, string? cursor, int? limit, CancellationToken ct) =>
        await service.PersonsAsync(project, id, cursor, Math.Clamp(limit ?? DefaultPersonsLimit, 1, MaxPersonsLimit), ct) is { } page
            ? ApiResults.Ok(page, AppJsonContext.Default.CohortPersonsResponse)
            : ApiResults.NotFound("Cohort not found");

    static async Task<IResult> Usage(long id, ProjectContext project, CohortRepository cohorts, CancellationToken ct) =>
        await cohorts.GetAsync(project.ProjectId, id, ct) is null
            ? ApiResults.NotFound("Cohort not found")
            : ApiResults.Ok(new CohortUsageResponse(await cohorts.UsageAsync(project.ProjectId, id, ct)), AppJsonContext.Default.CohortUsageResponse);

    static async Task<IResult> Preview(CohortPreviewRequest req, ProjectContext project, CohortService service, CancellationToken ct) =>
        ApiResults.Ok(await service.PreviewAsync(project, req.Definition, ct), AppJsonContext.Default.CohortPreviewResponse);

    static async Task<IResult> Create(CohortBody req, ProjectContext project, CohortRepository cohorts, CohortService service, CancellationToken ct)
    {
        var name = Ensure.NotBlank(req.Name, "name");
        var description = req.Description?.Trim() ?? "";
        Ensure.That(description.Length <= 1000, "description can be at most 1000 characters");
        var kind = Ensure.OneOf(req.Kind ?? CohortKinds.Dynamic, "kind", CohortKinds.All);

        var (definition, members) = kind == CohortKinds.Dynamic
            ? (DefinitionJson(req.Definition), (IReadOnlyList<string>)[])
            : ("{}", await service.StaticMembersAsync(project, req.Members, ct));
        var row = await cohorts.CreateAsync(project.ProjectId, name, description, kind, definition, members, project.UserId!.Value, ct);
        return ApiResults.Created(row, AppJsonContext.Default.CohortRow);
    }

    static async Task<IResult> Update(long id, CohortBody req, ProjectContext project, CohortRepository cohorts, CancellationToken ct)
    {
        if (await cohorts.GetAsync(project.ProjectId, id, ct) is not { } existing) return ApiResults.NotFound("Cohort not found");
        Ensure.That(req.Kind is null || req.Kind == existing.Row.Kind, "kind cannot be changed");
        Ensure.That(req.Definition is null || existing.Row.Kind == CohortKinds.Dynamic, "A static cohort has no definition; replace its members instead");

        var name = req.Name is null ? null : Ensure.NotBlank(req.Name, "name");
        var description = req.Description?.Trim();
        Ensure.That(description is null || description.Length <= 1000, "description can be at most 1000 characters");
        var definition = req.Definition is null ? null : DefinitionJson(req.Definition);
        return await cohorts.UpdateAsync(project.ProjectId, id, name, description, definition, ct) is { } row
            ? ApiResults.Ok(row, AppJsonContext.Default.CohortRow)
            : ApiResults.NotFound("Cohort not found");
    }

    static async Task<IResult> ReplaceMembers(long id, CohortMembersBody req, ProjectContext project, CohortRepository cohorts, CohortService service,
        CancellationToken ct)
    {
        var members = await service.StaticMembersAsync(project, req.Members, ct);
        return await cohorts.ReplaceMembersAsync(project.ProjectId, id, members, ct) && await cohorts.GetAsync(project.ProjectId, id, ct) is { } c
            ? ApiResults.Ok(c.Row, AppJsonContext.Default.CohortRow)
            : ApiResults.NotFound("Static cohort not found");
    }

    static async Task<IResult> Recalculate(long id, ProjectContext project, CohortRepository cohorts, CancellationToken ct) =>
        await cohorts.RecalculateAsync(project.ProjectId, id, ct) && await cohorts.GetAsync(project.ProjectId, id, ct) is { } c
            ? ApiResults.Accepted(c.Row, AppJsonContext.Default.CohortRow)
            : ApiResults.NotFound("Cohort not found");

    static async Task<IResult> Delete(long id, ProjectContext project, CohortRepository cohorts, CancellationToken ct) =>
        await cohorts.DeleteAsync(project.ProjectId, id, ct) ? Results.NoContent() : ApiResults.NotFound("Cohort not found");

    static string DefinitionJson(CohortDefinition? definition) =>
        JsonSerializer.Serialize(CohortRules.Normalize(definition), CohortJsonContext.Default.CohortDefinition);
}
