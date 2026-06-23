using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Tests;

public class UsageCostTests
{
    private static readonly Dictionary<string, ModelPricing> Pricing = new()
    {
        ["claude-sonnet-4-6"] = new(InputPerMTok: 3.00m, OutputPerMTok: 15.00m,
            CacheReadPerMTok: 0.30m, CacheWritePerMTok: 3.75m),
    };

    [Fact]
    public void EstimateUsd_sums_all_token_classes_at_their_rates()
    {
        var usage = new List<StageUsage>
        {
            // 1M input @ $3, 1M output @ $15, 1M cache-read @ $0.30, 1M cache-write @ $3.75 = $22.05
            new("extract", "claude-sonnet-4-6", 1_000_000, 1_000_000, 1_000_000, 1_000_000),
        };
        Assert.Equal(22.05m, UsageCost.EstimateUsd(usage, Pricing));
    }

    [Fact]
    public void EstimateUsd_returns_null_when_no_model_has_pricing()
    {
        var usage = new List<StageUsage> { new("extract", "unknown-model", 1000, 500, 0, 0) };
        Assert.Null(UsageCost.EstimateUsd(usage, Pricing));
    }

    [Fact]
    public void EstimateUsd_ignores_unpriced_models_but_counts_priced_ones()
    {
        var usage = new List<StageUsage>
        {
            new("extract", "claude-sonnet-4-6", 1_000_000, 0, 0, 0), // $3.00
            new("classify", "unknown-model", 1_000_000, 0, 0, 0),    // ignored
        };
        Assert.Equal(3.00m, UsageCost.EstimateUsd(usage, Pricing));
    }
}
