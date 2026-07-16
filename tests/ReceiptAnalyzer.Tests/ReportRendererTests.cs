using ReceiptAnalyzer.Reports;

namespace ReceiptAnalyzer.Tests;

public class ReportRendererTests
{
    private static readonly string Report = ReportRenderer.Render(TestData.SampleResult());

    [Fact]
    public void Renders_header_with_retailer_and_paid_total()
    {
        Assert.Contains("# Shopping Report — 20 June 2026", Report);
        Assert.Contains("## Receipt: Morrisons | 20 June 2026 | Paid: £4.39", Report);
    }

    [Fact]
    public void Flags_nova_3_and_4_items_with_emoji()
    {
        Assert.Contains("**4** 🚩", Report);   // Maltesers
        Assert.Contains("**3** 🚩", Report);   // own-label cheddar
    }

    [Fact]
    public void Lists_american_brand_with_parent_company()
    {
        Assert.Contains("**Maltesers 100g** — Mars (USA) 🇺🇸", Report);
        Assert.Contains("→ Swap: Try a UK-owned chocolate.", Report);
    }

    [Fact]
    public void Own_label_item_is_noted_and_not_flagged_american()
    {
        Assert.Contains("Own-label", Report);
        Assert.DoesNotContain("M Cheddar Mild 400g** — ", Report); // not in American section
    }

    [Fact]
    public void Price_check_bolds_only_savings_at_or_above_threshold()
    {
        Assert.Contains("| Maltesers 100g | £1.50 | £1.00 | Asda | **£0.50** |", Report);
        Assert.Contains("| M Cheddar Mild 400g | £2.29 | £2.20 | Aldi | £0.09 |", Report);
        Assert.Contains("Combined potential saving", Report);
    }

    [Fact]
    public void Price_check_renders_a_coverage_line()
    {
        // Sample items pre-date outcomes, so both derive as "cheaper elsewhere".
        Assert.Contains("Price-checked 2 of 2 branded item(s): 2 cheaper elsewhere.", Report);
    }

    private static string RenderWithPriceChecks(ReceiptAnalyzer.Agent.PriceCheckResult priceChecks)
    {
        var sample = TestData.SampleResult();
        return ReportRenderer.Render(sample with { PriceChecks = priceChecks });
    }

    [Fact]
    public void Price_check_accounts_for_every_outcome()
    {
        var report = RenderWithPriceChecks(new(new List<ReceiptAnalyzer.Agent.PriceCheckItem>
        {
            new(0, "Cheaper Thing", 2.00m, "Morrisons", 1.50m, "Asda", 0.50m, null,
                ReceiptAnalyzer.Agent.PriceCheckOutcome.CheaperElsewhere),
            new(1, "Best Already Thing", 1.00m, "Morrisons", 1.20m, "Asda", -0.20m, null,
                ReceiptAnalyzer.Agent.PriceCheckOutcome.AlreadyBest),
            new(2, "Unfindable Thing", 3.00m, "Morrisons", null, null, null, null,
                ReceiptAnalyzer.Agent.PriceCheckOutcome.NotFound),
            new(3, "Errored Thing", 4.00m, "Morrisons", null, null, null, null,
                ReceiptAnalyzer.Agent.PriceCheckOutcome.Unchecked),
        }, null));

        Assert.Contains(
            "Price-checked 3 of 4 branded item(s): 1 cheaper elsewhere, 1 already best price, 1 not found, 1 not checked.",
            report);
        Assert.Contains("| Cheaper Thing | £2.00 | £1.50 | Asda | **£0.50** |", report);
        Assert.Contains("Already the best price: Best Already Thing.", report);
        Assert.Contains("Couldn't price: Unfindable Thing.", report);
        Assert.Contains("Not checked (error — will retry on the next receipt): Errored Thing.", report);
        // A found-but-not-cheaper price never appears in the cheaper-elsewhere table.
        Assert.DoesNotContain("| Best Already Thing |", report);
    }

    [Fact]
    public void Price_check_combined_saving_is_quantity_aware()
    {
        var report = RenderWithPriceChecks(new(new List<ReceiptAnalyzer.Agent.PriceCheckItem>
        {
            new(0, "Multibuy Beer", 2.00m, "Morrisons", 1.50m, "Asda", 0.50m, null,
                ReceiptAnalyzer.Agent.PriceCheckOutcome.CheaperElsewhere, Quantity: 4),
        }, null));

        Assert.Contains("| Multibuy Beer | £2.00 ×4 | £1.50 | Asda | **£0.50** |", report);
        Assert.Contains("Combined potential saving (this trip, branded items only): ~£2.00", report);
    }

    [Fact]
    public void Null_price_checks_mean_nothing_was_eligible()
    {
        // Renderer treats null as "no eligible branded items" (failures now surface per item).
        var report = ReportRenderer.Render(TestData.SampleResult() with { PriceChecks = null });
        Assert.Contains("_No branded items to price-check on this receipt._", report);
    }

    [Fact]
    public void Never_recommends_tesco()
    {
        Assert.DoesNotContain("Tesco", Report);
    }

    [Fact]
    public void Renders_seasonality_origin_for_imported_produce()
    {
        Assert.Contains("Bananas Loose (Costa Rica)", Report);
    }

    [Fact]
    public void Renders_receipt_math_reconciliation_line()
    {
        // Sample items sum to 4.69, matching the printed subtotal → reconciles.
        Assert.Contains("**Receipt math:** ✅", Report);
        Assert.Contains("matching the printed subtotal", Report);
    }

    [Fact]
    public void Reconciling_receipt_has_no_reshoot_guidance()
    {
        Assert.DoesNotContain("This read looks unreliable", Report);
    }

    [Fact]
    public void Large_unreconciled_mismatch_warns_to_reshoot()
    {
        // One £5 item but a printed subtotal of £50 — a gap big enough to mean a bad photo.
        var ext = new ReceiptAnalyzer.Agent.ReceiptExtraction(
            "Sainsbury's", new DateOnly(2026, 6, 21),
            new[] { new ReceiptAnalyzer.Agent.RawItem("Mystery", 1m, 5.00m, 5.00m) },
            PrintedSubtotal: 50.00m, PrintedTotal: 50.00m, Savings: 0m,
            IsReceipt: true, Confidence: 0.5, Notes: null);
        var result = new ReceiptAnalyzer.Agent.AnalysisResult(
            ext,
            new ReceiptAnalyzer.Agent.ItemClassifications(new List<ReceiptAnalyzer.Agent.ItemClassification>()),
            null, null, new DateTimeOffset(2026, 6, 21, 9, 0, 0, TimeSpan.Zero));

        var report = ReportRenderer.Render(result);
        Assert.Contains("**Receipt math:** ⚠️", report);
        Assert.Contains("This read looks unreliable", report);
        Assert.Contains("one receipt at a time", report);
    }
}
