using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class PersonalPriceHistoryTests
{
    private static readonly DateOnly Today = new(2026, 6, 26);

    private static PurchaseRecord Rec(string item, string store, DateOnly date, decimal unit, string source = "old")
        => new(KeyNormaliser.Normalise(item), item, store, date, 1m, unit, source);

    private static PurchaseHistoryData History(params PurchaseRecord[] records)
        => new() { Records = records.ToList() };

    private static RawItem Item(string name, decimal unit) => new(name, 1m, unit, unit);

    [Fact]
    public void Flags_item_bought_cheaper_before_elsewhere()
    {
        var history = History(Rec("Peppers", "Morrisons", Today.AddDays(-14), 0.89m));
        var current = new[] { Item("Peppers", 1.20m) };

        var cmp = Assert.Single(PersonalPriceHistoryBuilder.Build(history, current, "Sainsbury's", Today, "now"));
        Assert.Equal("Morrisons", cmp.BestRetailer);
        Assert.Equal(0.89m, cmp.BestUnitPrice);
        Assert.Equal(1.20m, cmp.PaidNow);
        Assert.Equal(0.31m, cmp.Saving);
    }

    [Fact]
    public void Picks_the_cheapest_of_several_past_prices()
    {
        var history = History(
            Rec("Butter", "Asda", Today.AddDays(-30), 2.20m),
            Rec("Butter", "Aldi", Today.AddDays(-10), 1.79m),
            Rec("Butter", "Lidl", Today.AddDays(-5), 1.95m));
        var current = new[] { Item("Butter", 2.50m) };

        var cmp = Assert.Single(PersonalPriceHistoryBuilder.Build(history, current, "Waitrose", Today, "now"));
        Assert.Equal("Aldi", cmp.BestRetailer);
        Assert.Equal(1.79m, cmp.BestUnitPrice);
    }

    [Fact]
    public void Ignores_history_from_the_same_receipt_being_analysed()
    {
        // The only cheaper row belongs to the current job → must be excluded, so nothing is flagged.
        var history = History(Rec("Milk", "Asda", Today, 0.90m, source: "now"));
        var current = new[] { Item("Milk", 1.30m) };

        Assert.Empty(PersonalPriceHistoryBuilder.Build(history, current, "Asda", Today, "now"));
    }

    [Fact]
    public void Ignores_prices_older_than_the_lookback_window()
    {
        var history = History(Rec("Coffee", "Aldi", Today.AddDays(-200), 2.00m));
        var current = new[] { Item("Coffee", 3.50m) };

        Assert.Empty(PersonalPriceHistoryBuilder.Build(history, current, "Asda", Today, "now"));
    }

    [Fact]
    public void Ignores_trivially_small_differences()
    {
        var history = History(Rec("Bananas", "Lidl", Today.AddDays(-7), 0.95m));
        var current = new[] { Item("Bananas", 1.00m) }; // 5p < 10p threshold

        Assert.Empty(PersonalPriceHistoryBuilder.Build(history, current, "Asda", Today, "now"));
    }

    [Fact]
    public void Does_not_flag_when_current_price_is_lowest()
    {
        var history = History(Rec("Eggs", "Asda", Today.AddDays(-10), 2.50m));
        var current = new[] { Item("Eggs", 2.00m) };

        Assert.Empty(PersonalPriceHistoryBuilder.Build(history, current, "Aldi", Today, "now"));
    }
}
