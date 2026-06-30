using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Ledger;

/// <summary>
/// Compares the items on the receipt being analysed against <em>your own</em> purchase history and
/// flags anything you've bought cheaper before. This is distinct from the market price-check (which
/// asks what supermarkets charge today): here the benchmark is what <em>you</em> actually paid.
/// </summary>
public static class PersonalPriceHistoryBuilder
{
    /// <summary>How far back a previous purchase can be and still count as a fair comparison.</summary>
    public const int LookbackDays = 180;

    /// <summary>Ignore trivially-small differences (rounding / weight wobble on loose produce).</summary>
    public const decimal MinSaving = 0.10m;

    /// <summary>
    /// For each current item, find the lowest unit price you paid for the same product within the
    /// lookback window — excluding the receipt being analysed (<paramref name="excludeSource"/>) so it
    /// never compares a shop against itself. Returns only items where the previous price was cheaper by
    /// at least <see cref="MinSaving"/>, dearest-saving first.
    /// </summary>
    public static IReadOnlyList<PersonalPriceComparison> Build(
        PurchaseHistoryData history,
        IReadOnlyList<RawItem> currentItems,
        string currentRetailer,
        DateOnly today,
        string? excludeSource)
    {
        var cutoff = today.AddDays(-LookbackDays);

        var priorByKey = history.Records
            .Where(r => r.Source != excludeSource && r.Date >= cutoff && r.UnitPrice > 0)
            .GroupBy(r => r.Key);

        var cheapest = priorByKey.ToDictionary(
            g => g.Key,
            g => g.OrderBy(r => r.UnitPrice).First());

        var results = new List<PersonalPriceComparison>();
        var seen = new HashSet<string>();

        foreach (var item in currentItems)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.UnitPrice <= 0) continue;
            var key = KeyNormaliser.Normalise(item.Name);
            if (!seen.Add(key)) continue; // one row per product even if it appears twice on the receipt
            if (!cheapest.TryGetValue(key, out var best)) continue;

            var saving = item.UnitPrice - best.UnitPrice;
            if (saving < MinSaving) continue;

            results.Add(new PersonalPriceComparison(
                item.Name, item.UnitPrice, currentRetailer,
                best.UnitPrice, best.Retailer, best.Date, saving));
        }

        return results.OrderByDescending(r => r.Saving).ToList();
    }
}
