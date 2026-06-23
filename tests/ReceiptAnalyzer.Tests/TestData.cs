using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Tests;

/// <summary>Builders for a representative analysis result used across renderer/ledger tests.</summary>
internal static class TestData
{
    public static AnalysisResult SampleResult()
    {
        var items = new List<RawItem>
        {
            new("Bananas Loose", 6m, 0.15m, 0.90m),          // index 0 — NOVA 1 produce
            new("Maltesers 100g", 1m, 1.50m, 1.50m),         // index 1 — American, NOVA 4
            new("M Cheddar Mild 400g", 1m, 2.29m, 2.29m),    // index 2 — own-label, NOVA 3
        };

        var extraction = new ReceiptExtraction(
            Retailer: "Morrisons",
            ReceiptDate: new DateOnly(2026, 6, 20),
            Items: items,
            PrintedSubtotal: 4.69m,   // matches sum of line items (0.90 + 1.50 + 2.29)
            PrintedTotal: 4.39m,      // subtotal minus £0.30 savings
            Savings: 0.30m,
            IsReceipt: true,
            Confidence: 0.97,
            Notes: "Faded thermal print near total.");

        var classifications = new ItemClassifications(new List<ItemClassification>
        {
            new(0, NovaLevel: 1, IsAmerican: false, ParentCompany: null, ParentCountry: null,
                IsOwnLabel: false, SwapSuggestion: null, Notes: null),
            new(1, NovaLevel: 4, IsAmerican: true, ParentCompany: "Mars", ParentCountry: "USA",
                IsOwnLabel: false, SwapSuggestion: "Try a UK-owned chocolate.", Notes: null),
            new(2, NovaLevel: 3, IsAmerican: false, ParentCompany: null, ParentCountry: null,
                IsOwnLabel: true, SwapSuggestion: null, Notes: null),
        });

        var priceChecks = new PriceCheckResult(new List<PriceCheckItem>
        {
            // saving above the £0.30 threshold — should render bold
            new(1, "Maltesers 100g", PricePaid: 1.50m, StorePaid: "Morrisons",
                BestPrice: 1.00m, BestPriceStore: "Asda", Saving: 0.50m, Notes: null),
            // saving below threshold — present but not bold
            new(2, "M Cheddar Mild 400g", PricePaid: 2.29m, StorePaid: "Morrisons",
                BestPrice: 2.20m, BestPriceStore: "Aldi", Saving: 0.09m, Notes: null),
        }, SkippedSummary: "_Skipped 1 commodity item._");

        var seasonality = new SeasonalityResult(new List<SeasonalityAssessment>
        {
            new(0, "Bananas Loose", IsInSeason: true, LikelyOrigin: "Costa Rica", UkSeasonMonths: null),
        });

        return new AnalysisResult(extraction, classifications, priceChecks, seasonality,
            GeneratedAt: new DateTimeOffset(2026, 6, 23, 9, 0, 0, TimeSpan.Zero));
    }
}
