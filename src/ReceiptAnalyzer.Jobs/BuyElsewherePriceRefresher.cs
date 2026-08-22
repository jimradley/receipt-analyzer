using Microsoft.Extensions.Logging;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Jobs;

public sealed record PriceRefreshResult(int Checked, int Refreshed, int TotalItems);

/// <summary>
/// Re-checks market prices for <c>BuyElsewhere</c> ledger entries that haven't been looked at in
/// <see cref="JobsOptions.BuyElsewhereRefreshDays"/> days, so stale "best price" figures on the
/// Stores tab don't quietly go out of date. Mirrors <see cref="AnalysisPipeline"/>'s chunked
/// price-check pattern but runs standalone (not tied to a receipt upload).
/// </summary>
public sealed class BuyElsewherePriceRefresher
{
    private readonly IAnalysisAgent _agent;
    private readonly LedgerStore _ledgerStore;
    private readonly PriceCacheStore _priceCache;
    private readonly JobsOptions _options;
    private readonly ILogger<BuyElsewherePriceRefresher> _logger;

    public BuyElsewherePriceRefresher(IAnalysisAgent agent, LedgerStore ledgerStore, PriceCacheStore priceCache,
        JobsOptions options, ILogger<BuyElsewherePriceRefresher> logger)
    {
        _agent = agent;
        _ledgerStore = ledgerStore;
        _priceCache = priceCache;
        _options = options;
        _logger = logger;
    }

    public async Task<PriceRefreshResult> RefreshStaleAsync(DateOnly today, CancellationToken ct)
    {
        var ledger = _ledgerStore.Load();
        var cutoff = today.AddDays(-_options.BuyElsewhereRefreshDays);
        var stale = ledger.BuyElsewhere.Where(b => IsStale(b.LastSeen, cutoff)).ToList();
        if (stale.Count == 0)
            return new PriceRefreshResult(0, 0, ledger.BuyElsewhere.Count);

        var branded = stale.Select((b, i) => new BrandedItemForCheck(i, b.Item, b.PricePaid, b.StorePaid)).ToList();

        var results = new Dictionary<int, PriceCheckItem>();
        foreach (var chunk in branded.Chunk(Math.Max(1, _options.PriceCheckChunkSize)))
            await CheckChunkAsync(chunk, results, ct);

        var items = branded.Select(b => results[b.Index]).ToList();
        var todayStr = today.ToString("yyyy-MM-dd");
        var refreshed = _ledgerStore.ApplyPriceRefresh(ledger, stale, items, todayStr);
        _ledgerStore.Save(ledger);
        _ledgerStore.ReRenderMarkdown(ledger);

        var cacheable = items.Where(i => i.Outcome != PriceCheckOutcome.Unchecked).ToList();
        if (cacheable.Count > 0)
        {
            var cache = _priceCache.Load();
            PriceCacheStore.Upsert(cache, cacheable.Select(i => new PriceCacheEntry(
                KeyNormaliser.PriceKey(i.Name), i.BestPrice, i.BestPriceStore, i.Notes, todayStr,
                KeyNormaliser.Product(i.Name), KeyNormaliser.Pack(i.Name), i.Outcome)));
            _priceCache.Save(cache);
        }

        _logger.LogInformation("Grocery price refresh: {Checked} checked, {Refreshed} refreshed.", stale.Count, refreshed);
        return new PriceRefreshResult(stale.Count, refreshed, ledger.BuyElsewhere.Count);
    }

    private async Task CheckChunkAsync(
        IReadOnlyList<BrandedItemForCheck> chunk, Dictionary<int, PriceCheckItem> results, CancellationToken ct)
    {
        PriceCheckResult? fresh = null;
        try
        {
            fresh = ModelOutputValidator.Repair(chunk, await _agent.PriceCheckAsync(chunk, ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Grocery price refresh: chunk of {Count} failed.", chunk.Count);
        }

        foreach (var b in chunk)
        {
            results[b.Index] = fresh?.Items.FirstOrDefault(i => i.Index == b.Index)
                ?? new PriceCheckItem(b.Index, b.Name, b.PricePaid, b.Retailer,
                    null, null, null, "Price check failed.", PriceCheckOutcome.Unchecked, b.Quantity);
        }
    }

    private static bool IsStale(string lastSeen, DateOnly cutoff) =>
        !DateOnly.TryParse(lastSeen, out var parsed) || parsed <= cutoff;
}
