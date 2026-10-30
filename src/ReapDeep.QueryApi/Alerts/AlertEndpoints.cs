using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Alerts;

public static class AlertEndpoints
{
    public static void MapAlerts(this RouteGroupBuilder api)
    {
        api.MapGet("/alerts", List).RequireUser();
        api.MapPost("/alerts", Create).RequireManager();
        api.MapPut("/alerts/{id:long}", Update).RequireManager();
        api.MapDelete("/alerts/{id:long}", Delete).RequireManager();
        api.MapPost("/alerts/{id:long}/check", Check).RequireManager();
        api.MapGet("/alerts/{id:long}/events", Events).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, AlertRepository alerts, CancellationToken ct) =>
        ApiResults.Ok(new AlertsResponse((await alerts.ListAsync(project.ProjectId, ct)).Select(a => a.ToContract(project.CanManage)).ToList()),
            AppJsonContext.Default.AlertsResponse);

    static async Task<IResult> Create(AlertBody req, ProjectContext project, AlertService service, CancellationToken ct) =>
        ApiResults.Created(await service.CreateAsync(project, req, ct), AppJsonContext.Default.AlertRow);

    static async Task<IResult> Update(long id, AlertBody req, ProjectContext project, AlertService service, CancellationToken ct) =>
        await service.UpdateAsync(project, id, req, ct) is { } row
            ? ApiResults.Ok(row, AppJsonContext.Default.AlertRow)
            : ApiResults.NotFound("Alert not found");

    static async Task<IResult> Delete(long id, ProjectContext project, AlertRepository alerts, CancellationToken ct) =>
        await alerts.DeleteAsync(project.ProjectId, id, ct) ? Results.NoContent() : ApiResults.NotFound("Alert not found");

    static async Task<IResult> Check(long id, ProjectContext project, AlertService service, CancellationToken ct) =>
        await service.CheckNowAsync(project, id, ct) is { } result
            ? ApiResults.Ok(result, AppJsonContext.Default.AlertCheckResponse)
            : ApiResults.NotFound("Alert not found");

    static async Task<IResult> Events(long id, ProjectContext project, AlertRepository alerts, CancellationToken ct) =>
        ApiResults.Ok(new AlertEventsResponse(await alerts.EventsAsync(project.ProjectId, id, ct)), AppJsonContext.Default.AlertEventsResponse);
}
