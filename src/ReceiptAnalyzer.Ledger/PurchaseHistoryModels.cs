namespace ReceiptAnalyzer.Ledger;

/// <summary>
/// One line item bought on one receipt, kept durably so purchase cadence can be learned over time.
/// <see cref="Key"/> is <see cref="KeyNormaliser"/>'d for grouping variants of the same product.
/// <see cref="Source"/> identifies the receipt that contributed it (the job id for live appends, or
/// <c>"backfill:{file}"</c> for rows migrated from existing reports) so re-processing a receipt
/// replaces rather than duplicates its rows.
/// <see cref="NovaLevel"/> and <see cref="IsAmerican"/> carry the per-item classification forward so
/// longitudinal habits (heavy ultra-processed / repeat US-brand buying) can be flagged across receipts;
/// both are nullable because rows written before classification was persisted won't have them.
/// </summary>
public sealed record PurchaseRecord(
    string Key,
    string Item,
    string Retailer,
    DateOnly Date,
    decimal Quantity,
    decimal UnitPrice,
    string? Source,
    int? NovaLevel = null,
    bool? IsAmerican = null,
    string? ParentCompany = null,
    string? CanonicalName = null
);

public sealed class PurchaseHistoryData
{
    public List<PurchaseRecord> Records { get; set; } = new();
}
