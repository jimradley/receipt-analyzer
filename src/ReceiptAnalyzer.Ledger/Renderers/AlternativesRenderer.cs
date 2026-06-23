using System.Text;

namespace ReceiptAnalyzer.Ledger.Renderers;

public static class AlternativesRenderer
{
    public static string Render(LedgerData ledger)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Shopping Alternatives");
        sb.AppendLine();
        sb.AppendLine("Cleaner swaps for regularly purchased processed or American-brand items.");

        foreach (var e in ledger.Alternatives)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            if (e.DetailMarkdown is not null)
            {
                sb.AppendLine(e.DetailMarkdown.Trim());
            }
            else
            {
                sb.AppendLine($"## {e.Item}");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(e.Reason))
                    sb.AppendLine($"**Why swap:** {e.Reason}");
                if (!string.IsNullOrWhiteSpace(e.SwapSuggestion))
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Swap suggestion:** {e.SwapSuggestion}");
                }
                sb.AppendLine();
                sb.AppendLine($"_Last seen: {FormatDate(e.LastSeen)}_");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatDate(string iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", out var d) ? d.ToString("d MMM yyyy") : iso;
}
