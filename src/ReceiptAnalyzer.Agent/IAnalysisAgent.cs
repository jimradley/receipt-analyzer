namespace ReceiptAnalyzer.Agent;

public interface IAnalysisAgent
{
    Task<ReceiptExtraction> ExtractReceiptAsync(byte[] imageBytes, string mediaType, CancellationToken ct);

    Task<ItemClassifications> ClassifyAsync(IReadOnlyList<RawItem> items, CancellationToken ct);

    Task<PriceCheckResult> PriceCheckAsync(IReadOnlyList<BrandedItemForCheck> items, CancellationToken ct);

    Task<SeasonalityResult> AssessSeasonalityAsync(IReadOnlyList<ProduceItem> items, int month, CancellationToken ct);
}
