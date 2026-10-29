using System.Collections.Frozen;
using ReapDeep.Recommender.Recommendations;

namespace ReapDeep.UnitTests;

public class RecommenderTests
{
    static CatalogItem Item(string id, string category, bool inStock = true) => new(id, id.ToUpperInvariant(), category, 10m, "EUR", inStock, "", "");

    static readonly CatalogSnapshot Snapshot = new(
        new[]
        {
            Item("shirt", "apparel"), Item("tee", "apparel"), Item("dress", "apparel", inStock: false), Item("chinos", "apparel"),
            Item("boots", "shoes"), Item("sneakers", "shoes"), Item("scarf", "accessories"),
        }.ToFrozenDictionary(i => i.ItemId),
        new Dictionary<string, Candidate[]>
        {
            ["popular"] = [new("boots", 90), new("shirt", 80), new("dress", 70), new("sneakers", 60), new("tee", 50), new("chinos", 40), new("scarf", 30)],
        }.ToFrozenDictionary(),
        new Dictionary<string, FrozenDictionary<string, Candidate[]>>
        {
            ["co_viewed"] = new Dictionary<string, Candidate[]>
            {
                ["shirt"] = [new("dress", 0.9), new("unknown", 0.8), new("tee", 0.5), new("shirt", 0.4)],
            }.ToFrozenDictionary(),
        }.ToFrozenDictionary());

    [Fact]
    public void Similar_items_skip_out_of_stock_unknown_and_the_source_item()
    {
        var items = Snapshot.Recommend("co_viewed", "shirt", null, 1);

        Assert.Equal(["tee"], items.Select(i => i.ItemId));
        Assert.Equal("co_viewed", items[0].Source);
    }

    [Fact]
    public void Short_lists_are_filled_from_popular_items_of_the_source_category_first()
    {
        var items = Snapshot.Recommend("co_viewed", "shirt", null, 4);

        Assert.Equal(["tee", "chinos", "boots", "sneakers"], items.Select(i => i.ItemId));
        Assert.Equal(["co_viewed", "popular", "popular", "popular"], items.Select(i => i.Source));
    }

    [Fact]
    public void Item_without_any_list_falls_back_to_popular_items()
    {
        var items = Snapshot.Recommend("bought_together", "scarf", null, 2);

        Assert.Equal(["boots", "shirt"], items.Select(i => i.ItemId));
    }

    [Fact]
    public void Category_filter_applies_to_global_lists()
    {
        var items = Snapshot.Recommend("popular", null, "SHOES", 10);

        Assert.Equal(["boots", "sneakers"], items.Select(i => i.ItemId));
    }

    [Fact]
    public void Empty_snapshot_returns_nothing()
    {
        Assert.Empty(CatalogSnapshot.Empty.Recommend("co_viewed", "shirt", null, 10));
        Assert.Empty(CatalogSnapshot.Empty.Recommend("popular", null, null, 10));
    }
}
