using Npgsql;

namespace ReapDeep.LoadTest;

sealed record DemoItem(string Id, string Name, string Category, double Price, bool InStock, double Popularity);

static class DemoCatalog
{
    static readonly (string Category, string[] Nouns, double BasePrice)[] Categories =
    [
        ("apparel", ["shirt", "tee", "sweater", "chinos", "dress"], 49),
        ("shoes", ["sneakers", "trainers", "boots", "loafers", "sandals"], 89),
        ("accessories", ["scarf", "belt", "cap", "sunglasses", "watch strap"], 29),
        ("outerwear", ["jacket", "parka", "raincoat", "vest", "blazer"], 139),
        ("bags", ["backpack", "tote", "duffel", "weekender", "crossbody"], 99),
        ("home", ["throw", "mug set", "candle", "cushion", "vase"], 35),
        ("beauty", ["face cream", "serum", "lip balm", "soap bar", "hand cream"], 22),
        ("sports", ["yoga mat", "water bottle", "gym bag", "resistance band", "running belt"], 39),
    ];

    static readonly string[] Adjectives = ["Linen", "Canvas", "Wool", "Denim", "Leather", "Organic", "Merino", "Recycled"];

    static readonly Dictionary<string, string> Complements = new()
    {
        ["apparel"] = "shoes", ["shoes"] = "sports", ["accessories"] = "bags", ["outerwear"] = "apparel",
        ["bags"] = "accessories", ["home"] = "beauty", ["beauty"] = "home", ["sports"] = "shoes",
    };

    static readonly string[] Classics = ["linen-shirt"];

    public static readonly DemoItem[] Items = Build();
    static readonly Dictionary<string, DemoItem> ById = Items.ToDictionary(i => i.Id);
    static readonly Dictionary<string, DemoItem[]> ByCategory = Items.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.ToArray());

    public static DemoItem Get(string id) => ById[id];

    static DemoItem[] Build()
    {
        var rng = new Random(7);
        var items = new List<DemoItem>();
        foreach (var (category, nouns, basePrice) in Categories)
        {
            var n = 0;
            foreach (var adjective in Adjectives)
                foreach (var noun in nouns)
                {
                    if (n++ >= 25) break;
                    var id = $"{adjective}-{noun}".ToLowerInvariant().Replace(' ', '-');
                    var price = Math.Round(basePrice * (0.6 + rng.NextDouble() * 1.2), 0);
                    items.Add(new DemoItem(id, $"{adjective} {noun}", category, price, rng.NextDouble() > 0.05, 1.0 / Math.Pow(n, 0.9)));
                }
        }
        foreach (var classic in Classics)
            if (items.All(i => i.Id != classic))
                throw new InvalidOperationException($"Demo catalog is missing {classic}");
        return items.ToArray();
    }

    public static DemoItem Popular(Random rng) => Pick(rng, Items);

    public static DemoItem Next(Random rng, DemoItem current)
    {
        var r = rng.NextDouble();
        if (r < 0.6) return Pick(rng, ByCategory[current.Category], current);
        if (r < 0.75) return Pick(rng, ByCategory[Complements[current.Category]]);
        return Pick(rng, Items, current);
    }

    static DemoItem Pick(Random rng, DemoItem[] items, DemoItem? except = null)
    {
        var total = items.Sum(i => i == except ? 0 : i.Popularity);
        var x = rng.NextDouble() * total;
        foreach (var i in items)
        {
            if (i == except) continue;
            if ((x -= i.Popularity) <= 0) return i;
        }
        return items[^1];
    }

    public static async Task WriteAsync(NpgsqlDataSource db, long projectId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var batch = new NpgsqlBatch(conn);
        foreach (var i in Items)
        {
            var cmd = new NpgsqlBatchCommand("""
                INSERT INTO items (project_id, item_id, title, category, price, currency, in_stock, url, attributes)
                VALUES (@p, @id, @title, @category, @price, 'EUR', @stock, @url, '{"collection":"demo"}'::jsonb)
                ON CONFLICT (project_id, item_id) DO UPDATE SET title = EXCLUDED.title, category = EXCLUDED.category,
                    price = EXCLUDED.price, in_stock = EXCLUDED.in_stock, url = EXCLUDED.url, updated_at = now()
                """);
            cmd.Parameters.AddWithValue("p", projectId);
            cmd.Parameters.AddWithValue("id", i.Id);
            cmd.Parameters.AddWithValue("title", i.Name);
            cmd.Parameters.AddWithValue("category", i.Category);
            cmd.Parameters.AddWithValue("price", (decimal)i.Price);
            cmd.Parameters.AddWithValue("stock", i.InStock);
            cmd.Parameters.AddWithValue("url", $"https://demo-shop.example/products/{i.Id}");
            batch.BatchCommands.Add(cmd);
        }
        await batch.ExecuteNonQueryAsync();
        Console.WriteLine($"Wrote {Items.Length} catalog items ({Items.Count(i => !i.InStock)} out of stock).");
    }
}
