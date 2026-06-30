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
}
