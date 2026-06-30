using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Ledger;

/// <summary>
/// The user's allowed grocery stores and the canonicalisation of the free-text store names that
/// appear in the ledger "Where" column and the Wine project's retailer cells. Anything outside the
/// allowed set — notably Tesco (never recommend; not near the user), plus M&S / Majestic / Co-op —
/// maps to <c>null</c> and is dropped from the per-store shopping list.
/// </summary>
public static class StoreCatalog
{
    /// <summary>Canonical allowed store names, in a sensible display order.</summary>
    public static readonly IReadOnlyList<string> Allowed = new[]
    {
        "Asda", "Sainsbury's", "Morrisons", "Waitrose", "Ocado", "Aldi", "Lidl", "B&M", "Poundland"
    };

    // Lower-cased aliases → canonical name. Only allowed stores appear here; unknown/forbidden
    // names (tesco, m&s, majestic, co-op, …) deliberately have no entry, so Canonical returns null.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["asda"] = "Asda",
        ["sainsbury's"] = "Sainsbury's", ["sainsburys"] = "Sainsbury's", ["sainsbury"] = "Sainsbury's",
        ["morrisons"] = "Morrisons", ["morrison's"] = "Morrisons",
        ["waitrose"] = "Waitrose", ["waitrose & partners"] = "Waitrose",
        ["ocado"] = "Ocado",
        ["aldi"] = "Aldi",
        ["lidl"] = "Lidl",
        ["b&m"] = "B&M", ["bm"] = "B&M",
        ["poundland"] = "Poundland",
    };

    // Strip a trailing annotation: " (Clubcard)", " — any 3 for £9", " - offer", "(offer)".
    private static readonly Regex Parenthetical = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex DashAnnotation = new(@"\s[—–]\s.*$|\s-\s.*$", RegexOptions.Compiled);

    /// <summary>Canonical allowed-store name for a single raw store token, or null if not allowed.</summary>
    public static string? Canonical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = DashAnnotation.Replace(Parenthetical.Replace(raw, " "), "").Trim();
        return Aliases.TryGetValue(cleaned, out var name) ? name : null;
    }

    /// <summary>
    /// All allowed canonical stores mentioned in a free-text cell. Handles multi-store cells such as
    /// "Morrisons / Ocado", "Asda / B&M" and "Tesco / Asda" (Tesco dropped), plus per-store annotations.
    /// </summary>
    public static IReadOnlyList<string> ExtractAllowed(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return [];
        var result = new List<string>();
        foreach (var token in cell.Split(['/', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var canonical = Canonical(token);
            if (canonical is not null && !result.Contains(canonical)) result.Add(canonical);
        }
        return result;
    }
}
