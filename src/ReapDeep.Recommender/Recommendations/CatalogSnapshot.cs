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
    public const string Content = "content";
    public const int ContentCandidates = 50;
    public const int MaxContentScan = 5_000;
    const double ContentBase = 0.1;
    const double ContentTitleWeight = 0.6;
    const double ContentPriceWeight = 0.3;
    static readonly char[] TokenSeparators = [' ', '-', '_', '/', ',', '.', '(', ')', '&', '+', ':', ';', '|', '\'', '"'];
    FrozenDictionary<string, CatalogItem[]>? _byCategory;

    FrozenDictionary<string, CatalogItem[]> ByCategory => _byCategory ??= items.Values.Where(i => i.Category != "")
        .GroupBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
        .ToFrozenDictionary(g => g.Key, g => g.OrderBy(i => i.ItemId, StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
    static readonly string[] PersonalSources = [RecommendationSql.CoViewed, RecommendationSql.BoughtTogether];

    public int ItemCount => items.Count;

    public CatalogItem? Item(string itemId) => items.GetValueOrDefault(itemId);

    public List<RecommendedItem> Recommend(string model, string? itemId, string? category, int limit, int? maxPerCategory = null,
        bool contentFallback = false)
    {
        var pick = new Picker(this, category, limit, maxPerCategory);
        if (itemId is not null) pick.Taken.Add(itemId);

        var isSimilar = RecommendationSql.ItemModels.Contains(model);
        var candidates = isSimilar
            ? similar.GetValueOrDefault(model)?.GetValueOrDefault(itemId!) ?? []
            : global.GetValueOrDefault(model) ?? [];
        pick.Take(candidates, model);

        if (isSimilar && !pick.Full)
        {
            if (contentFallback) pick.Take(ContentSimilar(itemId!), Content);
            var popular = global.GetValueOrDefault(RecommendationSql.Popular) ?? [];
            var sourceCategory = category ?? Item(itemId!)?.Category;
            if (!string.IsNullOrEmpty(sourceCategory)) pick.Take(popular, RecommendationSql.Popular, sourceCategory);
            pick.Take(popular, RecommendationSql.Popular);
        }
        return pick.Result;
    }

    public List<RecommendedItem> RecommendForPerson(IReadOnlyList<HistoryItem> history, string? category, int limit, DateTime nowUtc,
        int? maxPerCategory = null)
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

        var pick = new Picker(this, category, limit, maxPerCategory);
        pick.Taken.UnionWith(bought);
        var ranked = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new Candidate(s.Key, Math.Round(s.Value, 6))).ToArray();
        pick.Take(ranked, RecommendationSql.ForYou);
        pick.Take(global.GetValueOrDefault(RecommendationSql.Popular) ?? [], RecommendationSql.Popular);
        return pick.Result;
    }

    public Candidate[] ContentSimilar(string itemId)
    {
        if (!items.TryGetValue(itemId, out var source) || !ByCategory.TryGetValue(source.Category, out var peers)) return [];
        var tokens = Tokens(source.Title);
        var scored = new List<Candidate>(Math.Min(peers.Length, MaxContentScan));
        foreach (var peer in peers.AsSpan(0, Math.Min(peers.Length, MaxContentScan)))
        {
            if (peer.ItemId == itemId || !peer.InStock) continue;
            var title = Jaccard(tokens, Tokens(peer.Title));
            var price = source.Price is > 0 && peer.Price is > 0 && source.Currency == peer.Currency
                ? (double)(Math.Min(source.Price.Value, peer.Price.Value) / Math.Max(source.Price.Value, peer.Price.Value))
                : 0;
            scored.Add(new Candidate(peer.ItemId, Math.Round(ContentBase + ContentTitleWeight * title + ContentPriceWeight * price, 6)));
        }
        return [.. scored.OrderByDescending(c => c.Score).ThenBy(c => c.ItemId, StringComparer.Ordinal).Take(ContentCandidates)];
    }

    static HashSet<string> Tokens(string title) =>
        title.ToLowerInvariant().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1).ToHashSet(StringComparer.Ordinal);

    static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    sealed class Picker(CatalogSnapshot snapshot, string? category, int limit, int? maxPerCategory)
    {
        readonly Dictionary<string, int> _perCategory = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<RecommendedItem> Result = new(limit);
        public readonly HashSet<string> Taken = new(StringComparer.Ordinal);
        public bool Full => Result.Count >= limit;

        public void Take(Candidate[] candidates, string source, string? categoryOverride = null)
        {
            var wanted = categoryOverride ?? category;
            foreach (var c in candidates)
            {
                if (Full) return;
                if (Taken.Contains(c.ItemId) || snapshot.Item(c.ItemId) is not { InStock: true } item) continue;
                if (wanted is not null && !string.Equals(item.Category, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (maxPerCategory is { } max && _perCategory.GetValueOrDefault(item.Category) >= max) continue;
                Taken.Add(c.ItemId);
                _perCategory[item.Category] = _perCategory.GetValueOrDefault(item.Category) + 1;
                Result.Add(new RecommendedItem(item.ItemId, item.Title, item.Category, item.Price, item.Currency, item.Url, item.ImageUrl, c.Score, source));
            }
        }
    }
}
