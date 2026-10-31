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
                ["boots"] = [new("sneakers", 0.6), new("chinos", 0.2)],
                ["tee"] = [new("chinos", 0.9), new("shirt", 0.3)],
            }.ToFrozenDictionary(),
            ["bought_together"] = new Dictionary<string, Candidate[]>
            {
                ["boots"] = [new("scarf", 0.4)],
            }.ToFrozenDictionary(),
        }.ToFrozenDictionary());

    static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    static long DaysAgo(double days) => new DateTimeOffset(Now.AddDays(-days)).ToUnixTimeSeconds();

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

    [Fact]
    public void Personal_list_sums_similar_items_of_the_history_weighted_by_interaction()
    {
        var history = new List<HistoryItem> { new("boots", 3, DaysAgo(0)), new("tee", 1, DaysAgo(0)) };

        var items = Snapshot.RecommendForPerson(history, null, 3, Now);

        Assert.Equal(["sneakers", "chinos", "scarf"], items.Select(i => i.ItemId));
        Assert.All(items, i => Assert.Equal("for_you", i.Source));
        Assert.Equal(3 * 0.6, items[0].Score, 6);
        Assert.Equal(3 * 0.2 + 1 * 0.9, items[1].Score, 6);
    }

    [Fact]
    public void Older_interactions_count_less()
    {
        var recentTee = Snapshot.RecommendForPerson([new("boots", 1, DaysAgo(28)), new("tee", 1, DaysAgo(0))], null, 1, Now);
        var recentBoots = Snapshot.RecommendForPerson([new("boots", 1, DaysAgo(0)), new("tee", 1, DaysAgo(28))], null, 1, Now);

        Assert.Equal("chinos", recentTee[0].ItemId);
        Assert.Equal(0.9 + 0.2 * 0.25, recentTee[0].Score, 6);
        Assert.Equal("sneakers", recentBoots[0].ItemId);
    }

    [Fact]
    public void Purchased_items_are_never_recommended_but_viewed_ones_can_be()
    {
        var history = new List<HistoryItem> { new("tee", 1, DaysAgo(1)), new("shirt", 1, DaysAgo(1)), new("chinos", 5, DaysAgo(2)) };

        var items = Snapshot.RecommendForPerson(history, null, 10, Now);

        Assert.DoesNotContain(items, i => i.ItemId == "chinos");
        Assert.Contains(items, i => i.ItemId == "tee" && i.Source == "for_you");
        Assert.Contains(items, i => i.ItemId == "shirt" && i.Source == "for_you");
    }

    [Fact]
    public void Personal_list_is_filled_with_popular_items_and_an_empty_history_gets_only_popular()
    {
        var filled = Snapshot.RecommendForPerson([new("boots", 3, DaysAgo(0))], null, 4, Now);
        var empty = Snapshot.RecommendForPerson([], "shoes", 5, Now);

        Assert.Equal(["sneakers", "scarf", "chinos", "boots"], filled.Select(i => i.ItemId));
        Assert.Equal(["for_you", "for_you", "for_you", "popular"], filled.Select(i => i.Source));
        Assert.Equal(["boots", "sneakers"], empty.Select(i => i.ItemId));
        Assert.All(empty, i => Assert.Equal("popular", i.Source));
    }

    [Fact]
    public void Signature_is_bound_to_the_person_and_expiry()
    {
        const string secret = "rsk_test";
        var expires = DaysAgo(0) + 600;
        var signature = PersonSignature.Sign(secret, "customer-7", expires);

        Assert.Equal(64, signature.Length);
        Assert.Equal("0ec8ce98296680b03efbb84e8046365a99890b7ffa8707582bbc2ea9a54fc3e8", PersonSignature.Sign(secret, "customer-7", 1793400000));
        Assert.Equal(SignatureCheck.Valid, PersonSignature.Verify(secret, "customer-7", expires, signature, Now));
        Assert.Equal(SignatureCheck.Valid, PersonSignature.Verify(secret, "customer-7", expires, signature.ToUpperInvariant(), Now));
        Assert.Equal(SignatureCheck.Invalid, PersonSignature.Verify(secret, "customer-8", expires, signature, Now));
        Assert.Equal(SignatureCheck.Invalid, PersonSignature.Verify(secret, "customer-7", expires + 1, signature, Now));
        Assert.Equal(SignatureCheck.Invalid, PersonSignature.Verify("rsk_other", "customer-7", expires, signature, Now));
        Assert.Equal(SignatureCheck.Invalid, PersonSignature.Verify(secret, "customer-7", expires, "abc", Now));
    }

    [Fact]
    public void Signature_must_be_current_and_short_lived()
    {
        const string secret = "rsk_test";
        var past = DaysAgo(0) - 1;
        var far = DaysAgo(0) + (long)PersonSignature.MaxLifetime.TotalSeconds + 1;

        Assert.Equal(SignatureCheck.Expired, PersonSignature.Verify(secret, "c", past, PersonSignature.Sign(secret, "c", past), Now));
        Assert.Equal(SignatureCheck.TooLong, PersonSignature.Verify(secret, "c", far, PersonSignature.Sign(secret, "c", far), Now));
    }

    [Fact]
    public void History_json_is_parsed()
    {
        var items = PersonHistoryStore.Parse("""[{"item_id":"12","weight":5,"at":1793400000},{"item_id":"7","weight":1,"at":1793300000}]""");

        Assert.Equal([new HistoryItem("12", 5, 1793400000), new HistoryItem("7", 1, 1793300000)], items);
    }
}
