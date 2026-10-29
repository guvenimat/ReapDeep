using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Deletion;

public static class DeletionRequestEndpoints
{
    static readonly string[] TargetTypes = ["person", "distinct_id"];

    public static void MapDeletionRequests(this RouteGroupBuilder api)
    {
        api.MapGet("/deletion-requests", List);
        api.MapPost("/deletion-requests", Create).RequireManager();
    }

    static async Task<IResult> List(ProjectContext project, DeletionRequestRepository requests, CancellationToken ct) =>
        ApiResults.Ok(new DeletionRequestsResponse(await requests.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.DeletionRequestsResponse);

    static async Task<IResult> Create(DeletionRequestBody req, ProjectContext project, DeletionRequestRepository requests, CancellationToken ct)
    {
        var targetType = Ensure.OneOf(req.TargetType, "target_type", TargetTypes);
        var targetId = Ensure.NotBlank(req.TargetId, "target_id");
        var row = await requests.CreateOrGetPendingAsync(project.ProjectId, targetType, targetId, project.UserId!.Value, ct);
        return ApiResults.Accepted(row, AppJsonContext.Default.DeletionRequestRow);
    }
}
