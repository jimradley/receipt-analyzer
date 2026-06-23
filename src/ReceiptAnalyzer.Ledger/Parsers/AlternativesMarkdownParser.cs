using System.Text;

namespace ReceiptAnalyzer.Ledger.Parsers;

public static class AlternativesMarkdownParser
{
    public static (List<AlternativeEntry> Entries, List<string> Unparseable) Parse(string markdown)
    {
        var entries = new List<AlternativeEntry>();
        var unparseable = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");

        foreach (var block in SplitBlocks(markdown))
        {
            var trimmed = block.Trim();
            if (!trimmed.StartsWith("## ")) continue;

            try
            {
                var firstLine = trimmed.Split('\n')[0];
                var itemName = ExtractItemName(firstLine);
                if (string.IsNullOrWhiteSpace(itemName)) { unparseable.Add(firstLine); continue; }

                var key = KeyNormaliser.Normalise(itemName);
                var reason = ExtractReason(trimmed);

                entries.Add(new AlternativeEntry(key, itemName, reason, null, trimmed, today));
            }
            catch
            {
                unparseable.Add(trimmed[..Math.Min(80, trimmed.Length)]);
            }
        }

        return (entries, unparseable);
    }

    private static IEnumerable<string> SplitBlocks(string markdown)
    {
        var current = new StringBuilder();
        foreach (var line in markdown.Split('\n'))
        {
            if (line.Trim() == "---")
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.AppendLine(line);
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static string ExtractItemName(string headerLine)
    {
        var s = headerLine.TrimStart('#', ' ');
        var arrowIdx = s.IndexOf(" → ", StringComparison.Ordinal);
        if (arrowIdx >= 0) return s[..arrowIdx].Trim();
        var dashIdx = s.IndexOf(" — ", StringComparison.Ordinal);
        if (dashIdx >= 0) return s[..dashIdx].Trim();
        return s.Trim();
    }

    private static string ExtractReason(string section)
    {
        var whyLine = section.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("**Why swap:**"));
        if (whyLine is null) return "";
        return whyLine.Replace("**Why swap:**", "").Trim();
    }
}
