namespace ReceiptAnalyzer.Agent;

public interface IAnalysisAgent
{
    /// <param name="correctionHint">
    /// Optional feedback appended to the prompt on a re-extraction — e.g. when the first read's line
    /// items didn't reconcile with the printed total, prompting the model to look for missed rows.
    /// </param>
    Task<ReceiptExtraction> ExtractReceiptAsync(byte[] imageBytes, string mediaType, CancellationToken ct, string? correctionHint = null);

    Task<ItemClassifications> ClassifyAsync(IReadOnlyList<RawItem> items, CancellationToken ct);

    Task<PriceCheckResult> PriceCheckAsync(IReadOnlyList<BrandedItemForCheck> items, CancellationToken ct);

    Task<SeasonalityResult> AssessSeasonalityAsync(IReadOnlyList<ProduceItem> items, int month, CancellationToken ct);
}
