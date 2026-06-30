namespace ReceiptAnalyzer.Ledger;

public sealed record BuyElsewhereEntry(
    string Key,
    string Item,
    string StorePaid,
    decimal PricePaid,
    decimal BestPrice,
    string Where,
    decimal Saving,
    string LastSeen,   // yyyy-MM-dd
    decimal? LatestBestPrice = null,
    string? LatestBestPriceStore = null
);

public sealed record AlternativeEntry(
    string Key,
    string Item,
    string Reason,
    string? SwapSuggestion,
    string? DetailMarkdown,   // full migrated section; null for items added by analysis
    string LastSeen           // yyyy-MM-dd
);

public sealed class LedgerData
{
    public List<BuyElsewhereEntry> BuyElsewhere { get; set; } = new();
    public List<AlternativeEntry> Alternatives { get; set; } = new();
}

public sealed record LedgerMergeResult(int Added, int Updated, IReadOnlyList<string> NewItems);

/// <summary>
/// A cached price-check result for one item, keyed by the normalised item name. Stores only the
/// "cheapest elsewhere" facts — <c>Saving</c> is recomputed against each receipt's price-paid, since
/// that varies. A null <see cref="BestPrice"/> records "checked, nothing cheaper / commodity" so those
/// items aren't re-searched within the freshness window.
/// </summary>
public sealed record PriceCacheEntry(
    string Key,
    decimal? BestPrice,
    string? BestPriceStore,
    string? Notes,
    string CheckedOn,   // yyyy-MM-dd
    string? ProductKey = null,
    string? Pack = null
);

public sealed class PriceCacheData
{
    public List<PriceCacheEntry> Entries { get; set; } = new();
}
