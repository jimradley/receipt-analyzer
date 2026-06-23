namespace ReceiptAnalyzer.Ledger;

public sealed record BuyElsewhereEntry(
    string Key,
    string Item,
    string StorePaid,
    decimal PricePaid,
    decimal BestPrice,
    string Where,
    decimal Saving,
    string LastSeen   // yyyy-MM-dd
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
