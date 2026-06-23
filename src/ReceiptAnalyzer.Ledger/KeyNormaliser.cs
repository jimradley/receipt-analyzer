using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Ledger;

public static class KeyNormaliser
{
    private static readonly Regex NonAlphanumeric = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalise(string input)
    {
        var s = input.ToLowerInvariant();
        s = NonAlphanumeric.Replace(s, " ");
        s = Whitespace.Replace(s.Trim(), "-");
        return s;
    }
}
