using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Jobs;

/// <summary>Repairs untrusted model output before it can be cached or persisted.</summary>
public static class ModelOutputValidator
{
    /// <param name="today">
    /// Overridable for tests; defaults to the current UTC date. Used only for the receipt-date
    /// sanity check below.
    /// </param>
    public static ReceiptExtraction Repair(ReceiptExtraction value, DateOnly? today = null)
    {
        var items = value.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => i with
            {
                Name = i.Name.Trim(),
                Quantity = i.Quantity > 0 ? i.Quantity : 1,
                UnitPrice = Math.Max(0, i.UnitPrice),
                LineTotal = Math.Max(0, i.LineTotal)
            })
            .ToList();

        // Date sanity: a receipt date outside a plausible window (today's OCR read a garbled "2023"
        // from a 2026 receipt) is worse than no date at all — null it and let the renderer fall back
        // to the upload date, with a note explaining why.
        var receiptDate = value.ReceiptDate;
        var notes = value.Notes;
        if (receiptDate is { } rd)
        {
            var todayDate = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var earliestAllowed = todayDate.AddYears(-1);
            var latestAllowed = todayDate.AddDays(1);
            if (rd <= earliestAllowed || rd > latestAllowed)
            {
                notes = AppendNote(notes,
                    $"Receipt date {rd:yyyy-MM-dd} looked implausible and was cleared (falls back to the upload date).");
                receiptDate = null;
            }
        }

        return value with
        {
            Retailer = string.IsNullOrWhiteSpace(value.Retailer) ? "Unknown" : value.Retailer.Trim(),
            Items = items,
            ReceiptDate = receiptDate,
            Confidence = Math.Clamp(value.Confidence, 0, 1),
            PrintedSubtotal = NonNegative(value.PrintedSubtotal),
            PrintedTotal = NonNegative(value.PrintedTotal),
            Savings = NonNegative(value.Savings),
            Notes = notes
        };
    }

    public static ItemClassifications Repair(
        IReadOnlyList<RawItem> source, ItemClassifications value)
    {
        var valid = value.Items
            .Where(c => c.Index >= 0 && c.Index < source.Count)
            .GroupBy(c => c.Index)
            .ToDictionary(g => g.Key, g => g.First());

        var repaired = Enumerable.Range(0, source.Count).Select(index =>
        {
            if (!valid.TryGetValue(index, out var c))
                return new ItemClassification(index, null, false, null, null, false, null,
                    "Classification unavailable.");

            return c with
            {
                NovaLevel = c.NovaLevel is >= 1 and <= 4 ? c.NovaLevel : null,
                ParentCompany = Clean(c.ParentCompany),
                ParentCountry = Clean(c.ParentCountry),
                SwapSuggestion = Clean(c.SwapSuggestion),
                Notes = Clean(c.Notes),
                CanonicalName = Clean(c.CanonicalName)
            };
        }).ToList();
        return new ItemClassifications(repaired);
    }

    public static PriceCheckResult Repair(
        IReadOnlyList<BrandedItemForCheck> requested, PriceCheckResult value)
    {
        var requestedIndexes = requested.Select(r => r.Index).ToHashSet();
        var byIndex = value.Items
            .Where(x => requestedIndexes.Contains(x.Index))
            .GroupBy(x => x.Index)
            // Prefer a duplicate that carries a price over one that doesn't; then the cheapest.
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.BestPrice.HasValue).ThenBy(x => x.BestPrice).First());

        // Every requested item comes back exactly once with an outcome — an item the model
        // omitted is recorded as "unchecked" rather than silently vanishing.
        var items = requested.Select(request =>
        {
            if (!byIndex.TryGetValue(request.Index, out var x))
                return new PriceCheckItem(
                    request.Index, request.Name, request.PricePaid, request.Retailer,
                    null, null, null, "No result returned.",
                    PriceCheckOutcome.Unchecked, request.Quantity);

            var best = NonNegative(x.BestPrice);
            var bestStore = best is null ? null : Clean(x.BestPriceStore);
            var sourceUrl = Clean(x.SourceUrl);

            // Hard rule: "cheaper elsewhere" must mean a different store than the one the item was
            // actually bought at. A same-store result — even a real search hit, e.g. a stale or
            // promotional price — isn't a useful recommendation, so drop it the same way a
            // never-allowed store would be (falls through to NotFound and gets retried).
            if (bestStore is not null &&
                StoreCatalog.Canonical(bestStore) is { } bs && bs == StoreCatalog.Canonical(request.Retailer))
            {
                best = null;
                bestStore = null;
            }

            var saving = best is { } price ? request.PricePaid - price : (decimal?)null;
            var outcome = best is null
                ? PriceCheckOutcome.NotFound
                : saving > 0 ? PriceCheckOutcome.CheaperElsewhere : PriceCheckOutcome.AlreadyBest;

            return x with
            {
                Name = request.Name,
                PricePaid = request.PricePaid,
                StorePaid = request.Retailer,
                BestPrice = best,
                BestPriceStore = bestStore,
                Saving = saving,
                Notes = Clean(x.Notes),
                Outcome = outcome,
                Quantity = request.Quantity,
                SourceUrl = sourceUrl
            };
        })
        .OrderBy(x => x.Index)
        .ToList();
        return new PriceCheckResult(items, Clean(value.SkippedSummary));
    }

    public static SeasonalityResult Repair(
        IReadOnlyList<ProduceItem> requested, SeasonalityResult value)
    {
        var byIndex = requested.ToDictionary(x => x.Index);
        var items = value.Items
            .Where(x => byIndex.ContainsKey(x.Index))
            .GroupBy(x => x.Index)
            .Select(g => g.First())
            .Select(x => x with
            {
                Name = byIndex[x.Index].Name,
                LikelyOrigin = Clean(x.LikelyOrigin),
                UkSeasonMonths = Clean(x.UkSeasonMonths)
            })
            .OrderBy(x => x.Index)
            .ToList();
        return new SeasonalityResult(items);
    }

    private static decimal? NonNegative(decimal? value) => value is >= 0 ? value : null;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string AppendNote(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : existing.Trim() + " " + addition;
}
