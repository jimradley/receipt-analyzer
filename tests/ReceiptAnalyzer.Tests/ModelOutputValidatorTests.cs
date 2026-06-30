using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Jobs;

namespace ReceiptAnalyzer.Tests;

public class ModelOutputValidatorTests
{
    private static readonly RawItem[] Items =
    [
        new("MISSION WRAPS 6PK", 1, 2, 2),
        new("WINE", 1, 8, 8)
    ];

    [Fact]
    public void Classification_repair_deduplicates_rejects_foreign_indices_and_fills_gaps()
    {
        var raw = new ItemClassifications(
        [
            new(0, 9, false, null, null, false, null, null, "Mission Wraps 6 pack"),
            new(0, 4, true, "Wrong duplicate", null, false, null, null),
            new(99, 4, true, "Out of range", null, false, null, null)
        ]);

        var repaired = ModelOutputValidator.Repair(Items, raw);

        Assert.Equal([0, 1], repaired.Items.Select(x => x.Index));
        Assert.Null(repaired.Items[0].NovaLevel);
        Assert.Null(repaired.Items[1].NovaLevel);
    }

    [Fact]
    public void Price_repair_uses_requested_identity_and_recomputes_saving()
    {
        var requested = new[] { new BrandedItemForCheck(0, "Maltesers 100g", 1.50m, "Morrisons") };
        var raw = new PriceCheckResult(
            [new(0, "Invented", 99, "Wrong", 1m, "Asda", 98m, null)], null);

        var item = Assert.Single(ModelOutputValidator.Repair(requested, raw).Items);
        Assert.Equal("Maltesers 100g", item.Name);
        Assert.Equal("Morrisons", item.StorePaid);
        Assert.Equal(0.50m, item.Saving);
    }
}
