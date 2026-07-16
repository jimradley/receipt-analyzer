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
/// A cached price-check result for one item, keyed by the normalised item name. Stores the best
/// market price found (even when it wasn't cheaper than what was paid) — <c>Saving</c> is
/// recomputed against each receipt's price-paid, since that varies. <see cref="Outcome"/> records
/// why <see cref="BestPrice"/> may be null: a "not-found" entry expires on a shorter window than a
/// priced one, so a transient search failure doesn't suppress re-checking for a week. Legacy rows
/// (null <see cref="Outcome"/>) with a null price mean "checked, nothing cheaper / commodity" and
/// keep the standard freshness window.
/// </summary>
public sealed record PriceCacheEntry(
    string Key,
    decimal? BestPrice,
    string? BestPriceStore,
    string? Notes,
    string CheckedOn,   // yyyy-MM-dd
    string? ProductKey = null,
    string? Pack = null,
    string? Outcome = null
);

public sealed class PriceCacheData
{
    public List<PriceCacheEntry> Entries { get; set; } = new();
}
