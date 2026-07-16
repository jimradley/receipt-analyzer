namespace ReceiptAnalyzer.Agent;

public sealed record ReceiptExtraction(
    string Retailer,
    DateOnly? ReceiptDate,
    IReadOnlyList<RawItem> Items,
    decimal? PrintedSubtotal,
    decimal? PrintedTotal,
    decimal? Savings,
    bool IsReceipt,
    double Confidence,
    string? Notes,
    // The printed "number of items" line, when present. Cross-checked against the sum of item
    // quantities to catch a vision read that dropped or duplicated rows even when the £ totals
    // happen to reconcile.
    int? PrintedItemCount = null
);

public sealed record RawItem(
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal
);

public sealed record ItemClassifications(
    IReadOnlyList<ItemClassification> Items
);

public sealed record ItemClassification(
    int Index,
    int? NovaLevel,
    bool IsAmerican,
    string? ParentCompany,
    string? ParentCountry,
    bool IsOwnLabel,
    string? SwapSuggestion,
    string? Notes,
    // The abbreviated receipt name expanded to a full, searchable product name
    // (e.g. "BELVOIR HOM LEM GING" → "Belvoir Homemade Lemonade Ginger"). Used as the
    // search term for price-checking; null/blank falls back to the original receipt name.
    string? CanonicalName = null
);

public sealed record BrandedItemForCheck(
    int Index,
    string Name,
    decimal PricePaid,
    string Retailer,
    decimal Quantity = 1
);

/// <summary>
/// Per-item price-check outcomes. Strings (not an enum) so persisted job/cache JSON stays
/// trivially forward- and backward-compatible.
/// </summary>
public static class PriceCheckOutcome
{
    /// <summary>A cheaper current price was found at an allowed store.</summary>
    public const string CheaperElsewhere = "cheaper-elsewhere";

    /// <summary>The market was checked and the price paid is already the best (or equal).</summary>
    public const string AlreadyBest = "already-best";

    /// <summary>The item was searched for but no price could be established.</summary>
    public const string NotFound = "not-found";

    /// <summary>The item was never resolved — call failed or the model omitted it.</summary>
    public const string Unchecked = "unchecked";
}

/// <summary>
/// <see cref="BestPrice"/> is the best market price found at the allowed stores even when it is
/// NOT cheaper than what was paid (<see cref="Saving"/> ≤ 0 → outcome "already-best"). A null
/// price means the item couldn't be priced — see <see cref="Outcome"/> for why.
/// </summary>
public sealed record PriceCheckItem(
    int Index,
    string Name,
    decimal PricePaid,
    string StorePaid,
    decimal? BestPrice,
    string? BestPriceStore,
    decimal? Saving,
    string? Notes,
    string? Outcome = null,
    decimal Quantity = 1,
    // The URL the price was sourced from, when the agent provides one. Used by the fabrication
    // guard: a claimed price with no source at the receipt's own retailer is treated as unresolved.
    string? SourceUrl = null
);

public sealed record PriceCheckResult(
    IReadOnlyList<PriceCheckItem> Items,
    string? SkippedSummary
);

public sealed record ProduceItem(int Index, string Name);

public sealed record SeasonalityAssessment(
    int Index,
    string Name,
    bool IsInSeason,
    string? LikelyOrigin,
    string? UkSeasonMonths
);

public sealed record SeasonalityResult(
    IReadOnlyList<SeasonalityAssessment> Items
);

/// <summary>
/// One item on the current receipt that you've bought cheaper before, drawn from your own durable
/// purchase history (not market prices). <see cref="BestRetailer"/>/<see cref="BestDate"/> are when
/// and where you paid <see cref="BestUnitPrice"/>.
/// </summary>
public sealed record PersonalPriceComparison(
    string Item,
    decimal PaidNow,
    string RetailerNow,
    decimal BestUnitPrice,
    string BestRetailer,
    DateOnly BestDate,
    decimal Saving
);

public sealed record AnalysisResult(
    ReceiptExtraction Extraction,
    ItemClassifications Classifications,
    PriceCheckResult? PriceChecks,
    SeasonalityResult? Seasonality,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<StageUsage>? Usage = null,
    decimal? EstimatedCostGbp = null,
    IReadOnlyList<PersonalPriceComparison>? PersonalPrices = null
);
