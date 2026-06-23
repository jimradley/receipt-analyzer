using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Jobs;

/// <summary>Runtime knobs for the job subsystem: retention for pruning and pricing for cost telemetry.</summary>
public sealed class JobsOptions
{
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(14);

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
