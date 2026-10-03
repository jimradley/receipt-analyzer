using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class ReplenishmentTests
{
    private static readonly DateOnly Today = new(2026, 6, 26);

    private static PurchaseRecord Rec(string item, string store, DateOnly date, decimal unit = 1.00m)
        => new(KeyNormaliser.Normalise(item), item, store, date, 1m, unit, "test");

    private static PurchaseHistoryData History(params PurchaseRecord[] records)
        => new() { Records = records.ToList() };

    /// <summary>Buys every 7 days, last 9 days ago → overdue by 2.</summary>
    [Fact]
    public void Weekly_item_bought_9_days_ago_is_overdue()
    {
        var history = History(
            Rec("Milk", "Asda", Today.AddDays(-30)),
            Rec("Milk", "Asda", Today.AddDays(-23)),
            Rec("Milk", "Asda", Today.AddDays(-16)),
            Rec("Milk", "Asda", Today.AddDays(-9)));

        var milk = Assert.Single(ReplenishmentBuilder.Build(history, Today).Staples);
        Assert.Equal(7, milk.CadenceDays);
        Assert.Equal("Overdue", milk.Status);
        Assert.Equal(-2, milk.DueInDays);
        Assert.Equal(4, milk.PurchaseCount);
        Assert.Equal(ShoppingAisleCatalog.DairyAndEggs, milk.Aisle);
        Assert.Equal(ShoppingAisleCatalog.SortOrder(ShoppingAisleCatalog.DairyAndEggs), milk.AisleOrder);
    }

    /// <summary>Buys every 21 days, last 5 days ago → on track (16 days to go).</summary>
    [Fact]
    public void Three_weekly_item_bought_recently_is_on_track()
    {
        var history = History(
            Rec("Coffee", "Aldi", Today.AddDays(-47)),
            Rec("Coffee", "Aldi", Today.AddDays(-26)),
            Rec("Coffee", "Aldi", Today.AddDays(-5)));

        var coffee = Assert.Single(ReplenishmentBuilder.Build(history, Today).Staples);
        Assert.Equal(21, coffee.CadenceDays);
        Assert.Equal("OnTrack", coffee.Status);
        Assert.Equal(16, coffee.DueInDays);
    }

    [Fact]
    public void Due_within_quarter_of_cadence_is_due_soon()
    {
        // cadence 20, last 18 days ago → dueInDays 2, soonWindow max(2, 5)=5 → DueSoon.
        var history = History(
            Rec("Eggs", "Lidl", Today.AddDays(-58)),
            Rec("Eggs", "Lidl", Today.AddDays(-38)),
            Rec("Eggs", "Lidl", Today.AddDays(-18)));

        var eggs = Assert.Single(ReplenishmentBuilder.Build(history, Today).Staples);
        Assert.Equal(20, eggs.CadenceDays);
        Assert.Equal("DueSoon", eggs.Status);
    }

    [Fact]
    public void Median_ignores_one_irregular_gap()
    {
        // Gaps 7, 7, 60, 7 → median 7 (mean would be ~20).
        var history = History(
            Rec("Bread", "Asda", Today.AddDays(-81)),
            Rec("Bread", "Asda", Today.AddDays(-74)),
            Rec("Bread", "Asda", Today.AddDays(-67)),
            Rec("Bread", "Asda", Today.AddDays(-7)),
            Rec("Bread", "Asda", Today));

        var bread = Assert.Single(ReplenishmentBuilder.Build(history, Today).Staples);
        Assert.Equal(7, bread.CadenceDays);
    }

    [Fact]
    public void Fewer_than_three_distinct_days_is_counted_not_predicted()
    {
        var history = History(
            Rec("Truffle Oil", "Waitrose", Today.AddDays(-40)),
            Rec("Truffle Oil", "Waitrose", Today.AddDays(-10)));

        var result = ReplenishmentBuilder.Build(history, Today);
        Assert.Empty(result.Staples);
        Assert.Equal(1, result.InsufficientDataItems);
    }

    [Fact]
    public void Same_day_lines_collapse_to_one_purchase_event()
    {
        // Two lines on each of three days — should read as 3 events (gaps 7,7), not 6.
        var history = History(
            Rec("Yoghurt", "Asda", Today.AddDays(-14)), Rec("Yoghurt", "Asda", Today.AddDays(-14)),
            Rec("Yoghurt", "Asda", Today.AddDays(-7)), Rec("Yoghurt", "Asda", Today.AddDays(-7)),
            Rec("Yoghurt", "Asda", Today), Rec("Yoghurt", "Asda", Today));

        var y = Assert.Single(ReplenishmentBuilder.Build(history, Today).Staples);
        Assert.Equal(3, y.PurchaseCount);
        Assert.Equal(7, y.CadenceDays);
    }

    [Fact]
    public void Staples_are_ordered_most_overdue_first()
    {
        var history = History(
            // On track: every 14, last 2 ago.
            Rec("Cheese", "Aldi", Today.AddDays(-30)), Rec("Cheese", "Aldi", Today.AddDays(-16)), Rec("Cheese", "Aldi", Today.AddDays(-2)),
            // Overdue: every 7, last 20 ago.
            Rec("Bananas", "Lidl", Today.AddDays(-34)), Rec("Bananas", "Lidl", Today.AddDays(-27)), Rec("Bananas", "Lidl", Today.AddDays(-20)));

        var staples = ReplenishmentBuilder.Build(history, Today).Staples;
        Assert.Equal("Bananas", staples[0].Item);
        Assert.Equal("Overdue", staples[0].Status);
        Assert.Equal("Cheese", staples[1].Item);
    }

    [Fact]
    public void Stores_aggregate_most_recent_first()
    {
        var history = History(
            Rec("Butter", "Aldi", Today.AddDays(-28)),
            Rec("Butter", "Asda", Today.AddDays(-14)),
            Rec("Butter", "Lidl", Today));

        var butter = Assert.Single(ReplenishmentBuilder.Build(history, Today).Staples);
        Assert.Equal(new[] { "Lidl", "Asda", "Aldi" }, butter.Stores);
    }

    [Fact]
    public void Current_cheapest_store_overrides_historical_store_list()
    {
        var history = History(
            Rec("Milk 2L", "Asda", Today.AddDays(-28)),
            Rec("Milk 2L", "Asda", Today.AddDays(-14)),
            Rec("Milk 2L", "Asda", Today));
        var inventory = new InventoryData
        {
            Products =
            [
                new("milk|2l", "milk", "2L", "Milk 2L", 3, Today.ToString("yyyy-MM-dd"), "Asda", 2m,
                    InventoryMatchStatus.Matched, Offers:
                    [
                        new("Tesco", 1.50m, 1.50m, 1, 1.50m, null, false, "2026-09-12", "https://www.trolley.co.uk/product/milk"),
                        new("Aldi", 1.40m, 1.40m, 1, 1.40m, null, false, "2026-09-12", "https://www.trolley.co.uk/product/milk")
                    ])
            ]
        };

        var result = ReplenishmentBuilder.WithCurrentPrices(
            ReplenishmentBuilder.Build(history, Today), inventory);

        var staple = Assert.Single(result.Staples);
        Assert.Equal(new[] { "Aldi" }, staple.CheapestStores);
        Assert.Equal(1.40m, staple.CheapestPrice);
    }

    [Fact]
    public void Current_prices_match_on_canonical_key_and_prefer_variants_with_offers()
    {
        var history = History(
            Rec("MOORISH SMKD HOUMOUS", "Morrisons", Today.AddDays(-28)),
            Rec("MOORISH SMKD HOUMOUS", "Morrisons", Today.AddDays(-14)),
            Rec("MOORISH SMKD HOUMOUS", "Morrisons", Today));
        var built = ReplenishmentBuilder.Build(history, Today);
        var key = Assert.Single(built.Staples).Key;
        var offer = new StorePriceOffer("Aldi", 1.40m, 1.40m, 1, 1.40m, null, false, "2026-09-12", "https://www.trolley.co.uk/product/x");
        var inventory = new InventoryData
        {
            Products =
            [
                // Newer but unmatched variant sharing the key must not hide the matched one.
                new($"{key}|unknown-size", key, null, "Unmatched", 3, Today.ToString("yyyy-MM-dd"), "Morrisons", 2m,
                    InventoryMatchStatus.NotComparable),
                new($"{key}|200g", key, "200g", "Matched", 3, Today.AddDays(-14).ToString("yyyy-MM-dd"), "Morrisons", 2m,
                    InventoryMatchStatus.Matched, Offers: [offer])
            ]
        };

        var result = ReplenishmentBuilder.WithCurrentPrices(built, inventory);

        var staple = Assert.Single(result.Staples);
        Assert.Equal(new[] { "Aldi" }, staple.CheapestStores);
        Assert.Equal(1.40m, staple.CheapestPrice);
    }

    private static StaplePrediction OneStaple(string item) => Assert.Single(ReplenishmentBuilder.Build(History(
        Rec(item, "Morrisons", Today.AddDays(-28), 3.00m),
        Rec(item, "Morrisons", Today.AddDays(-14), 3.00m),
        Rec(item, "Morrisons", Today, 3.00m)), Today).Staples);

    private static InventoryData Inventory(string productKey, string? pack, decimal price) => new()
    {
        Products =
        [
            new($"{productKey}|{pack ?? "unknown-size"}", productKey, pack, "P", 3, Today.ToString("yyyy-MM-dd"), "Morrisons", 3m,
                InventoryMatchStatus.Matched, Offers:
                [new("Aldi", price, price, 1, price, null, false, "2026-09-12", "https://www.trolley.co.uk/product/p")])
        ]
    };

    [Fact]
    public void Implausible_price_with_unknown_pack_is_withheld()
    {
        var staple = OneStaple("Best Prosecco");
        var result = ReplenishmentBuilder.WithCurrentPrices(
            new ReplenishmentResult([staple], 0), Inventory(staple.Key, null, 1.00m));

        var priced = Assert.Single(result.Staples);
        Assert.Null(priced.CheapestPrice);
        Assert.Empty(priced.CheapestStores!);
        Assert.Equal("Pack size unclear", priced.PriceNote);
    }

    [Fact]
    public void Same_price_gap_is_kept_when_the_pack_is_known()
    {
        var staple = OneStaple("Best Prosecco");
        var result = ReplenishmentBuilder.WithCurrentPrices(
            new ReplenishmentResult([staple], 0), Inventory(staple.Key, "75cl", 1.00m));

        Assert.Equal(1.00m, Assert.Single(result.Staples).CheapestPrice);
    }

    [Fact]
    public void Staple_key_words_contained_in_one_inventory_key_fuzzy_match()
    {
        var staple = OneStaple("Yeo Valley Kefir");
        var result = ReplenishmentBuilder.WithCurrentPrices(
            new ReplenishmentResult([staple], 0), Inventory("yeo-valley-organic-kefir", null, 3.00m));

        Assert.Equal(new[] { "Aldi" }, Assert.Single(result.Staples).CheapestStores);
    }

    [Fact]
    public void Ambiguous_fuzzy_match_is_ignored()
    {
        var staple = OneStaple("Yeo Valley Kefir");
        var inventory = Inventory("yeo-valley-organic-kefir", null, 3.00m);
        inventory.Products.AddRange(Inventory("yeo-valley-plain-kefir", null, 3.00m).Products);

        var result = ReplenishmentBuilder.WithCurrentPrices(new ReplenishmentResult([staple], 0), inventory);

        Assert.Empty(Assert.Single(result.Staples).CheapestStores!);
    }

    [Fact]
    public void Extreme_gap_is_withheld_even_when_the_pack_is_known()
    {
        var staple = OneStaple("Pepsi Max Cherry");
        var result = ReplenishmentBuilder.WithCurrentPrices(
            new ReplenishmentResult([staple], 0), Inventory(staple.Key, "2l", 0.60m));

        var priced = Assert.Single(result.Staples);
        Assert.Null(priced.CheapestPrice);
        Assert.Equal("Last price looks wrong", priced.PriceNote);
    }
}
