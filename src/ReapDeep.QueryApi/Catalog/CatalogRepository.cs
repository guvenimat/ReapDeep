using System.Globalization;
using Dapper;
using Npgsql;
using ReapDeep.QueryApi.Infrastructure;

namespace ReapDeep.QueryApi.Catalog;

public sealed class CatalogRepository(NpgsqlDataSource db)
{
    internal sealed record ItemRecord(
        string ItemId, string Title, string Category, decimal? Price, string Currency, bool InStock,
        string Url, string ImageUrl, string Attributes, DateTime UpdatedAt)
    {
        public ItemRow ToContract() => new(ItemId, Title, Category, Price, Currency, InStock, Url, ImageUrl, JsonText.Parse(Attributes), UpdatedAt);
    }

    internal sealed record CountsRecord(long Total, long InStock);
    internal sealed record RecommendedRecord(string Model, string ItemId, string Title, string Category, bool InStock, double Score);
    internal sealed record RunRecord(string Model, DateTime CalculatedAt, int CalculationMs, int SourceItems);

    const string Columns = "item_id, title, category, price, currency, in_stock, url, image_url, attributes::text AS attributes, updated_at";

    public async Task<int> UpsertAsync(long projectId, IReadOnlyList<ValidItem> items, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("""
            INSERT INTO items (project_id, item_id, title, category, price, currency, in_stock, url, image_url, attributes, updated_at)
            SELECT @ProjectId, t.id, t.title, t.category, t.price::numeric, t.currency, t.stock, t.url, t.image, t.attrs::jsonb, now()
            FROM unnest(@Ids, @Titles, @Categories, @Prices, @Currencies, @Stock, @Urls, @Images, @Attributes)
                AS t(id, title, category, price, currency, stock, url, image, attrs)
            ON CONFLICT (project_id, item_id) DO UPDATE SET
                title = EXCLUDED.title, category = EXCLUDED.category, price = EXCLUDED.price, currency = EXCLUDED.currency,
                in_stock = EXCLUDED.in_stock, url = EXCLUDED.url, image_url = EXCLUDED.image_url, attributes = EXCLUDED.attributes,
                updated_at = now()
            """, new
        {
            ProjectId = projectId,
            Ids = items.Select(i => i.ItemId).ToArray(),
            Titles = items.Select(i => i.Title).ToArray(),
            Categories = items.Select(i => i.Category).ToArray(),
            Prices = items.Select(i => i.Price?.ToString(CultureInfo.InvariantCulture)).ToArray(),
            Currencies = items.Select(i => i.Currency).ToArray(),
            Stock = items.Select(i => i.InStock).ToArray(),
            Urls = items.Select(i => i.Url).ToArray(),
            Images = items.Select(i => i.ImageUrl).ToArray(),
            Attributes = items.Select(i => i.Attributes).ToArray(),
        });
    }

    public async Task<(List<ItemRow> Items, long Total, long InStock, List<string> Categories)> ListAsync(
        long projectId, string? search, string? category, string? after, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ItemRecord>($"""
            SELECT {Columns} FROM items
            WHERE project_id = @ProjectId
              AND (@Category::text IS NULL OR category = @Category::text)
              AND (@Pattern::text IS NULL OR item_id ILIKE @Pattern::text OR title ILIKE @Pattern::text)
              AND (@After::text IS NULL OR item_id > @After::text)
            ORDER BY item_id LIMIT @Limit
            """, new
        {
            ProjectId = projectId,
            Category = string.IsNullOrWhiteSpace(category) ? null : category,
            Pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + EscapeLike(search.Trim()) + "%",
            After = after,
            Limit = limit,
        });
        var counts = await conn.QuerySingleAsync<CountsRecord>(
            "SELECT count(*) AS total, count(*) FILTER (WHERE in_stock) AS in_stock FROM items WHERE project_id = @ProjectId", new { ProjectId = projectId });
        var categories = await conn.QueryAsync<string>(
            "SELECT DISTINCT category FROM items WHERE project_id = @ProjectId AND category <> '' ORDER BY category LIMIT 200", new { ProjectId = projectId });
        return (rows.Select(r => r.ToContract()).ToList(), counts.Total, counts.InStock, categories.AsList());
    }

    public async Task<ItemDetail?> GetAsync(long projectId, string itemId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var item = await conn.QuerySingleOrDefaultAsync<ItemRecord>($"SELECT {Columns} FROM items WHERE project_id = @ProjectId AND item_id = @ItemId",
            new { ProjectId = projectId, ItemId = itemId });
        if (item is null) return null;

        var recs = (await conn.QueryAsync<RecommendedRecord>("""
            SELECT r.model, c.value->>'item_id' AS item_id, i.title, i.category, i.in_stock, (c.value->>'score')::float8 AS score
            FROM item_recommendations r
            CROSS JOIN LATERAL jsonb_array_elements(r.recommended_items) WITH ORDINALITY AS c(value, ord)
            JOIN items i ON i.project_id = r.project_id AND i.item_id = c.value->>'item_id'
            WHERE r.project_id = @ProjectId AND r.source_item_id = @ItemId AND r.model IN ('co_viewed', 'bought_together')
            ORDER BY r.model, c.ord
            """, new { ProjectId = projectId, ItemId = itemId })).AsList();

        List<RecommendedItem> Of(string model) => recs.Where(r => r.Model == model).Take(limit)
            .Select(r => new RecommendedItem(r.ItemId, r.Title, r.Category, r.InStock, r.Score)).ToList();
        return new ItemDetail(item.ToContract(), Of("co_viewed"), Of("bought_together"));
    }

    public async Task<bool> DeleteAsync(long projectId, string itemId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM items WHERE project_id = @ProjectId AND item_id = @ItemId", new { ProjectId = projectId, ItemId = itemId }) > 0;
    }

    public async Task<List<ModelRun>> RunsAsync(long projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RunRecord>(
            "SELECT model, calculated_at, calculation_ms, source_items FROM recommendation_runs WHERE project_id = @ProjectId ORDER BY model",
            new { ProjectId = projectId });
        return rows.Select(r => new ModelRun(r.Model, r.CalculatedAt, r.CalculationMs, r.SourceItems)).ToList();
    }

    static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}

public sealed record ValidItem(
    string ItemId, string Title, string Category, decimal? Price, string Currency, bool InStock, string Url, string ImageUrl, string Attributes);
