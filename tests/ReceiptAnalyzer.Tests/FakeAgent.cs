using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Tests;

/// <summary>Records how many times each pipeline stage is invoked, so resume behaviour is observable.</summary>
internal sealed class FakeAgent : IAnalysisAgent
{
    public int ExtractCalls;
    public int ClassifyCalls;
    public int PriceCheckCalls;
    public int SeasonalityCalls;
    public bool IsReceipt = true;

    /// <summary>The branded items handed to the most recent price-check call (cache misses only).</summary>
    public IReadOnlyList<BrandedItemForCheck> LastPriceCheckItems = Array.Empty<BrandedItemForCheck>();

    // Every stage reports 1000 input / 500 output tokens against "test-model".
    public const string Model = "test-model";

    public Task<ReceiptExtraction> ExtractReceiptAsync(byte[] imageBytes, string mediaType, CancellationToken ct, string? correctionHint = null)
    {
        ExtractCalls++;
        UsageReporter.Report(new StageUsage("extract", Model, 1000, 500, 0, 0));
        var ext = TestData.SampleResult().Extraction with { IsReceipt = IsReceipt };
        return Task.FromResult(ext);
    }

    public Task<ItemClassifications> ClassifyAsync(IReadOnlyList<RawItem> items, CancellationToken ct)
    {
        ClassifyCalls++;
        UsageReporter.Report(new StageUsage("classify", Model, 1000, 500, 0, 0));
        return Task.FromResult(TestData.SampleResult().Classifications);
    }

    public Task<PriceCheckResult> PriceCheckAsync(IReadOnlyList<BrandedItemForCheck> items, CancellationToken ct)
    {
        PriceCheckCalls++;
        LastPriceCheckItems = items;
        UsageReporter.Report(new StageUsage("price-check", Model, 1000, 500, 0, 0));
        // Echo each requested item with a fixed cheapest-elsewhere price so the cache covers its key.
        var results = items.Select(i => new PriceCheckItem(
            i.Index, i.Name, i.PricePaid, i.Retailer,
            BestPrice: 1.00m, BestPriceStore: "Asda", Saving: i.PricePaid - 1.00m, Notes: null)).ToList();
        return Task.FromResult(new PriceCheckResult(results, SkippedSummary: null));
    }

    public Task<SeasonalityResult> AssessSeasonalityAsync(IReadOnlyList<ProduceItem> items, int month, CancellationToken ct)
    {
        SeasonalityCalls++;
        UsageReporter.Report(new StageUsage("seasonality", Model, 1000, 500, 0, 0));
        return Task.FromResult(TestData.SampleResult().Seasonality!);
    }
}
