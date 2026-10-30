using Microsoft.AspNetCore.Mvc;
using ReapDeep.Analytics.Sql;
using ReapDeep.Recommender.Infrastructure;
using ReapDeep.Recommender.Projects;

namespace ReapDeep.Recommender.Recommendations;

public sealed record RecommendResponse(string Model, string? ItemId, List<RecommendedItem> Items);
public sealed record ErrorResponse(string Error);
public sealed record HealthResponse(string Status);

public static class RecommendEndpoint
{
    const int DefaultLimit = 10;
    const int MaxLimit = 50;
    const int MaxIdLength = 200;

    public static void MapRecommend(this WebApplication app)
    {
        app.MapGet("/recommend", Recommend);
        app.MapGet("/healthz", (ProjectCache projects, RecommendationCache cache) => projects.IsLoaded && cache.IsLoaded
            ? Results.Json(new HealthResponse("ok"), AppJsonContext.Default.HealthResponse)
            : Results.Json(new HealthResponse("loading"), AppJsonContext.Default.HealthResponse, statusCode: StatusCodes.Status503ServiceUnavailable));
    }

    static IResult Recommend(HttpContext ctx, ProjectCache projects, RecommendationCache cache,
        [FromQuery(Name = "api_key")] string? apiKey, string? model, [FromQuery(Name = "item_id")] string? itemId, string? category, int? limit)
    {
        if (!projects.IsLoaded || !cache.IsLoaded) return Error(StatusCodes.Status503ServiceUnavailable, "Starting up, try again shortly");
        if (string.IsNullOrEmpty(apiKey) || !projects.TryGetProjectId(apiKey, out var projectId)) return Error(StatusCodes.Status401Unauthorized, "Invalid api_key");

        var m = model ?? RecommendationSql.Popular;
        if (!RecommendationSql.Models.Contains(m)) return Error(StatusCodes.Status400BadRequest, $"model: {string.Join(" | ", RecommendationSql.Models)}");
        var needsItem = m is RecommendationSql.CoViewed or RecommendationSql.BoughtTogether;
        if (needsItem && string.IsNullOrWhiteSpace(itemId)) return Error(StatusCodes.Status400BadRequest, $"item_id is required for {m}");
        if (itemId?.Length > MaxIdLength || category?.Length > MaxIdLength) return Error(StatusCodes.Status400BadRequest, "item_id and category can be at most 200 characters");
        var n = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var items = cache.Get(projectId).Recommend(m, needsItem ? itemId : null, string.IsNullOrWhiteSpace(category) ? null : category, n);
        ctx.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Json(new RecommendResponse(m, needsItem ? itemId : null, items), AppJsonContext.Default.RecommendResponse);
    }

    static IResult Error(int status, string message) => Results.Json(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, statusCode: status);
}
