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

    static async Task<IResult> Recommend(HttpContext ctx, ProjectCache projects, RecommendationCache cache, PersonHistoryStore histories,
        [FromQuery(Name = "api_key")] string? apiKey, string? model, [FromQuery(Name = "item_id")] string? itemId, string? category, int? limit,
        [FromQuery(Name = "distinct_id")] string? distinctId, long? expires, string? signature, CancellationToken ct)
    {
        if (!projects.IsLoaded || !cache.IsLoaded) return Error(StatusCodes.Status503ServiceUnavailable, "Starting up, try again shortly");
        if (string.IsNullOrEmpty(apiKey) || !projects.TryGetProjectId(apiKey, out var projectId)) return Error(StatusCodes.Status401Unauthorized, "Invalid api_key");

        var m = model ?? RecommendationSql.Popular;
        if (m != RecommendationSql.ForYou && !RecommendationSql.Models.Contains(m))
            return Error(StatusCodes.Status400BadRequest, $"model: {string.Join(" | ", RecommendationSql.Models.Append(RecommendationSql.ForYou))}");
        var needsItem = m is RecommendationSql.CoViewed or RecommendationSql.BoughtTogether;
        if (needsItem && string.IsNullOrWhiteSpace(itemId)) return Error(StatusCodes.Status400BadRequest, $"item_id is required for {m}");
        if (itemId?.Length > MaxIdLength || category?.Length > MaxIdLength || distinctId?.Length > MaxIdLength)
            return Error(StatusCodes.Status400BadRequest, "item_id, category and distinct_id can be at most 200 characters");
        var n = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var categoryFilter = string.IsNullOrWhiteSpace(category) ? null : category;

        if (m == RecommendationSql.ForYou)
        {
            if (string.IsNullOrWhiteSpace(distinctId) || expires is null || string.IsNullOrEmpty(signature))
                return Error(StatusCodes.Status400BadRequest, "distinct_id, expires and signature are required for for_you");
            if (projects.RecommendationSecret(projectId) is not { } secret)
                return Error(StatusCodes.Status403Forbidden, "Personalized recommendations are not enabled for this project");
            switch (PersonSignature.Verify(secret, distinctId, expires.Value, signature, DateTime.UtcNow))
            {
                case SignatureCheck.Invalid: return Error(StatusCodes.Status401Unauthorized, "Invalid signature");
                case SignatureCheck.Expired: return Error(StatusCodes.Status401Unauthorized, "Signature expired");
                case SignatureCheck.TooLong: return Error(StatusCodes.Status400BadRequest, $"expires can be at most {PersonSignature.MaxLifetime.TotalMinutes:0} minutes ahead");
            }
            var history = await histories.GetAsync(projectId, distinctId, ct);
            var personal = cache.Get(projectId).RecommendForPerson(history, categoryFilter, n, DateTime.UtcNow);
            ctx.Response.Headers.CacheControl = "private, no-store";
            return Results.Json(new RecommendResponse(m, null, personal), AppJsonContext.Default.RecommendResponse);
        }

        var items = cache.Get(projectId).Recommend(m, needsItem ? itemId : null, categoryFilter, n);
        ctx.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Json(new RecommendResponse(m, needsItem ? itemId : null, items), AppJsonContext.Default.RecommendResponse);
    }

    static IResult Error(int status, string message) => Results.Json(new ErrorResponse(message), AppJsonContext.Default.ErrorResponse, statusCode: status);
}
