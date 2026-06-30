using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Ledger;

/// <summary>One rated wine available at one allowed store. <see cref="Price"/> is the raw cell text
/// (e.g. "£11.00 (£8.50 w. Nectar)") — kept verbatim for display.</summary>
public sealed record WineRecommendation(string Store, string Grape, string Wine, string Price, string Tier);

/// <summary>
/// Reads the personal Wine project (one markdown file per grape/style, each with rating-tier sections
/// and a Wine | Retailer | Price table) and flattens it into per-store recommendations. Only the
/// "recommended + decent" tiers are kept (CLASS / TOM'S PICK / RECOMMENDED and PASS / THE REST);
/// ARSE and UNRATED are dropped, as are wines whose retailer isn't an allowed grocery store.
/// </summary>
public sealed class WineCatalog
{
    // Section header → display tier. Headers not listed here (ARSE, UNRATED, prose sections) are skipped.
    private static readonly Dictionary<string, string> TierByHeader = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CLASS"] = "Top pick", ["TOM'S PICK"] = "Top pick", ["RECOMMENDED"] = "Top pick",
        ["PASS"] = "Also good", ["THE REST"] = "Also good",
    };

    /// <summary>Tier sort order — "Top pick" before "Also good".</summary>
    public static int TierRank(string tier) => tier == "Top pick" ? 0 : 1;

    private static readonly Regex H1 = new(@"^#\s+(.*?)(?:\s+Recommendations)?\s*$", RegexOptions.Compiled);
    private static readonly Regex H2 = new(@"^##\s+(.*?)\s*$", RegexOptions.Compiled);

    private readonly string _dir;

    public WineCatalog(string dir) => _dir = dir;

    public IReadOnlyList<WineRecommendation> Load()
    {
        if (!Directory.Exists(_dir)) return [];

        var recs = new List<WineRecommendation>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.md", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(file), "CLAUDE.md", StringComparison.OrdinalIgnoreCase)) continue;
            ParseFile(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), recs);
        }
        return recs;
    }

    private static void ParseFile(string markdown, string fallbackGrape, List<WineRecommendation> recs)
    {
        var grape = fallbackGrape;
        string? tier = null;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("## "))
                {
                    var header = H2.Match(line).Groups[1].Value;
                    tier = TierByHeader.GetValueOrDefault(header); // null → section skipped
                }
                else if (line.StartsWith("# "))
                {
                    var title = H1.Match(line).Groups[1].Value.Trim();
                    if (title.Length > 0) grape = title;
                }
                continue;
            }

            if (tier is null || !line.StartsWith('|')) continue;

            var cells = line.Split('|').Select(c => c.Trim()).ToArray();
            // [0]="" [1]=Wine [2]=Retailer [3]=Price (extra columns ignored)
            if (cells.Length < 4) continue;
            if (cells[2].Equals("Retailer", StringComparison.OrdinalIgnoreCase)) continue; // column header
            if (cells[1].StartsWith("---") || cells[2].StartsWith("---")) continue;          // separator

            var wine = cells[1];
            var price = cells[3];
            if (wine.Length == 0) continue;

            foreach (var store in StoreCatalog.ExtractAllowed(cells[2]))
                recs.Add(new WineRecommendation(store, grape, wine, price, tier));
        }
    }
}
