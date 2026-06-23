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
}
