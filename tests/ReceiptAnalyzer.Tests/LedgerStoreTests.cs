using Microsoft.Extensions.Logging.Abstractions;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class LedgerStoreTests : IDisposable
{
    private readonly string _dir;

    public LedgerStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ra-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private LedgerStore NewStore() => new(_dir, NullLogger<LedgerStore>.Instance);

    [Fact]
    public void Merge_records_american_and_processed_items_as_alternatives()
    {
        var store = NewStore();
        var ledger = store.Load();

        var merge = store.Merge(ledger, TestData.SampleResult(), today: "2026-06-23");

        // Maltesers (American + NOVA 4) and the NOVA 3 own-label cheddar qualify; NOVA 1 bananas do not.
        Assert.Equal(2, ledger.Alternatives.Count);
        Assert.Contains(ledger.Alternatives, a => a.Item == "Maltesers 100g" && a.Reason.Contains("Mars"));
        // Added counts new entries across both ledgers: 2 alternatives + 1 buy-elsewhere.
        Assert.Equal(3, merge.Added);
    }

    [Fact]
    public void Merge_records_buy_elsewhere_only_above_saving_threshold()
    {
        var store = NewStore();
        var ledger = store.Load();

        store.Merge(ledger, TestData.SampleResult(), today: "2026-06-23");

        // Only the £0.50 saving qualifies; the £0.09 saving is below £0.30.
        Assert.Single(ledger.BuyElsewhere);
        Assert.Equal("Maltesers 100g", ledger.BuyElsewhere[0].Item);
        Assert.Equal("Asda", ledger.BuyElsewhere[0].Where);
    }

    [Fact]
    public void Save_then_load_round_trips_through_json()
    {
        var store = NewStore();
        var ledger = store.Load();
        store.Merge(ledger, TestData.SampleResult(), today: "2026-06-23");
        store.Save(ledger);

        var reloaded = NewStore().Load();
        Assert.Equal(ledger.Alternatives.Count, reloaded.Alternatives.Count);
        Assert.Equal(ledger.BuyElsewhere.Count, reloaded.BuyElsewhere.Count);
        Assert.True(File.Exists(Path.Combine(_dir, ".state", "ledger.json")));
    }

    [Fact]
    public void ReRenderMarkdown_writes_both_ledger_files()
    {
        var store = NewStore();
        var ledger = store.Load();
        store.Merge(ledger, TestData.SampleResult(), today: "2026-06-23");
        store.ReRenderMarkdown(ledger);

        var buyElsewhere = File.ReadAllText(Path.Combine(_dir, "buy-elsewhere.md"));
        var alternatives = File.ReadAllText(Path.Combine(_dir, "alternatives.md"));

        Assert.Contains("Maltesers 100g", buyElsewhere);
        Assert.Contains("Maltesers 100g", alternatives);
    }

    [Fact]
    public void ApplyPriceRefresh_overwrites_resolved_items()
    {
        var store = NewStore();
        var ledger = store.Load();
        store.Merge(ledger, TestData.SampleResult(), today: "2026-06-23");
        var stale = new List<BuyElsewhereEntry> { ledger.BuyElsewhere[0] }; // Maltesers, paid 1.50, was best 1.00 at Asda

        var items = new List<PriceCheckItem>
        {
            new(0, stale[0].Item, stale[0].PricePaid, stale[0].StorePaid,
                BestPrice: 0.80m, BestPriceStore: "Aldi", Saving: stale[0].PricePaid - 0.80m, Notes: null,
                Outcome: PriceCheckOutcome.CheaperElsewhere)
        };

        var refreshed = store.ApplyPriceRefresh(ledger, stale, items, today: "2026-06-26");

        Assert.Equal(1, refreshed);
        Assert.Equal(0.80m, ledger.BuyElsewhere[0].BestPrice);
        Assert.Equal("Aldi", ledger.BuyElsewhere[0].Where);
        Assert.Equal("2026-06-26", ledger.BuyElsewhere[0].LastSeen);
    }

    [Fact]
    public void ApplyPriceRefresh_drops_entry_when_saving_falls_below_threshold()
    {
        var store = NewStore();
        var ledger = store.Load();
        store.Merge(ledger, TestData.SampleResult(), today: "2026-06-23");
        var stale = new List<BuyElsewhereEntry> { ledger.BuyElsewhere[0] };

        // Price at the paying store rose since — best price now barely beats what was paid.
        var items = new List<PriceCheckItem>
        {
            new(0, stale[0].Item, stale[0].PricePaid, stale[0].StorePaid,
                BestPrice: stale[0].PricePaid - 0.10m, BestPriceStore: "Aldi",
                Saving: 0.10m, Notes: null, Outcome: PriceCheckOutcome.CheaperElsewhere)
        };

        store.ApplyPriceRefresh(ledger, stale, items, today: "2026-06-26");

        Assert.Empty(ledger.BuyElsewhere);
    }

    [Fact]
    public void ApplyPriceRefresh_leaves_unchecked_items_untouched()
    {
        var store = NewStore();
        var ledger = store.Load();
        store.Merge(ledger, TestData.SampleResult(), today: "2026-06-23");
        var stale = new List<BuyElsewhereEntry> { ledger.BuyElsewhere[0] };
        var original = ledger.BuyElsewhere[0];

        var items = new List<PriceCheckItem>
        {
            new(0, stale[0].Item, stale[0].PricePaid, stale[0].StorePaid,
                BestPrice: null, BestPriceStore: null, Saving: null, Notes: "call failed",
                Outcome: PriceCheckOutcome.Unchecked)
        };

        var refreshed = store.ApplyPriceRefresh(ledger, stale, items, today: "2026-06-26");

        Assert.Equal(0, refreshed);
        Assert.Equal(original, ledger.BuyElsewhere[0]);
    }
}
