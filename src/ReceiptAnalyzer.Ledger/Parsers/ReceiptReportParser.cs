using System.Globalization;
using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Ledger.Parsers;

public sealed record ParsedReceiptItem(
    string Name, decimal Quantity, decimal UnitPrice,
    int? NovaLevel = null, bool? IsAmerican = null, string? ParentCompany = null);

public sealed record ParsedReceipt(string Retailer, DateOnly Date, IReadOnlyList<ParsedReceiptItem> Items);

/// <summary>
/// Extracts the retailer, date and line items from a single rendered per-receipt report
/// (see <c>ReportRenderer</c>): retailer + date from the <c>## Receipt: {Retailer} | {d MMMM yyyy}</c>
/// heading, items from the <c>### Items</c> table (<c>| Item | Qty | £Unit | … |</c>). Used only to
/// back-fill the durable purchase history from reports written before that history existed.
/// </summary>
public static class ReceiptReportParser
{
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");
    private static readonly Regex ReceiptHeader = new(@"^##\s*Receipt:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex PriceRegex = new(@"£\s*(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>
    /// Parses one report. <paramref name="fallbackDate"/> (typically derived from the report's
    /// <c>dd-MMMM-yy</c> filename) is used when the heading date can't be read. Returns null when the
    /// report has no Items table or no usable date.
    /// </summary>
    public static ParsedReceipt? Parse(string markdown, DateOnly? fallbackDate)
    {
        string retailer = "Unknown";
        DateOnly? headingDate = null;

        var items = new List<ParsedReceiptItem>();
        var inItems = false;     // seen the "### Items" heading
        var inTable = false;     // inside its table header
        var pastSeparator = false;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var header = ReceiptHeader.Match(line);
            if (header.Success)
            {
                ParseReceiptHeading(header.Groups[1].Value, ref retailer, ref headingDate);
                continue;
            }

            if (line.StartsWith("### Items", StringComparison.OrdinalIgnoreCase))
            {
                inItems = true;
                continue;
            }

            if (!inItems) continue;

            // The Items table ends at the next section / rule.
            if (line.StartsWith("---") || (line.StartsWith("###") && !line.StartsWith("### Items")))
            {
                inItems = inTable = pastSeparator = false;
                continue;
            }

            if (!line.StartsWith('|'))
            {
                inTable = pastSeparator = false;
                continue;
            }

            if (line.Contains("Item", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Qty", StringComparison.OrdinalIgnoreCase))
            {
                inTable = true;
                pastSeparator = false;
                continue;
            }

            if (line.Contains("---"))
            {
                pastSeparator = true;
                continue;
            }

            if (!inTable || !pastSeparator) continue;

            // [0]="" [1]=Name [2]=Qty [3]=£Unit [4]=NOVA [5]=flag [6]=Notes [7]=""
            var cells = line.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 4) continue;

            var name = cells[1];
            if (name.Length == 0) continue;

            var qty = ParseQuantity(cells[2]);
            var unit = ExtractPrice(cells[3]);
            if (unit is null) continue;

            // NOVA cell is rendered as "2", "**4** 🚩" or "—"; the US flag cell as "**🇺🇸 Company**" or "".
            int? nova = cells.Length > 4 ? ParseNova(cells[4]) : null;
            var (isAmerican, parent) = cells.Length > 5 ? ParseUsFlag(cells[5]) : (null, null);

            items.Add(new ParsedReceiptItem(name, qty, unit.Value, nova, isAmerican, parent));
        }

        var date = headingDate ?? fallbackDate;
        if (items.Count == 0 || date is null) return null;

        return new ParsedReceipt(retailer, date.Value, items);
    }

    private static void ParseReceiptHeading(string rest, ref string retailer, ref DateOnly? date)
    {
        // "{Retailer} | {d MMMM yyyy} | Paid: £…"
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length >= 1 && parts[0].Length > 0) retailer = parts[0];
        if (parts.Length >= 2 &&
            DateTime.TryParseExact(parts[1], "d MMMM yyyy", Uk, DateTimeStyles.None, out var d))
            date = DateOnly.FromDateTime(d);
    }

    private static decimal ParseQuantity(string cell)
        => decimal.TryParse(cell, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) && q > 0 ? q : 1m;

    private static decimal? ExtractPrice(string cell)
    {
        var m = PriceRegex.Match(cell);
        return m.Success ? decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static int? ParseNova(string cell)
    {
        var m = Regex.Match(cell, @"[1-4]");
        return m.Success ? int.Parse(m.Value) : null;
    }

    /// <summary>"**🇺🇸 Coca-Cola Co**" → (true, "Coca-Cola Co"); an empty cell → (null, null).</summary>
    private static (bool? IsAmerican, string? Parent) ParseUsFlag(string cell)
    {
        if (cell.Length == 0 || cell == "—") return (null, null);
        if (!cell.Contains("🇺🇸")) return (false, null);
        var parent = cell.Replace("*", "").Replace("🇺🇸", "").Trim();
        return (true, parent.Length > 0 ? parent : null);
    }
}
