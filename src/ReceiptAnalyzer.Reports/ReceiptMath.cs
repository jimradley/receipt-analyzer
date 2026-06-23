using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Reports;

/// <summary>Outcome of reconciling the summed line items against the receipt's printed totals.</summary>
public sealed record ReceiptMathCheck(
    decimal SumOfItems,
    decimal? Reference,        // the printed figure compared against (subtotal, or total+savings)
    string ReferenceLabel,     // human label for what Reference represents
    decimal? Delta,            // SumOfItems - Reference; null when nothing to compare to
    bool? Reconciles,          // null when no printed total was available
    string Summary);

public static class ReceiptMath
{
    /// <summary>Items are allowed to differ from the printed figure by this much (rounding / weighed goods).</summary>
    public const decimal Tolerance = 0.05m;

    public static ReceiptMathCheck Check(ReceiptExtraction extraction)
    {
        var sum = extraction.Items.Sum(i => i.LineTotal);
        var savings = extraction.Savings ?? 0m;

        // Prefer the printed subtotal (pre-discount) — it should equal the raw item sum.
        if (extraction.PrintedSubtotal is { } subtotal)
        {
            var delta = sum - subtotal;
            var ok = Math.Abs(delta) <= Tolerance;
            return new ReceiptMathCheck(sum, subtotal, "printed subtotal", delta, ok,
                ok
                    ? $"Items sum to £{sum:F2}, matching the printed subtotal."
                    : $"Items sum to £{sum:F2} but the printed subtotal is £{subtotal:F2} (delta £{delta:F2}) — possible mis-read.");
        }

        // Otherwise reconcile against the total, adding back any printed savings.
        if (extraction.PrintedTotal is { } total)
        {
            var expected = total + savings;
            var delta = sum - expected;
            var ok = Math.Abs(delta) <= Tolerance;
            var savingsNote = savings > 0 ? $" (total £{total:F2} + £{savings:F2} savings)" : "";
            return new ReceiptMathCheck(sum, expected, "printed total + savings", delta, ok,
                ok
                    ? $"Items sum to £{sum:F2}, matching the printed total{savingsNote}."
                    : $"Items sum to £{sum:F2} but expected £{expected:F2}{savingsNote} (delta £{delta:F2}) — possible mis-read.");
        }

        return new ReceiptMathCheck(sum, null, "none", null, null,
            $"Items sum to £{sum:F2}; no printed total on the receipt to reconcile against.");
    }
}
