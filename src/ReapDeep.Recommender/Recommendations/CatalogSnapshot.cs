using System.Collections.Frozen;
using ReapDeep.Analytics.Sql;

namespace ReapDeep.Recommender.Recommendations;

public sealed record CatalogItem(string ItemId, string Title, string Category, decimal? Price, string Currency, bool InStock, string Url, string ImageUrl);

public readonly record struct Candidate(string ItemId, double Score);

public readonly record struct HistoryItem(string ItemId, int Weight, long At);

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

    public const double PersonalHalfLifeDays = 14;
    static readonly string[] PersonalSources = [RecommendationSql.CoViewed, RecommendationSql.BoughtTogether];

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

    public List<RecommendedItem> RecommendForPerson(IReadOnlyList<HistoryItem> history, string? category, int limit, DateTime nowUtc)
    {
        var bought = history.Where(h => h.Weight >= RecommendationSql.PurchaseWeight).Select(h => h.ItemId).ToHashSet(StringComparer.Ordinal);
        var now = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var h in history)
        {
            var ageDays = Math.Max(0, now - h.At) / 86_400d;
            var weight = h.Weight * Math.Pow(0.5, ageDays / PersonalHalfLifeDays);
            foreach (var model in PersonalSources)
            {
                foreach (var c in similar.GetValueOrDefault(model)?.GetValueOrDefault(h.ItemId) ?? [])
                {
                    if (bought.Contains(c.ItemId)) continue;
                    scores[c.ItemId] = scores.GetValueOrDefault(c.ItemId) + weight * c.Score;
                }
            }
        }

        var result = new List<RecommendedItem>(limit);
        var taken = new HashSet<string>(bought, StringComparer.Ordinal);
        var ranked = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new Candidate(s.Key, Math.Round(s.Value, 6))).ToArray();
        Take(ranked, RecommendationSql.ForYou, category, limit, result, taken);
        Take(global.GetValueOrDefault(RecommendationSql.Popular) ?? [], RecommendationSql.Popular, category, limit, result, taken);
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
