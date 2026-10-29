using System.Text.Json;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Insights;

public static class InsightEndpoints
{
    const int MaxQueryLength = 32 * 1024;

    public static void MapInsights(this RouteGroupBuilder api)
    {
        api.MapGet("/insights", List);
        api.MapPost("/insights", Create).RequireUser();
        api.MapPut("/insights/{id:long}", Update).RequireUser();
        api.MapDelete("/insights/{id:long}", Delete).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, InsightRepository insights, CancellationToken ct) =>
        ApiResults.Ok(new InsightsResponse(await insights.ListAsync(project.ProjectId, ct)), AppJsonContext.Default.InsightsResponse);

    static async Task<IResult> Create(InsightBody req, ProjectContext project, InsightRepository insights, CancellationToken ct)
    {
        var kind = Ensure.OneOf(req.Kind, "kind", InsightKinds.All);
        var name = Ensure.NotBlank(req.Name, "name");
        var query = QueryJson(req.Query);
        var row = await insights.CreateAsync(project.ProjectId, name, kind, query, project.UserId!.Value, ct);
        return ApiResults.Created(row, AppJsonContext.Default.InsightRow);
    }

    static async Task<IResult> Update(long id, InsightBody req, ProjectContext project, InsightRepository insights, CancellationToken ct)
    {
        var name = req.Name is null ? null : Ensure.NotBlank(req.Name, "name");
        var query = req.Query is null ? null : QueryJson(req.Query);
        return await insights.UpdateAsync(project.ProjectId, id, name, query, ct) is { } row
            ? ApiResults.Ok(row, AppJsonContext.Default.InsightRow)
            : ApiResults.NotFound("Insight not found");
    }

    static async Task<IResult> Delete(long id, ProjectContext project, InsightRepository insights, CancellationToken ct) =>
        await insights.DeleteAsync(project.ProjectId, id, ct) ? Results.NoContent() : ApiResults.NotFound("Insight not found");

    static string QueryJson(JsonElement? query)
    {
        Ensure.That(query is { ValueKind: JsonValueKind.Object }, "query must be a JSON object");
        var raw = query.Value.GetRawText();
        Ensure.That(raw.Length <= MaxQueryLength, "query is too large");
        return raw;
    }
}
