namespace ReceiptAnalyzer.Ledger;

public static class InventoryMatchStatus
{
    public const string Pending = "pending";
    public const string Matched = "matched";
    public const string NotComparable = "not-comparable";
    public const string Unavailable = "unavailable";
    public const string Error = "error";
}

public sealed record StorePriceOffer(
    string Store,
    decimal ShelfPrice,
    decimal EffectivePrice,
    int MinimumQuantity,
    decimal CheckoutCost,
    string? Deal,
    bool LoyaltyRequired,
    string CheckedOn,
    string SourceUrl);

public sealed record InventoryProduct(
    string Key,
    string ProductKey,
    string? Pack,
    string Name,
    int PurchaseCount,
    string LastPurchasedOn,
    string LastPurchasedStore,
    decimal LastPricePaid,
    string MatchStatus,
    string? SourceUrl = null,
    string? LastCheckedOn = null,
    string? LastMatchAttemptOn = null,
    string? Error = null,
    IReadOnlyList<StorePriceOffer>? Offers = null)
{
    public IReadOnlyList<StorePriceOffer> CurrentOffers => Offers ?? [];
}

public sealed class InventoryRefreshState
{
    public string? Id { get; set; }
    public string Status { get; set; } = "idle";
    public string? StartedAt { get; set; }
    public string? NextBatchAt { get; set; }
    public string? CompletedAt { get; set; }
    public string? LastPublishedAt { get; set; }
    public int PublishedBatch { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Updated { get; set; }
    public int Unmatched { get; set; }
    public int Failed { get; set; }
    public int DirectRequests { get; set; }
    public int AgentCalls { get; set; }
    public List<string> PendingKeys { get; set; } = [];
    public string? Error { get; set; }

    public int Remaining => PendingKeys.Count;
}

public sealed class InventoryData
{
    public List<InventoryProduct> Products { get; set; } = [];
    public InventoryRefreshState Refresh { get; set; } = new();
}
