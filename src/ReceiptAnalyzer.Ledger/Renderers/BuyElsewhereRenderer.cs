using System.Text;

namespace ReceiptAnalyzer.Ledger.Renderers;

public static class BuyElsewhereRenderer
{
    public static string Render(LedgerData ledger)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Buy Elsewhere");
        sb.AppendLine();
        sb.AppendLine("Items regularly purchased at Waitrose or Sainsbury's that are consistently cheaper elsewhere.");
        sb.AppendLine();
        sb.AppendLine("| Item | Store Paid | Price Paid | Best Price | Where | Saving | Last Seen |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var e in ledger.BuyElsewhere.OrderByDescending(x => x.Saving))
        {
            var bold = e.Saving >= 0.30m ? "**" : "";
            sb.AppendLine($"| {e.Item} | {e.StorePaid} | £{e.PricePaid:F2} | £{e.BestPrice:F2} | {e.Where} | {bold}£{e.Saving:F2}{bold} | {FormatDate(e.LastSeen)} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatDate(string iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", out var d) ? d.ToString("d MMM yy") : iso;
}
