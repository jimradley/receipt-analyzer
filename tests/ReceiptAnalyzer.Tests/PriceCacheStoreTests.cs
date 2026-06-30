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

        Assert.True(PriceCacheStore.TryGetFresh(data, "maltesers", today.AddDays(-7), out var hit));
        Assert.NotNull(hit);
        Assert.Equal(1.00m, hit!.BestPrice);
    }

    [Fact]
    public void TryGetFresh_rejects_an_entry_older_than_the_cutoff()
    {
        var today = new DateOnly(2026, 6, 24);
        var data = new PriceCacheData();
        PriceCacheStore.Upsert(data, new[] { Entry("maltesers", 1.00m, today.AddDays(-10)) });

        Assert.False(PriceCacheStore.TryGetFresh(data, "maltesers", today.AddDays(-7), out var hit));
        Assert.Null(hit);
    }

    [Fact]
    public void A_null_best_price_is_cached_and_treated_as_a_fresh_hit()
    {
        // "checked, nothing cheaper" must count as a hit so the item isn't re-searched within the window.
        var today = new DateOnly(2026, 6, 24);
        var data = new PriceCacheData();
        PriceCacheStore.Upsert(data, new[] { Entry("commodity-eggs", null, today.AddDays(-1)) });

        Assert.True(PriceCacheStore.TryGetFresh(data, "commodity-eggs", today.AddDays(-7), out var hit));
        Assert.NotNull(hit);
        Assert.Null(hit!.BestPrice);
    }
}
