using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Reports;

namespace ReceiptAnalyzer.Tests;

public class ReceiptMathTests
{
    private static ReceiptExtraction Extraction(
        decimal[] lineTotals, decimal? subtotal = null, decimal? total = null, decimal? savings = null)
    {
        var items = lineTotals
            .Select((lt, i) => new RawItem($"Item {i}", 1m, lt, lt))
            .ToList();
        return new ReceiptExtraction("Asda", null, items, subtotal, total, savings,
            IsReceipt: true, Confidence: 1.0, Notes: null);
    }

    [Fact]
    public void Reconciles_against_printed_subtotal_within_tolerance()
    {
        var check = ReceiptMath.Check(Extraction([1.00m, 2.50m, 0.49m], subtotal: 4.00m));
        Assert.True(check.Reconciles);          // 3.99 vs 4.00, delta 0.01 ≤ 0.05
        Assert.Equal(3.99m, check.SumOfItems);
    }

    [Fact]
    public void Flags_subtotal_mismatch_beyond_tolerance()
    {
        var check = ReceiptMath.Check(Extraction([1.00m, 2.00m], subtotal: 5.00m));
        Assert.False(check.Reconciles);         // 3.00 vs 5.00
        Assert.Equal(-2.00m, check.Delta);
        Assert.Contains("possible mis-read", check.Summary);
    }

    [Fact]
    public void Adds_savings_back_when_only_total_is_printed()
    {
        // items 10.00; paid 9.00 after 1.00 savings → reconciles
        var check = ReceiptMath.Check(Extraction([6.00m, 4.00m], total: 9.00m, savings: 1.00m));
        Assert.True(check.Reconciles);
        Assert.Equal("printed total + savings", check.ReferenceLabel);
        Assert.Equal(10.00m, check.Reference);
    }

    [Fact]
    public void Prefers_subtotal_over_total_when_both_present()
    {
        var check = ReceiptMath.Check(Extraction([4.00m], subtotal: 4.00m, total: 3.50m, savings: 0.50m));
        Assert.Equal("printed subtotal", check.ReferenceLabel);
        Assert.True(check.Reconciles);
    }

    [Fact]
    public void Reports_unknown_when_no_totals_printed()
    {
        var check = ReceiptMath.Check(Extraction([1.23m, 4.56m]));
        Assert.Null(check.Reconciles);
        Assert.Null(check.Delta);
        Assert.Equal(5.79m, check.SumOfItems);
        Assert.Contains("no printed total", check.Summary);
    }
}
