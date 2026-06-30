namespace ReceiptAnalyzer.Ledger;

/// <summary>Total spend in one calendar month (<c>yyyy-MM</c>), oldest first.</summary>
public sealed record MonthSpend(string Month, decimal Total, int Receipts);

/// <summary>Total spend at one retailer across all history, with its share of overall spend (0..1).</summary>
public sealed record RetailerSpend(string Retailer, decimal Total, int Receipts, double Share);

public sealed record SpendSummary(
    decimal TotalAllTime,
    decimal ThisMonth,
    decimal LastMonth,
    int ReceiptsAllTime,
    IReadOnlyList<MonthSpend> Months,
    IReadOnlyList<RetailerSpend> Retailers);

/// <summary>A product bought repeatedly that's worth a second look (US-owned, or ultra-processed).</summary>
public sealed record RepeatOffender(
    string Item,
    int TimesBought,
    decimal TotalSpent,
    DateOnly LastBought,
    IReadOnlyList<string> Stores,
    int? NovaLevel,
    string? ParentCompany);

public sealed record RepeatOffenders(
    IReadOnlyList<RepeatOffender> American,
    IReadOnlyList<RepeatOffender> UltraProcessed);

public sealed record SpendInsights(SpendSummary Spend, RepeatOffenders Offenders);

/// <summary>
/// Longitudinal views over the durable purchase history: where the money goes (by month / retailer)
/// and which repeatedly-bought items are US-owned or heavily processed. Line spend is
/// <c>Quantity × UnitPrice</c>; a "receipt" is one distinct <see cref="PurchaseRecord.Source"/>.
/// </summary>
public static class SpendInsightsBuilder
{
    /// <summary>An item must be bought on at least this many separate days to count as a "repeat".</summary>
    public const int MinRepeat = 2;

    /// <summary>Repeat-offender habits only consider the last year, so old patterns don't linger.</summary>
    public const int OffenderLookbackDays = 365;

    /// <summary>NOVA level at/above which an item is treated as heavily (ultra-) processed.</summary>
    public const int UltraProcessedNova = 4;

    public static SpendInsights Build(PurchaseHistoryData history, DateOnly today)
        => new(BuildSpend(history, today), BuildOffenders(history, today));

    private static SpendSummary BuildSpend(PurchaseHistoryData history, DateOnly today)
    {
        var records = history.Records;
        var thisMonth = today.ToString("yyyy-MM");
        var lastMonth = today.AddMonths(-1).ToString("yyyy-MM");

        var months = records
            .GroupBy(r => r.Date.ToString("yyyy-MM"))
            .Select(g => new MonthSpend(g.Key, LineSum(g), g.Select(r => r.Source).Distinct().Count()))
            .OrderBy(m => m.Month)
            .ToList();

        var totalAllTime = records.Sum(LineTotal);

        var retailers = records
            .GroupBy(r => r.Retailer)
            .Select(g => new RetailerSpend(
                g.Key, LineSum(g), g.Select(r => r.Source).Distinct().Count(),
                totalAllTime > 0 ? (double)(LineSum(g) / totalAllTime) : 0))
            .OrderByDescending(r => r.Total)
            .ToList();

        return new SpendSummary(
            totalAllTime,
            months.FirstOrDefault(m => m.Month == thisMonth)?.Total ?? 0m,
            months.FirstOrDefault(m => m.Month == lastMonth)?.Total ?? 0m,
            records.Select(r => r.Source).Distinct().Count(),
            months,
            retailers);
    }

    private static RepeatOffenders BuildOffenders(PurchaseHistoryData history, DateOnly today)
    {
        var cutoff = today.AddDays(-OffenderLookbackDays);
        var american = new List<RepeatOffender>();
        var processed = new List<RepeatOffender>();

        foreach (var group in history.Records.Where(r => r.Date >= cutoff).GroupBy(r => r.Key))
        {
            var rows = group.ToList();
            var timesBought = rows.Select(r => r.Date).Distinct().Count();
            if (timesBought < MinRepeat) continue;

            var mostRecent = rows.OrderByDescending(r => r.Date).First();
            var stores = rows
                .GroupBy(r => r.Retailer)
                .OrderByDescending(g => g.Max(r => r.Date))
                .Select(g => g.Key)
                .ToList();
            var totalSpent = rows.Sum(LineTotal);
            var nova = RepresentativeNova(rows);
            var parent = rows.OrderByDescending(r => r.Date)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ParentCompany))?.ParentCompany;

            var offender = new RepeatOffender(
                mostRecent.Item, timesBought, totalSpent, mostRecent.Date, stores, nova, parent);

            if (rows.Any(r => r.IsAmerican == true))
                american.Add(offender);
            if (nova >= UltraProcessedNova)
                processed.Add(offender);
        }

        return new RepeatOffenders(Rank(american), Rank(processed));
    }

    /// <summary>
    /// The NOVA level most representative of an item across its readings: the most frequently assigned
    /// level, ties broken to the <em>lower</em> value. This is deliberately conservative — a single
    /// stray classification (e.g. a wine briefly read as NOVA 4) can't on its own brand an item as
    /// ultra-processed; it has to be the item's usual reading.
    /// </summary>
    private static int? RepresentativeNova(IReadOnlyList<PurchaseRecord> rows)
    {
        var levels = rows.Where(r => r.NovaLevel.HasValue).Select(r => r.NovaLevel!.Value).ToList();
        if (levels.Count == 0) return null;
        return levels
            .GroupBy(l => l)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First().Key;
    }

    private static IReadOnlyList<RepeatOffender> Rank(List<RepeatOffender> offenders) => offenders
        .OrderByDescending(o => o.TimesBought)
        .ThenByDescending(o => o.TotalSpent)
        .ToList();

    private static decimal LineTotal(PurchaseRecord r) => r.Quantity > 0 ? r.Quantity * r.UnitPrice : r.UnitPrice;

    private static decimal LineSum(IEnumerable<PurchaseRecord> records) => records.Sum(LineTotal);
}
