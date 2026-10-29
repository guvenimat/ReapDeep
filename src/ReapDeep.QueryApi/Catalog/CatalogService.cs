using System.Diagnostics;
using System.Text.Json;
using ReapDeep.Analytics;
using ReapDeep.Analytics.Sql;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Catalog;

public sealed class CatalogService(StarRocksClient starRocks)
{
    public const int MaxItemsPerRequest = 10_000;
    const int MaxUrlLength = 2048;
    const int MaxAttributesLength = 8 * 1024;

    public static List<ValidItem> Validate(List<ItemBody>? items)
    {
        var list = items ?? [];
        Ensure.That(list.Count is > 0 and <= MaxItemsPerRequest, $"items must contain 1-{MaxItemsPerRequest} entries");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return list.Select((i, n) =>
        {
            var id = Ensure.NotBlank(i.ItemId, $"items[{n}].item_id");
            Ensure.That(seen.Add(id), $"Duplicate item_id: '{id}'");
            var title = Ensure.NotBlank(i.Title, $"items[{n}].title", 500);
            var category = i.Category?.Trim() ?? "";
            Ensure.That(category.Length <= 100, $"items[{n}].category can be at most 100 characters");
            Ensure.That(i.Price is null || (i.Price >= 0 && i.Price < 10_000_000_000m), $"items[{n}].price must be between 0 and 9,999,999,999.99");
            var currency = (i.Currency?.Trim() is { Length: > 0 } c ? c : "TRY").ToUpperInvariant();
            Ensure.That(currency.Length <= 10, $"items[{n}].currency can be at most 10 characters");
            var url = i.Url?.Trim() ?? "";
            var image = i.ImageUrl?.Trim() ?? "";
            Ensure.That(url.Length <= MaxUrlLength && image.Length <= MaxUrlLength, $"items[{n}] urls can be at most {MaxUrlLength} characters");
            var attributes = i.Attributes is { ValueKind: JsonValueKind.Object } a ? a.GetRawText() : "{}";
            Ensure.That(i.Attributes is null or { ValueKind: JsonValueKind.Object or JsonValueKind.Null }, $"items[{n}].attributes must be an object");
            Ensure.That(attributes.Length <= MaxAttributesLength, $"items[{n}].attributes is too large");
            return new ValidItem(id, title, category, i.Price is { } p ? Math.Round(p, 2) : null, currency, i.InStock ?? true, url, image, attributes);
        }).ToList();
    }

    public async Task<HitRateResponse> HitRateAsync(ProjectContext project, int k, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTime.UtcNow;
        var row = (await starRocks.QueryAsync(RecommendationSql.HitRate(project.ProjectId, now, k), r => (
            Cases: Convert.ToInt64(r.GetValue(0)),
            Similar: r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1)),
            Covered: r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2)),
            Popular: r.IsDBNull(3) ? 0 : Convert.ToInt64(r.GetValue(3))), ct)).Single();
        double Rate(long hits) => row.Cases == 0 ? 0 : Math.Round((double)hits / row.Cases, 4);
        var testFrom = now - RecommendationSql.EvaluationWindow;
        return new HitRateResponse(k, row.Cases, row.Covered, row.Similar, Rate(row.Similar), row.Popular, Rate(row.Popular),
            (testFrom - RecommendationSql.CoViewedWindow).ToString("o"), testFrom.ToString("o"), now.ToString("o"), sw.Elapsed.TotalMilliseconds);
    }
}
