using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class SpendInsightsTests
{
    private static readonly DateOnly Today = new(2026, 6, 26);

    private static PurchaseRecord Rec(
        string item, string store, DateOnly date, decimal unit, string source,
        decimal qty = 1m, int? nova = null, bool? american = null, string? parent = null)
        => new(KeyNormaliser.Normalise(item), item, store, date, qty, unit, source, nova, american, parent);

    private static PurchaseHistoryData History(params PurchaseRecord[] records)
        => new() { Records = records.ToList() };

    [Fact]
    public void Spend_totals_by_month_use_quantity_times_unit_price()
    {
        var history = History(
            Rec("Milk", "Asda", new DateOnly(2026, 6, 1), 1.00m, "r1", qty: 2m),   // £2.00
            Rec("Bread", "Asda", new DateOnly(2026, 6, 1), 1.50m, "r1"),           // £1.50  (same receipt)
            Rec("Cheese", "Aldi", new DateOnly(2026, 5, 3), 3.00m, "r2"));         // £3.00

        var spend = SpendInsightsBuilder.Build(history, Today).Spend;

        Assert.Equal(6.50m, spend.TotalAllTime);
        Assert.Equal(3.50m, spend.ThisMonth);   // June
        Assert.Equal(3.00m, spend.LastMonth);   // May
        Assert.Equal(2, spend.ReceiptsAllTime); // r1, r2

        var june = Assert.Single(spend.Months, m => m.Month == "2026-06");
        Assert.Equal(3.50m, june.Total);
        Assert.Equal(1, june.Receipts);
    }

    [Fact]
    public void Retailers_ranked_by_spend_with_share()
    {
        var history = History(
            Rec("A", "Asda", new DateOnly(2026, 6, 1), 30.00m, "r1"),
            Rec("B", "Aldi", new DateOnly(2026, 6, 2), 10.00m, "r2"));

        var retailers = SpendInsightsBuilder.Build(history, Today).Spend.Retailers;
        Assert.Equal("Asda", retailers[0].Retailer);
        Assert.Equal(0.75, retailers[0].Share, 3);
        Assert.Equal("Aldi", retailers[1].Retailer);
    }

    [Fact]
    public void American_repeat_offender_needs_two_distinct_days()
    {
        var history = History(
            Rec("Coke", "Asda", Today.AddDays(-20), 1.50m, "r1", american: true, parent: "Coca-Cola Co"),
            Rec("Coke", "Aldi", Today.AddDays(-5), 1.40m, "r2", american: true, parent: "Coca-Cola Co"));

        var offenders = SpendInsightsBuilder.Build(history, Today).Offenders;
        var coke = Assert.Single(offenders.American);
        Assert.Equal(2, coke.TimesBought);
        Assert.Equal("Coca-Cola Co", coke.ParentCompany);
        Assert.Equal(2.90m, coke.TotalSpent);
        Assert.Equal(new[] { "Aldi", "Asda" }, coke.Stores); // most recent first
    }

    [Fact]
    public void Single_purchase_is_not_a_repeat_offender()
    {
        var history = History(
            Rec("Pop Tarts", "Asda", Today.AddDays(-3), 2.00m, "r1", nova: 4, american: true, parent: "Kellanova"));

        var offenders = SpendInsightsBuilder.Build(history, Today).Offenders;
        Assert.Empty(offenders.American);
        Assert.Empty(offenders.UltraProcessed);
    }

    [Fact]
    public void Only_nova_four_counts_as_ultra_processed()
    {
        var history = History(
            // NOVA 3 bought twice — processed but not "heavy", should not appear.
            Rec("Tinned Soup", "Asda", Today.AddDays(-20), 0.80m, "r1", nova: 3),
            Rec("Tinned Soup", "Asda", Today.AddDays(-6), 0.80m, "r2", nova: 3),
            // NOVA 4 bought twice — should appear.
            Rec("Cereal Bars", "Aldi", Today.AddDays(-18), 1.50m, "r3", nova: 4),
            Rec("Cereal Bars", "Aldi", Today.AddDays(-4), 1.50m, "r4", nova: 4));

        var processed = SpendInsightsBuilder.Build(history, Today).Offenders.UltraProcessed;
        var bars = Assert.Single(processed);
        Assert.Equal("Cereal Bars", bars.Item);
        Assert.Equal(4, bars.NovaLevel);
    }

    [Fact]
    public void A_single_stray_nova_reading_does_not_brand_an_item_ultra_processed()
    {
        // Wine read NOVA 1 once and (wrongly) NOVA 4 once → representative is the lower, so not flagged.
        var history = History(
            Rec("Yellow Tail Shiraz", "Morrisons", Today.AddDays(-40), 8.50m, "r1", nova: 1),
            Rec("Yellow Tail Shiraz", "Morrisons", Today.AddDays(-3), 8.50m, "r2", nova: 4));

        Assert.Empty(SpendInsightsBuilder.Build(history, Today).Offenders.UltraProcessed);
    }

    [Fact]
    public void Consistent_nova_four_readings_are_flagged()
    {
        var history = History(
            Rec("Maltesers", "Asda", Today.AddDays(-30), 1.50m, "r1", nova: 4),
            Rec("Maltesers", "Aldi", Today.AddDays(-10), 1.40m, "r2", nova: 4));

        var bar = Assert.Single(SpendInsightsBuilder.Build(history, Today).Offenders.UltraProcessed);
        Assert.Equal(4, bar.NovaLevel);
    }

    [Fact]
    public void Offenders_ignore_purchases_older_than_a_year()
    {
        var history = History(
            Rec("Coke", "Asda", Today.AddDays(-400), 1.50m, "r1", american: true),
            Rec("Coke", "Asda", Today.AddDays(-380), 1.50m, "r2", american: true));

        Assert.Empty(SpendInsightsBuilder.Build(history, Today).Offenders.American);
    }
}
