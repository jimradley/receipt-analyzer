using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Agent;

/// <summary>
/// Deterministic safety net for a classification the vision model gets wrong reliably: plain alcoholic
/// drinks (wine, prosecco, champagne, spirits) are fermented/distilled — NOVA 1 — yet gpt-4o keeps
/// tagging them NOVA 4 (ultra-processed), which then triggers spurious swap suggestions and
/// repeat-offender flags. This forces recognised alcohol to NOVA 1 after classification, independent of
/// the model. The term list is deliberately high-precision (grape varietals + unambiguous drink words)
/// to avoid mislabelling foods — e.g. "beer" is excluded because ginger/root beer are soft drinks, and
/// short ambiguous words like "port"/"ale"/"rum" are excluded so cheeses/snacks aren't caught.
/// </summary>
public static class AlcoholNovaGuard
{
    private static readonly string[] Terms =
    {
        "wine", "shiraz", "syrah", "merlot", "malbec", "cabernet", "sauvignon", "chardonnay",
        "pinot", "rioja", "prosecco", "champagne", "chablis", "tempranillo", "grenache",
        "zinfandel", "viognier", "gewurztraminer", "chenin", "primitivo", "montepulciano",
        "lager", "cider", "whisky", "whiskey", "bourbon", "brandy", "cognac", "tequila",
        "vodka", "vermouth", "75cl", "70cl",
    };

    private static readonly Regex Matcher = new(
        @"(?<![a-z])(" + string.Join("|", Terms) + @")(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Words that contain an alcohol term but are not the drink: "wine gums" (sweets),
    // "wine vinegar" (condiment). Their presence vetoes the match.
    private static readonly Regex Exclude = new(
        @"(?<![a-z])(gum|gums|vinegar)(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsAlcohol(string itemName) =>
        !string.IsNullOrWhiteSpace(itemName) && Matcher.IsMatch(itemName) && !Exclude.IsMatch(itemName);

    /// <summary>
    /// Returns classifications with recognised alcohol forced to NOVA 1, clearing any swap suggestion
    /// that was only there because of an (incorrect) NOVA 3/4 label. Items flagged American keep their
    /// swap. Non-alcohol classifications pass through unchanged.
    /// </summary>
    public static ItemClassifications Apply(IReadOnlyList<RawItem> items, ItemClassifications classifications)
    {
        var corrected = classifications.Items.Select(c =>
        {
            if (c.Index < 0 || c.Index >= items.Count) return c;
            if (!IsAlcohol(items[c.Index].Name)) return c;
            if (c.NovaLevel is null or 1) return c;

            // Drop a swap that existed only for the wrong NOVA label; keep it if the item is American.
            var swap = c.IsAmerican ? c.SwapSuggestion : null;
            return c with { NovaLevel = 1, SwapSuggestion = swap };
        }).ToList();

        return new ItemClassifications(corrected);
    }
}
