using System.Collections.Frozen;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.Recommender.Recommendations;

public sealed record CatalogItem(string ItemId, string Title, string Category, decimal? Price, string Currency, bool InStock, string Url, string ImageUrl);

public readonly record struct Candidate(string ItemId, double Score);

public sealed record RecommendedItem(
    string ItemId, string Title, string Category, decimal? Price, string Currency, string Url, string ImageUrl, double Score, string Source);

public sealed class CatalogSnapshot(
    FrozenDictionary<string, CatalogItem> items,
    FrozenDictionary<string, Candidate[]> global,
    FrozenDictionary<string, FrozenDictionary<string, Candidate[]>> similar)
{
    public static readonly CatalogSnapshot Empty = new(
        FrozenDictionary<string, CatalogItem>.Empty, FrozenDictionary<string, Candidate[]>.Empty,
        FrozenDictionary<string, FrozenDictionary<string, Candidate[]>>.Empty);

    public int ItemCount => items.Count;

    public CatalogItem? Item(string itemId) => items.GetValueOrDefault(itemId);

    public List<RecommendedItem> Recommend(string model, string? itemId, string? category, int limit)
    {
        var result = new List<RecommendedItem>(limit);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (itemId is not null) taken.Add(itemId);

        var isSimilar = model is RecommendationSql.CoViewed or RecommendationSql.BoughtTogether;
        var candidates = isSimilar
            ? similar.GetValueOrDefault(model)?.GetValueOrDefault(itemId!) ?? []
            : global.GetValueOrDefault(model) ?? [];
        Take(candidates, model, category, limit, result, taken);

        if (isSimilar && result.Count < limit)
        {
            var popular = global.GetValueOrDefault(RecommendationSql.Popular) ?? [];
            var sourceCategory = category ?? Item(itemId!)?.Category;
            if (!string.IsNullOrEmpty(sourceCategory)) Take(popular, RecommendationSql.Popular, sourceCategory, limit, result, taken);
            Take(popular, RecommendationSql.Popular, category, limit, result, taken);
        }
        return result;
    }

    void Take(Candidate[] candidates, string source, string? category, int limit, List<RecommendedItem> result, HashSet<string> taken)
    {
        foreach (var c in candidates)
        {
            if (result.Count >= limit) return;
            if (taken.Contains(c.ItemId) || !items.TryGetValue(c.ItemId, out var item) || !item.InStock) continue;
            if (category is not null && !string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)) continue;
            taken.Add(c.ItemId);
            result.Add(new RecommendedItem(item.ItemId, item.Title, item.Category, item.Price, item.Currency, item.Url, item.ImageUrl, c.Score, source));
        }
    }
}
