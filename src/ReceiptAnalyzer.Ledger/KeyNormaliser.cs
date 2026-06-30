using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Ledger;

public static class KeyNormaliser
{
    private static readonly Regex NonAlphanumeric = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingCount = new(@"^\s*\d+(?:\.\d+)?\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingStoreMarker = new(@"^\s*m\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PackSize = new(
        @"(?<!\w)(?:\d+\s*x\s*)?\d+(?:[.,]\d+)?\s*(?:kg|g|mg|l|ml|cl|pack|pk)(?!\w)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Normalise(string input)
    {
        var s = input.ToLowerInvariant();
        s = NonAlphanumeric.Replace(s, " ");
        s = Whitespace.Replace(s.Trim(), "-");
        return s;
    }

    /// <summary>Stable product identity, preferring the classifier's expanded name when available.</summary>
    public static string Product(string receiptName, string? canonicalName = null)
    {
        var source = string.IsNullOrWhiteSpace(canonicalName) ? receiptName : canonicalName;
        source = LeadingCount.Replace(source, "");
        source = LeadingStoreMarker.Replace(source, "");
        source = PackSize.Replace(source, " ");
        return Normalise(source);
    }

    /// <summary>Pack-size discriminator used by price comparisons; null means size was not present.</summary>
    public static string? Pack(string input)
    {
        var match = PackSize.Match(input);
        return match.Success ? Normalise(match.Value.Replace(',', '.')) : null;
    }

    public static string PriceKey(string name) => $"{Product(name)}|{Pack(name) ?? "unknown-size"}";
}
