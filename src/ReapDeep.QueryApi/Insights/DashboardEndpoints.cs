using ReapDeep.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Insights;

public static class DashboardEndpoints
{
    const int MaxItems = 30;
    const int MaxDescriptionLength = 1000;

    public static void MapDashboards(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboards", List);
        api.MapGet("/dashboards/{id:long}", Get);
        api.MapPost("/dashboards", Create).RequireUser();
        api.MapPut("/dashboards/{id:long}", Update).RequireUser();
        api.MapDelete("/dashboards/{id:long}", Delete).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, DashboardRepository dashboards, CancellationToken ct) =>
        ApiResults.Ok(new DashboardsResponse(await dashboards.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.DashboardsResponse);

    static async Task<IResult> Get(long id, ProjectContext project, DashboardRepository dashboards, CancellationToken ct) =>
        await dashboards.GetAsync(project.ProjectId, id, ct) is { } detail
            ? ApiResults.Ok(detail, AppJsonContext.Default.DashboardDetail)
            : ApiResults.NotFound("Dashboard not found");

    static async Task<IResult> Create(DashboardBody req, ProjectContext project, DashboardRepository dashboards, CancellationToken ct)
    {
        var name = Ensure.NotBlank(req.Name, "name");
        var id = await dashboards.CreateAsync(project.ProjectId, name, Description(req.Description) ?? "", project.UserId!.Value, ct);
        if (req.InsightIds is { Count: > 0 } items && await SetItems(dashboards, project, id, items, ct) is { } error) return error;
        return ApiResults.Created((await dashboards.GetAsync(project.ProjectId, id, ct))!, AppJsonContext.Default.DashboardDetail);
    }

    static async Task<IResult> Update(long id, DashboardBody req, ProjectContext project, DashboardRepository dashboards, CancellationToken ct)
    {
        var name = req.Name is null ? null : Ensure.NotBlank(req.Name, "name");
        if (!await dashboards.UpdateAsync(project.ProjectId, id, name, Description(req.Description), ct)) return ApiResults.NotFound("Dashboard not found");
        if (req.InsightIds is { } items && await SetItems(dashboards, project, id, items, ct) is { } error) return error;
        return ApiResults.Ok((await dashboards.GetAsync(project.ProjectId, id, ct))!, AppJsonContext.Default.DashboardDetail);
    }

    static async Task<IResult> Delete(long id, ProjectContext project, DashboardRepository dashboards, CancellationToken ct) =>
        await dashboards.DeleteAsync(project.ProjectId, id, ct) ? Results.NoContent() : ApiResults.NotFound("Dashboard not found");

    static async Task<IResult?> SetItems(DashboardRepository dashboards, ProjectContext project, long id, List<long> items, CancellationToken ct)
    {
        var ids = items.Distinct().ToArray();
        Ensure.That(ids.Length <= MaxItems, $"A dashboard can hold at most {MaxItems} insights");
        return await dashboards.SetItemsAsync(project.ProjectId, id, ids, ct) switch
        {
            SetItemsResult.Updated => null,
            SetItemsResult.NotFound => ApiResults.NotFound("Dashboard not found"),
            _ => ApiResults.BadRequest("insight_ids contains an unknown insight"),
        };
    }

    static string? Description(string? description)
    {
        if (description is null) return null;
        var trimmed = description.Trim();
        Ensure.That(trimmed.Length <= MaxDescriptionLength, $"description can be at most {MaxDescriptionLength} characters");
        return trimmed;
    }
}
