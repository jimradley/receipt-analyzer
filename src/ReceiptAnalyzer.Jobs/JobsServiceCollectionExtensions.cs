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

        services.AddSingleton(options);
        services.AddSingleton(new JobStore(outputDir));
        services.AddSingleton<IJobQueue, ChannelJobQueue>();
        services.AddSingleton(sp => new AnalysisPipeline(
            sp.GetRequiredService<IAnalysisAgent>(),
            sp.GetRequiredService<LedgerStore>(),
            sp.GetRequiredService<JobStore>(),
            outputDir,
            sp.GetRequiredService<JobsOptions>(),
            sp.GetRequiredService<ILogger<AnalysisPipeline>>()));
        services.AddHostedService<AnalysisWorker>();
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
            UsdToGbp = configuration.GetValue("Pricing:UsdToGbp", 0.79m),
            Pricing = pricing,
        };
    }
}
