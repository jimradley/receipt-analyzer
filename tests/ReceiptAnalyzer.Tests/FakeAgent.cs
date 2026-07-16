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

    /// <summary>Every price-check call's items, in order — lets tests observe chunking.</summary>
    public List<IReadOnlyList<BrandedItemForCheck>> PriceCheckCallItems = new();

    /// <summary>The hint passed to each price-check call (null on the first pass).</summary>
    public List<string?> PriceCheckHints = new();

    /// <summary>1-based price-check call numbers that should throw (simulates a failed/garbled call).</summary>
    public HashSet<int> ThrowOnPriceCheckCalls = new();

    /// <summary>Item indexes to report as found=false (not found) when no hint is given.</summary>
    public HashSet<int> NotFoundUntilHinted = new();

    /// <summary>Item indexes to omit from the response entirely (simulates the model dropping items).</summary>
    public HashSet<int> OmitFromPriceCheck = new();

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

    public Task<PriceCheckResult> PriceCheckAsync(IReadOnlyList<BrandedItemForCheck> items, CancellationToken ct, string? hint = null)
    {
        PriceCheckCalls++;
        LastPriceCheckItems = items;
        PriceCheckCallItems.Add(items);
        PriceCheckHints.Add(hint);
        UsageReporter.Report(new StageUsage("price-check", Model, 1000, 500, 0, 0));

        if (ThrowOnPriceCheckCalls.Contains(PriceCheckCalls))
            throw new InvalidOperationException("Simulated price-check failure.");

        // Echo each requested item with a fixed cheapest-elsewhere price so the cache covers its key.
        var results = items
            .Where(i => !OmitFromPriceCheck.Contains(i.Index))
            .Select(i => NotFoundUntilHinted.Contains(i.Index) && hint is null
                ? new PriceCheckItem(i.Index, i.Name, i.PricePaid, i.Retailer,
                    BestPrice: null, BestPriceStore: null, Saving: null, Notes: null,
                    Outcome: PriceCheckOutcome.NotFound, Quantity: i.Quantity)
                : new PriceCheckItem(i.Index, i.Name, i.PricePaid, i.Retailer,
                    BestPrice: 1.00m, BestPriceStore: "Asda", Saving: i.PricePaid - 1.00m, Notes: null,
                    Quantity: i.Quantity))
            .ToList();
        return Task.FromResult(new PriceCheckResult(results, SkippedSummary: null));
    }

    public Task<SeasonalityResult> AssessSeasonalityAsync(IReadOnlyList<ProduceItem> items, int month, CancellationToken ct)
    {
        SeasonalityCalls++;
        UsageReporter.Report(new StageUsage("seasonality", Model, 1000, 500, 0, 0));
        return Task.FromResult(TestData.SampleResult().Seasonality!);
    }
}
