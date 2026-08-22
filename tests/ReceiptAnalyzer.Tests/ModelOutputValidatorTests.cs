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

    [Fact]
    public void Price_repair_backfills_an_omitted_item_as_unchecked()
    {
        // An item the model dropped from its JSON must not silently vanish from the results.
        var requested = new[]
        {
            new BrandedItemForCheck(0, "Maltesers 100g", 1.50m, "Morrisons"),
            new BrandedItemForCheck(1, "Beavertown Neck Oil", 2.75m, "Morrisons", Quantity: 4),
        };
        var raw = new PriceCheckResult(
            [new(0, "Maltesers 100g", 1.50m, "Morrisons", 1m, "Asda", null, null)], null);

        var repaired = ModelOutputValidator.Repair(requested, raw).Items;

        Assert.Equal(2, repaired.Count);
        var missing = repaired.Single(i => i.Index == 1);
        Assert.Equal(PriceCheckOutcome.Unchecked, missing.Outcome);
        Assert.Equal("Beavertown Neck Oil", missing.Name);
        Assert.Equal(4, missing.Quantity);
        Assert.Null(missing.BestPrice);
    }

    [Fact]
    public void Price_repair_prefers_a_duplicate_that_carries_a_price()
    {
        var requested = new[] { new BrandedItemForCheck(0, "Maltesers 100g", 1.50m, "Morrisons") };
        var raw = new PriceCheckResult(
        [
            new(0, "Maltesers 100g", 1.50m, "Morrisons", null, null, null, null),
            new(0, "Maltesers 100g", 1.50m, "Morrisons", 1.10m, "Aldi", null, null),
        ], null);

        var item = Assert.Single(ModelOutputValidator.Repair(requested, raw).Items);
        Assert.Equal(1.10m, item.BestPrice);
        Assert.Equal("Aldi", item.BestPriceStore);
    }

    [Fact]
    public void Price_repair_derives_an_outcome_for_every_item()
    {
        var requested = new[]
        {
            new BrandedItemForCheck(0, "Cheaper", 2.00m, "Waitrose"),
            new BrandedItemForCheck(1, "Best already", 1.00m, "Waitrose"),
            new BrandedItemForCheck(2, "Unfindable", 3.00m, "Waitrose"),
        };
        var raw = new PriceCheckResult(
        [
            new(0, "Cheaper", 2.00m, "Waitrose", 1.50m, "Asda", null, null),
            new(1, "Best already", 1.00m, "Waitrose", 1.20m, "Asda", null, null),
            new(2, "Unfindable", 3.00m, "Waitrose", null, null, null, null),
        ], null);

        var repaired = ModelOutputValidator.Repair(requested, raw).Items;

        Assert.Equal(PriceCheckOutcome.CheaperElsewhere, repaired[0].Outcome);
        Assert.Equal(0.50m, repaired[0].Saving);
        Assert.Equal(PriceCheckOutcome.AlreadyBest, repaired[1].Outcome);
        Assert.Equal(-0.20m, repaired[1].Saving); // price kept even though it isn't a saving
        Assert.Equal(PriceCheckOutcome.NotFound, repaired[2].Outcome);
    }

    [Fact]
    public void Price_repair_drops_a_same_store_same_price_no_source_result()
    {
        // The "every row £0.00 saving at Morrisons" fabrication failure mode: the model claims a
        // "best price" identical to what was paid, at the receipt's own retailer, with no source.
        var requested = new[] { new BrandedItemForCheck(0, "KTC Chick Peas", 0.52m, "Morrisons") };
        var raw = new PriceCheckResult(
            [new(0, "KTC Chick Peas", 0.52m, "Morrisons", 0.52m, "Morrisons", null, null)], null);

        var item = Assert.Single(ModelOutputValidator.Repair(requested, raw).Items);

        Assert.Equal(PriceCheckOutcome.NotFound, item.Outcome);
        Assert.Null(item.BestPrice);
        Assert.Null(item.BestPriceStore);
        Assert.Null(item.Saving);
    }

    [Fact]
    public void Price_repair_drops_a_same_store_result_even_with_a_real_source_url()
    {
        // "Cheaper elsewhere" must mean a different store than the one bought at — even a genuine
        // search hit (real sourceUrl, a real different price, e.g. a stale/promo price) is not a
        // useful recommendation when it names the receipt's own retailer. Reproduces the real-world
        // case: a Morrisons receipt item coming back "cheaper at Morrisons".
        var requested = new[] { new BrandedItemForCheck(0, "Yellow Tail Shiraz", 8.50m, "Morrisons") };
        var raw = new PriceCheckResult(
            [new(0, "Yellow Tail Shiraz", 8.50m, "Morrisons", 8.25m, "Morrisons", null, null,
                SourceUrl: "https://www.morrisons.com/p/yellow-tail-shiraz")], null);

        var item = Assert.Single(ModelOutputValidator.Repair(requested, raw).Items);

        Assert.Equal(PriceCheckOutcome.NotFound, item.Outcome);
        Assert.Null(item.BestPrice);
        Assert.Null(item.BestPriceStore);
        Assert.Null(item.Saving);
    }

    [Fact]
    public void Price_repair_now_allows_a_tesco_price_at_a_different_store()
    {
        // Tesco was previously hard-excluded; it's now an allowed comparison/recommendation store
        // like any other, so a genuinely cheaper Tesco price at a different retailer is kept.
        var requested = new[] { new BrandedItemForCheck(0, "Maltesers 100g", 1.50m, "Morrisons") };
        var raw = new PriceCheckResult(
            [new(0, "Maltesers 100g", 1.50m, "Morrisons", 1.00m, "Tesco", null, null, SourceUrl: "https://www.tesco.com/x")], null);

        var item = Assert.Single(ModelOutputValidator.Repair(requested, raw).Items);

        Assert.Equal(1.00m, item.BestPrice);
        Assert.Equal("Tesco", item.BestPriceStore);
        Assert.Equal(PriceCheckOutcome.CheaperElsewhere, item.Outcome);
    }

    [Fact]
    public void Extraction_repair_clears_an_implausibly_old_receipt_date()
    {
        var extraction = new ReceiptExtraction(
            "Morrisons", new DateOnly(2023, 1, 1), Items, 10m, 10m, null, true, 0.9, null);

        var repaired = ModelOutputValidator.Repair(extraction, today: new DateOnly(2026, 7, 16));

        Assert.Null(repaired.ReceiptDate);
        Assert.Contains("implausible", repaired.Notes);
    }

    [Fact]
    public void Extraction_repair_clears_a_receipt_date_more_than_a_day_in_the_future()
    {
        var extraction = new ReceiptExtraction(
            "Morrisons", new DateOnly(2026, 7, 20), Items, 10m, 10m, null, true, 0.9, null);

        var repaired = ModelOutputValidator.Repair(extraction, today: new DateOnly(2026, 7, 16));

        Assert.Null(repaired.ReceiptDate);
    }

    [Fact]
    public void Extraction_repair_keeps_a_plausible_recent_receipt_date()
    {
        var extraction = new ReceiptExtraction(
            "Morrisons", new DateOnly(2026, 6, 20), Items, 10m, 10m, null, true, 0.9, null);

        var repaired = ModelOutputValidator.Repair(extraction, today: new DateOnly(2026, 7, 16));

        Assert.Equal(new DateOnly(2026, 6, 20), repaired.ReceiptDate);
    }

    [Fact]
    public void Extraction_repair_keeps_a_date_exactly_one_day_in_the_future()
    {
        var extraction = new ReceiptExtraction(
            "Morrisons", new DateOnly(2026, 7, 17), Items, 10m, 10m, null, true, 0.9, null);

        var repaired = ModelOutputValidator.Repair(extraction, today: new DateOnly(2026, 7, 16));

        Assert.Equal(new DateOnly(2026, 7, 17), repaired.ReceiptDate);
    }
}
