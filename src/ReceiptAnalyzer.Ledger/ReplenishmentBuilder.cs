namespace ReceiptAnalyzer.Ledger;

/// <summary>One regularly-bought staple with its learned cadence and how due it is to run out.</summary>
public sealed record StaplePrediction(
    string Key,
    string Item,
    int PurchaseCount,
    int CadenceDays,
    DateOnly LastPurchased,
    int DueInDays,        // negative = overdue
    string Status,        // "Overdue" | "DueSoon" | "OnTrack"
    decimal LastUnitPrice,
    IReadOnlyList<string> Stores,
    double Regularity     // 0..1; higher = more evenly spaced buys (more trustworthy)
);

public sealed record ReplenishmentResult(
    IReadOnlyList<StaplePrediction> Staples,
    int InsufficientDataItems);

/// <summary>
/// Turns the durable purchase history into per-staple replenishment predictions. For each product
/// bought on at least <see cref="MinPurchases"/> distinct days, the typical gap between buys (the
/// median, robust to the odd irregular shop) is the cadence; an item is overdue once that long has
/// passed since the last purchase. Rarely-bought items can't be predicted and are only counted.
/// </summary>
public static class ReplenishmentBuilder
{
    public const int MinPurchases = 3;

    public static ReplenishmentResult Build(PurchaseHistoryData history, DateOnly today)
    {
        var staples = new List<StaplePrediction>();
        var insufficient = 0;

        foreach (var group in history.Records.GroupBy(r => r.Key))
        {
            var dates = group.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
            if (dates.Count < MinPurchases) { insufficient++; continue; }

            var gaps = new List<int>();
            for (var i = 1; i < dates.Count; i++)
                gaps.Add(dates[i].DayNumber - dates[i - 1].DayNumber);

            var cadence = Median(gaps);
            var last = dates[^1];
            var daysSinceLast = today.DayNumber - last.DayNumber;
            var dueInDays = cadence - daysSinceLast;

            var soonWindow = Math.Max(2, cadence / 4);
            var status = dueInDays < 0 ? "Overdue"
                       : dueInDays <= soonWindow ? "DueSoon"
                       : "OnTrack";

            var mostRecent = group.OrderByDescending(r => r.Date).First();
            var stores = group
                .GroupBy(r => r.Retailer)
                .OrderByDescending(g => g.Max(r => r.Date))
                .Select(g => g.Key)
                .ToList();

            staples.Add(new StaplePrediction(
                group.Key, mostRecent.Item, dates.Count, cadence, last, dueInDays, status,
                mostRecent.UnitPrice, stores, Regularity(gaps)));
        }

        var ordered = staples
            .OrderBy(s => s.DueInDays)
            .ThenBy(s => s.Item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReplenishmentResult(ordered, insufficient);
    }

    private static int Median(IReadOnlyList<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        return Math.Max(1, (int)Math.Round(median));
    }

    /// <summary>1 − coefficient of variation of the gaps, clamped to 0..1. Evenly-spaced buys → ~1.</summary>
    private static double Regularity(IReadOnlyList<int> gaps)
    {
        if (gaps.Count == 0) return 0;
        var mean = gaps.Average();
        if (mean <= 0) return 0;
        var variance = gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count;
        var cv = Math.Sqrt(variance) / mean;
        return Math.Clamp(1.0 - cv, 0.0, 1.0);
    }
}
