using System.Text.Json;

namespace ReapDeep.QueryApi.Catalog;

public sealed record ItemBody(
    string? ItemId, string? Title, string? Category, decimal? Price, string? Currency, bool? InStock,
    string? Url, string? ImageUrl, JsonElement? Attributes);
public sealed record ItemsUpsertRequest(List<ItemBody>? Items);
public sealed record ItemsUpsertResponse(int Upserted);

public sealed record ItemRow(
    string ItemId, string Title, string Category, decimal? Price, string Currency, bool InStock,
    string Url, string ImageUrl, JsonElement Attributes, DateTime UpdatedAt);
public sealed record ItemsResponse(List<ItemRow> Items, string? NextCursor, long Total, long InStock, List<string> Categories);

public sealed record RecommendedItem(string ItemId, string Title, string Category, bool InStock, double Score);
public sealed record ItemDetail(ItemRow Item, List<RecommendedItem> CoViewed, List<RecommendedItem> BoughtTogether);

public sealed record ModelRun(string Model, DateTime CalculatedAt, int CalculationMs, int SourceItems);
public sealed record ModelRunsResponse(List<ModelRun> Models);

public sealed record HitRateResponse(int K, long Cases, long Covered, long CoViewedHits, double CoViewedHitRate, long PopularHits, double PopularHitRate,
    string TrainFrom, string TestFrom, string TestTo, double TookMs);
