using ReapDeep.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Catalog;

public static class CatalogEndpoints
{
    const int DefaultLimit = 50;
    const int MaxLimit = 500;
    const int RecommendationsShown = 10;

    public static void MapCatalog(this RouteGroupBuilder api)
    {
        api.MapGet("/items", List).AllowCatalogKeys();
        api.MapGet("/items/{itemId}", Get).AllowCatalogKeys();
        api.MapPut("/items", Upsert).RequireCatalogWriter();
        api.MapDelete("/items/{itemId}", Delete).RequireCatalogWriter();
        api.MapGet("/recommendations/models", Models);
        api.MapPost("/recommendations/evaluate", Evaluate).RequireUser();
    }

    static async Task<IResult> List(ProjectContext project, CatalogRepository catalog, string? search, string? category, string? cursor, int? limit,
        CancellationToken ct)
    {
        var n = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var (items, total, inStock, categories) = await catalog.ListAsync(project.ProjectId, search, category, cursor, n, ct);
        return ApiResults.Ok(new ItemsResponse(items, items.Count == n ? items[^1].ItemId : null, total, inStock, categories),
            AppJsonContext.Default.ItemsResponse);
    }

    static async Task<IResult> Get(string itemId, ProjectContext project, CatalogRepository catalog, CancellationToken ct) =>
        await catalog.GetAsync(project.ProjectId, itemId, RecommendationsShown, ct) is { } item
            ? ApiResults.Ok(item, AppJsonContext.Default.ItemDetail)
            : ApiResults.NotFound("Item not found");

    static async Task<IResult> Upsert(ItemsUpsertRequest req, ProjectContext project, CatalogRepository catalog, CancellationToken ct) =>
        ApiResults.Ok(new ItemsUpsertResponse(await catalog.UpsertAsync(project.ProjectId, CatalogService.Validate(req.Items), ct)),
            AppJsonContext.Default.ItemsUpsertResponse);

    static async Task<IResult> Delete(string itemId, ProjectContext project, CatalogRepository catalog, CancellationToken ct) =>
        await catalog.DeleteAsync(project.ProjectId, itemId, ct) ? Results.NoContent() : ApiResults.NotFound("Item not found");

    static async Task<IResult> Models(ProjectContext project, CatalogRepository catalog, CancellationToken ct) =>
        ApiResults.Ok(new ModelRunsResponse(await catalog.RunsAsync(project.ProjectId, ct)), AppJsonContext.Default.ModelRunsResponse);

    static async Task<IResult> Evaluate(ProjectContext project, CatalogService service, int? k, CancellationToken ct) =>
        ApiResults.Ok(await service.HitRateAsync(project, Ensure.InRange(k ?? 10, 1, 50, "k"), ct), AppJsonContext.Default.HitRateResponse);
}
