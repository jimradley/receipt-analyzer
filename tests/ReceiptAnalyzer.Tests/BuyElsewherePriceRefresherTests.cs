using Microsoft.Extensions.Logging.Abstractions;
using ReceiptAnalyzer.Jobs;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class BuyElsewherePriceRefresherTests : IDisposable
{
    private readonly string _dir;
    private readonly FakeAgent _agent = new();

    public BuyElsewherePriceRefresherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ra-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private LedgerStore NewLedgerStore() => new(_dir, NullLogger<LedgerStore>.Instance);

    private BuyElsewherePriceRefresher NewRefresher(LedgerStore ledgerStore, int refreshDays = 3) =>
        new(_agent, ledgerStore, new PriceCacheStore(_dir),
            new JobsOptions { BuyElsewhereRefreshDays = refreshDays },
            NullLogger<BuyElsewherePriceRefresher>.Instance);

    [Fact]
    public async Task RefreshStaleAsync_checks_and_overwrites_a_stale_entry()
    {
        var ledgerStore = NewLedgerStore();
        var ledger = ledgerStore.Load();
        ledgerStore.Merge(ledger, TestData.SampleResult(), today: "2026-06-01");
        ledgerStore.Save(ledger);

        var refresher = NewRefresher(NewLedgerStore());
        var result = await refresher.RefreshStaleAsync(new DateOnly(2026, 6, 10), CancellationToken.None);

        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Refreshed);
        Assert.Equal(1, _agent.PriceCheckCalls);

        var reloaded = NewLedgerStore().Load();
        Assert.Equal("2026-06-10", reloaded.BuyElsewhere[0].LastSeen);
        Assert.Equal(1.00m, reloaded.BuyElsewhere[0].BestPrice); // FakeAgent's fixed echoed price
    }

    [Fact]
    public async Task RefreshStaleAsync_is_a_noop_when_nothing_is_past_the_cutoff()
    {
        var ledgerStore = NewLedgerStore();
        var ledger = ledgerStore.Load();
        ledgerStore.Merge(ledger, TestData.SampleResult(), today: "2026-06-08");
        ledgerStore.Save(ledger);

        var refresher = NewRefresher(NewLedgerStore());
        var result = await refresher.RefreshStaleAsync(new DateOnly(2026, 6, 10), CancellationToken.None);

        Assert.Equal(0, result.Checked);
        Assert.Equal(0, result.Refreshed);
        Assert.Equal(0, _agent.PriceCheckCalls);
    }
}
