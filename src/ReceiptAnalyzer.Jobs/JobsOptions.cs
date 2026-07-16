using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Jobs;

/// <summary>Runtime knobs for the job subsystem: retention for pruning and pricing for cost telemetry.</summary>
public sealed class JobsOptions
{
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(14);

    /// <summary>How many days a cached price stays usable before the item is re-checked via web search.</summary>
    public int PriceCacheDays { get; init; } = 7;

    /// <summary>
    /// How many days a cached "not found" result suppresses re-searching. Deliberately much shorter
    /// than <see cref="PriceCacheDays"/> so a lazy or failed search doesn't hide an item for a week.
    /// </summary>
    public int PriceCacheNotFoundDays { get; init; } = 1;

    /// <summary>
    /// Items per price-check agent call. Small chunks give each item a real share of the web-search
    /// budget and contain the blast radius of a malformed response to one chunk.
    /// </summary>
    public int PriceCheckChunkSize { get; init; } = 4;

    /// <summary>Max items retried individually (with a "search harder" hint) after the first pass.</summary>
    public int PriceCheckRetryMax { get; init; } = 8;

    public decimal UsdToGbp { get; init; } = 0.79m;

    /// <summary>USD per-MTok rates keyed by model id. Models absent here contribute no cost estimate.</summary>
    public IReadOnlyDictionary<string, ModelPricing> Pricing { get; init; } =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Estimated cost in GBP for the given usage, or null if no rates cover the models seen.</summary>
    public decimal? EstimateGbp(IReadOnlyList<StageUsage> usage)
    {
        var usd = UsageCost.EstimateUsd(usage, Pricing);
        return usd is { } v ? Math.Round(v * UsdToGbp, 4) : null;
    }
}
