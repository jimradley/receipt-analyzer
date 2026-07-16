using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class PriceCacheStoreTests : IDisposable
{
    private readonly string _dir;

    public PriceCacheStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ra-pricecache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static PriceCacheEntry Entry(string key, decimal? best, DateOnly on) =>
        new(key, best, best is null ? null : "Asda", null, on.ToString("yyyy-MM-dd"));

    [Fact]
    public void Upsert_and_save_round_trip_through_disk()
    {
        var store = new PriceCacheStore(_dir);
        var data = new PriceCacheData();
        PriceCacheStore.Upsert(data, new[] { Entry("maltesers", 1.00m, new DateOnly(2026, 6, 20)) });
        store.Save(data);

        var reloaded = store.Load();
        Assert.Single(reloaded.Entries);
        Assert.Equal("maltesers", reloaded.Entries[0].Key);
        Assert.Equal(1.00m, reloaded.Entries[0].BestPrice);
    }

    [Fact]
    public void Upsert_replaces_an_existing_key_rather_than_duplicating()
    {
        var data = new PriceCacheData();
        PriceCacheStore.Upsert(data, new[] { Entry("maltesers", 1.00m, new DateOnly(2026, 6, 10)) });
        PriceCacheStore.Upsert(data, new[] { Entry("maltesers", 0.90m, new DateOnly(2026, 6, 20)) });

        Assert.Single(data.Entries);
        Assert.Equal(0.90m, data.Entries[0].BestPrice);
    }

    [Fact]
    public void TryGetFresh_returns_an_entry_checked_on_or_after_the_cutoff()
    {
        var today = new DateOnly(2026, 6, 24);
        var data = new PriceCacheData();
        PriceCacheStore.Upsert(data, new[] { Entry("maltesers", 1.00m, today.AddDays(-3)) });

        Assert.True(PriceCacheStore.TryGetFresh(
            data, "maltesers", today.AddDays(-7), today.AddDays(-1), out var hit));
        Assert.NotNull(hit);
        Assert.Equal(1.00m, hit!.BestPrice);
    }

    [Fact]
    public void TryGetFresh_rejects_an_entry_older_than_the_cutoff()
    {
        var today = new DateOnly(2026, 6, 24);
        var data = new PriceCacheData();
        PriceCacheStore.Upsert(data, new[] { Entry("maltesers", 1.00m, today.AddDays(-10)) });

        Assert.False(PriceCacheStore.TryGetFresh(
            data, "maltesers", today.AddDays(-7), today.AddDays(-1), out var hit));
        Assert.Null(hit);
    }

    [Fact]
    public void A_legacy_null_best_price_is_cached_and_treated_as_a_fresh_hit()
    {
        // Legacy rows (no outcome) with a null price meant "checked, nothing cheaper" and must keep
        // the standard freshness window so the item isn't re-searched.
        var today = new DateOnly(2026, 6, 24);
        var data = new PriceCacheData();
        PriceCacheStore.Upsert(data, new[] { Entry("commodity-eggs", null, today.AddDays(-3)) });

        Assert.True(PriceCacheStore.TryGetFresh(
            data, "commodity-eggs", today.AddDays(-7), today.AddDays(-1), out var hit));
        Assert.NotNull(hit);
        Assert.Null(hit!.BestPrice);
    }

    [Fact]
    public void A_not_found_entry_expires_on_the_shorter_window()
    {
        // A genuine "couldn't price it" answer suppresses re-searching only briefly, so a lazy or
        // transiently-failing search doesn't hide the item for a whole week.
        var today = new DateOnly(2026, 6, 24);
        var data = new PriceCacheData();
        var notFound = Entry("obscure-beer", null, today.AddDays(-3)) with { Outcome = PriceCheckOutcome.NotFound };
        PriceCacheStore.Upsert(data, new[] { notFound });

        Assert.False(PriceCacheStore.TryGetFresh(
            data, "obscure-beer", today.AddDays(-7), today.AddDays(-1), out var stale));
        Assert.Null(stale);

        // Still fresh inside the not-found window.
        Assert.True(PriceCacheStore.TryGetFresh(
            data, "obscure-beer", today.AddDays(-7), today.AddDays(-4), out var hit));
        Assert.Equal(PriceCheckOutcome.NotFound, hit!.Outcome);
    }

    [Fact]
    public void A_priced_entry_keeps_the_full_window_even_when_the_not_found_window_has_passed()
    {
        var today = new DateOnly(2026, 6, 24);
        var data = new PriceCacheData();
        var priced = Entry("maltesers", 1.00m, today.AddDays(-3)) with { Outcome = PriceCheckOutcome.AlreadyBest };
        PriceCacheStore.Upsert(data, new[] { priced });

        Assert.True(PriceCacheStore.TryGetFresh(
            data, "maltesers", today.AddDays(-7), today.AddDays(-1), out var hit));
        Assert.Equal(1.00m, hit!.BestPrice);
    }
}
