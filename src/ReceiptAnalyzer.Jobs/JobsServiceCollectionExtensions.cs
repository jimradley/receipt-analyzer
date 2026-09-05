using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Jobs;

public static class JobsServiceCollectionExtensions
{
    /// <summary>Registers the durable job store, queue, pipeline, background worker, and cost/retention options.</summary>
    public static IServiceCollection AddAnalysisJobs(
        this IServiceCollection services, string outputDir, IConfiguration configuration)
    {
        var options = BuildOptions(configuration);
        var inventoryOptions = BuildInventoryOptions(configuration);

        services.AddSingleton(options);
        services.AddSingleton(inventoryOptions);
        services.AddSingleton(new JobStore(outputDir));
        services.AddSingleton<IJobQueue, ChannelJobQueue>();
        services.AddSingleton(sp => new AnalysisPipeline(
            sp.GetRequiredService<IAnalysisAgent>(),
            sp.GetRequiredService<LedgerStore>(),
            sp.GetRequiredService<PriceCacheStore>(),
            sp.GetRequiredService<ReceiptAnalyzer.Reports.UsageLedgerStore>(),
            sp.GetRequiredService<PurchaseHistoryStore>(),
            sp.GetRequiredService<JobStore>(),
            outputDir,
            sp.GetRequiredService<JobsOptions>(),
            sp.GetRequiredService<ILogger<AnalysisPipeline>>()));
        services.AddSingleton(sp => new BuyElsewherePriceRefresher(
            sp.GetRequiredService<IAnalysisAgent>(),
            sp.GetRequiredService<LedgerStore>(),
            sp.GetRequiredService<PriceCacheStore>(),
            sp.GetRequiredService<JobsOptions>(),
            sp.GetRequiredService<ILogger<BuyElsewherePriceRefresher>>()));
        services.AddHttpClient<TrolleyPriceClient>(http =>
        {
            http.BaseAddress = new Uri("https://www.trolley.co.uk/");
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ReceiptAnalyzer/1.0 price-inventory");
        });
        services.AddHttpClient<IProductCandidateMatcher, CodexCandidateMatcher>(http =>
        {
            http.BaseAddress = new Uri(inventoryOptions.BridgeUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromMinutes(4);
        });
        services.AddSingleton<InventoryPriceRefreshService>();
        services.AddHostedService<AnalysisWorker>();
        services.AddHostedService<InventoryPriceRefreshWorker>();
        return services;
    }

    private static JobsOptions BuildOptions(IConfiguration configuration)
    {
        // Sensible defaults; appsettings "Pricing"/"Jobs" sections override.
        var pricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            // USD per million tokens.
            ["claude-sonnet-4-6"] = new(InputPerMTok: 3.00m, OutputPerMTok: 15.00m,
                CacheReadPerMTok: 0.30m, CacheWritePerMTok: 3.75m),
            ["gpt-4o"] = new(InputPerMTok: 2.50m, OutputPerMTok: 10.00m,
                CacheReadPerMTok: 1.25m, CacheWritePerMTok: 0m),
            ["gpt-5-mini"] = new(InputPerMTok: 0.25m, OutputPerMTok: 2.00m,
                CacheReadPerMTok: 0.025m, CacheWritePerMTok: 0m),
            // Headless Claude Code via the bridge rides a Max subscription — zero marginal API cost.
            ["claude-sonnet-5"] = new(InputPerMTok: 0m, OutputPerMTok: 0m,
                CacheReadPerMTok: 0m, CacheWritePerMTok: 0m),
        };

        var modelsSection = configuration.GetSection("Pricing:Models");
        foreach (var model in modelsSection.GetChildren())
        {
            pricing[model.Key] = new ModelPricing(
                InputPerMTok: model.GetValue("InputPerMTok", 0m),
                OutputPerMTok: model.GetValue("OutputPerMTok", 0m),
                CacheReadPerMTok: model.GetValue("CacheReadPerMTok", 0m),
                CacheWritePerMTok: model.GetValue("CacheWritePerMTok", 0m));
        }

        return new JobsOptions
        {
            Retention = TimeSpan.FromDays(configuration.GetValue("Jobs:RetentionDays", 14)),
            PriceCacheDays = configuration.GetValue("Jobs:PriceCacheDays", 7),
            PriceCacheNotFoundDays = configuration.GetValue("Jobs:PriceCacheNotFoundDays", 1),
            PriceCheckChunkSize = configuration.GetValue("Jobs:PriceCheckChunkSize", 4),
            PriceCheckRetryMax = configuration.GetValue("Jobs:PriceCheckRetryMax", 8),
            BuyElsewhereRefreshDays = configuration.GetValue("Jobs:BuyElsewhereRefreshDays", 3),
            UsdToGbp = configuration.GetValue("Pricing:UsdToGbp", 0.79m),
            Pricing = pricing,
        };
    }

    private static InventoryRefreshOptions BuildInventoryOptions(IConfiguration configuration) => new()
    {
        KnownBatchSize = configuration.GetValue("PriceInventory:KnownBatchSize", 50),
        AgentBatchSize = configuration.GetValue("PriceInventory:AgentBatchSize", 5),
        RecentPurchaseDays = configuration.GetValue("PriceInventory:RecentPurchaseDays", 90),
        RecentRefreshDays = configuration.GetValue("PriceInventory:RecentRefreshDays", 7),
        OlderRefreshDays = configuration.GetValue("PriceInventory:OlderRefreshDays", 30),
        UnmatchedRetryDays = configuration.GetValue("PriceInventory:UnmatchedRetryDays", 30),
        BatchDelay = TimeSpan.FromMinutes(configuration.GetValue("PriceInventory:BatchDelayMinutes", 60)),
        BridgeUrl = configuration["PriceInventory:BridgeUrl"] ??
                    configuration["Agent:ClaudeCode:BridgeUrl"] ?? "http://localhost:5095",
        BridgeKeyEnvVar = configuration["PriceInventory:BridgeKeyEnvVar"] ??
                          configuration["Agent:ClaudeCode:BridgeKeyEnvVar"] ?? "RECEIPT_BRIDGE_KEY",
        CodexModel = configuration["PriceInventory:CodexModel"] ?? "gpt-5.6-luna"
    };
}
