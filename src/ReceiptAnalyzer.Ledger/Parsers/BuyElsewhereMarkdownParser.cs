using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Ledger.Parsers;

public static class BuyElsewhereMarkdownParser
{
    private static readonly Regex PriceRegex = new(@"£(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> MonthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jan"] = 1, ["Feb"] = 2, ["Mar"] = 3, ["Apr"] = 4,
        ["May"] = 5, ["Jun"] = 6, ["Jul"] = 7, ["Aug"] = 8,
        ["Sep"] = 9, ["Oct"] = 10, ["Nov"] = 11, ["Dec"] = 12
    };

    public static (List<BuyElsewhereEntry> Entries, List<string> Unparseable) Parse(string markdown)
    {
        var entries = new List<BuyElsewhereEntry>();
        var unparseable = new List<string>();

        var inTable = false;
        var pastSeparator = false;

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|'))
            {
                inTable = false;
                pastSeparator = false;
                continue;
            }

            if (trimmed.Contains("Store Paid"))
            {
                inTable = true;
                pastSeparator = false;
                continue;
            }

            if (trimmed.Contains("|---|"))
            {
                pastSeparator = true;
                continue;
            }

            if (!inTable || !pastSeparator) continue;

            var cells = trimmed.Split('|').Select(c => c.Trim()).ToArray();
            // [0]="" [1]=Item [2]=StorePaid [3]=PricePaid [4]=BestPrice [5]=Where [6]=Saving [7]=LastSeen [8]=""
            if (cells.Length < 8) { unparseable.Add(trimmed); continue; }

            try
            {
                var item = cells[1];
                var storePaid = cells[2];
                var pricePaid = ExtractPrice(cells[3]);
                var bestPrice = ExtractPrice(cells[4]);
                var where = cells[5];
                var saving = ExtractPrice(cells[6]);
                var lastSeen = ParseDate(cells[7]);

                if (pricePaid is null || bestPrice is null || saving is null || lastSeen is null)
                {
                    unparseable.Add(trimmed);
                    continue;
                }

                entries.Add(new BuyElsewhereEntry(
                    KeyNormaliser.Normalise(item), item, storePaid,
                    pricePaid.Value, bestPrice.Value, where, saving.Value, lastSeen));
            }
            catch
            {
                unparseable.Add(trimmed);
            }
        }

        return (entries, unparseable);
    }

    private static decimal? ExtractPrice(string cell)
    {
        var m = PriceRegex.Match(cell);
        return m.Success ? decimal.Parse(m.Groups[1].Value) : null;
    }

    private static string? ParseDate(string cell)
    {
        var parts = cell.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var day)) return null;
        if (!MonthMap.TryGetValue(parts[1], out var month)) return null;
        if (!int.TryParse(parts[2], out var year2)) return null;
        var year = year2 < 100 ? 2000 + year2 : year2;
        return new DateOnly(year, month, day).ToString("yyyy-MM-dd");
    }
}
