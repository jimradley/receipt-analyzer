using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Reports;

namespace ReceiptAnalyzer.Tests;

public class UsageLedgerTests : IDisposable
{
    private readonly string _dir;

    public UsageLedgerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ra-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static UsageLedgerEntry Entry(string id, DateTimeOffset at, decimal cost, params StageUsage[] usage) =>
        new(id, at, "Morrisons", usage, cost);

    [Fact]
    public void Upsert_is_idempotent_by_job_id()
    {
        var store = new UsageLedgerStore(_dir);
        var at = DateTimeOffset.UtcNow;
        store.Upsert(Entry("job-1", at, 0.10m, new StageUsage("extract", "m1", 1000, 500, 0, 0)));
        store.Upsert(Entry("job-1", at, 0.25m, new StageUsage("extract", "m1", 2000, 800, 0, 0))); // re-analysis

        var all = store.All();
        Assert.Single(all);
        Assert.Equal(0.25m, all[0].CostGbp); // latest wins; no double count
    }

    [Fact]
    public void Summarise_buckets_spend_into_today_month_and_all_time()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<UsageLedgerEntry>
        {
            Entry("today",      now,                 0.10m, new StageUsage("extract", "m1", 1000, 500, 0, 0)),
            Entry("this-month", now.AddDays(-3),     0.20m, new StageUsage("classify", "m1", 1000, 500, 0, 0)),
            Entry("old",        now.AddMonths(-2),   0.40m, new StageUsage("extract", "m2", 100, 50, 0, 0)),
        };

        var pricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["m1"] = new(InputPerMTok: 1.00m, OutputPerMTok: 2.00m, CacheReadPerMTok: 0m, CacheWritePerMTok: 0m),
        };

        var s = UsageAggregator.Summarise(entries, pricing, usdToGbp: 0.80m, nowUtc: now);

        Assert.Equal(0.10m, s.TodayGbp);
        Assert.Equal(0.30m, s.MonthGbp);   // today + this-month
        Assert.Equal(0.70m, s.AllTimeGbp); // all three
        Assert.Equal(3, s.ReceiptCount);
    }

    [Fact]
    public void Summarise_groups_tokens_and_cost_by_model()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var entries = new List<UsageLedgerEntry>
        {
            Entry("a", now, 0.10m,
                new StageUsage("extract", "m1", 1000, 500, 0, 0),
                new StageUsage("classify", "m1", 1000, 500, 0, 0)),
            Entry("b", now, 0.05m, new StageUsage("extract", "m2", 200, 100, 0, 0)),
        };

        var pricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["m1"] = new(InputPerMTok: 1.00m, OutputPerMTok: 2.00m, CacheReadPerMTok: 0m, CacheWritePerMTok: 0m),
        };

        var s = UsageAggregator.Summarise(entries, pricing, usdToGbp: 0.80m, nowUtc: now);

        var m1 = s.ByModel.Single(m => m.Model == "m1");
        Assert.Equal(2000, m1.InputTokens);
        Assert.Equal(1000, m1.OutputTokens);
        // (2000 × $1 + 1000 × $2) / 1e6 = $0.004 USD × 0.80 = £0.0032
        Assert.Equal(0.0032m, m1.Gbp);

        var m2 = s.ByModel.Single(m => m.Model == "m2");
        Assert.Null(m2.Gbp); // no rates for m2 → no estimate
    }
}
