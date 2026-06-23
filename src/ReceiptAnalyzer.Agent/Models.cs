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
    string? Notes
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
    string? Notes
);

public sealed record BrandedItemForCheck(
    int Index,
    string Name,
    decimal PricePaid,
    string Retailer
);

public sealed record PriceCheckItem(
    int Index,
    string Name,
    decimal PricePaid,
    string StorePaid,
    decimal? BestPrice,
    string? BestPriceStore,
    decimal? Saving,
    string? Notes
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

public sealed record AnalysisResult(
    ReceiptExtraction Extraction,
    ItemClassifications Classifications,
    PriceCheckResult? PriceChecks,
    SeasonalityResult? Seasonality,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<StageUsage>? Usage = null,
    decimal? EstimatedCostGbp = null
);
