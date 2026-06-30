using Microsoft.Extensions.Logging.Abstractions;
using ReceiptAnalyzer.Jobs;
using ReceiptAnalyzer.Ledger;
using ReceiptAnalyzer.Reports;

namespace ReceiptAnalyzer.Tests;

public class AnalysisPipelineTests : IDisposable
{
    private readonly string _dir;
    private readonly JobStore _store;
    private readonly FakeAgent _agent = new();

    public AnalysisPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ra-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new JobStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly JobsOptions Options = new()
    {
        UsdToGbp = 0.80m,
        Pricing = new Dictionary<string, ReceiptAnalyzer.Agent.ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            // FakeAgent reports model "test-model"; give it round rates for assertions.
            ["test-model"] = new(InputPerMTok: 1.00m, OutputPerMTok: 2.00m, CacheReadPerMTok: 0m, CacheWritePerMTok: 0m),
        },
    };

    private AnalysisPipeline NewPipeline() => new(
        _agent,
        new LedgerStore(_dir, NullLogger<LedgerStore>.Instance),
        new PriceCacheStore(_dir),
        new UsageLedgerStore(_dir),
        new PurchaseHistoryStore(_dir, NullLogger<PurchaseHistoryStore>.Instance),
        _store,
        _dir,
        Options,
        NullLogger<AnalysisPipeline>.Instance);

    private static byte[] Img(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Full_run_completes_and_writes_report_and_ledgers()
    {
        var (job, _) = _store.GetOrCreate(Img("full"), "image/jpeg");

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        var done = _store.Get(job.Id)!;
        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.False(string.IsNullOrEmpty(done.Markdown));
        Assert.Equal(1, _agent.ExtractCalls);
        Assert.Equal(1, _agent.ClassifyCalls);
        Assert.True(File.Exists(Path.Combine(_dir, ReceiptAnalyzer.Reports.ReportLibrary.BuyElsewhereFile)));
    }

    [Fact]
    public async Task Resume_skips_stages_already_cached_so_no_recharge()
    {
        // Simulate a crash after extract+classify succeeded but before price/seasonality.
        var (job, _) = _store.GetOrCreate(Img("resume"), "image/jpeg");
        var sample = TestData.SampleResult();
        job.Extraction = sample.Extraction;
        job.Classifications = sample.Classifications;
        job.Status = JobStatus.Queued;
        _store.Save(job);

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(0, _agent.ExtractCalls);    // not re-charged
        Assert.Equal(0, _agent.ClassifyCalls);   // not re-charged
        Assert.Equal(1, _agent.PriceCheckCalls); // remaining stages run
        Assert.Equal(1, _agent.SeasonalityCalls);
        Assert.Equal(JobStatus.Completed, _store.Get(job.Id)!.Status);
    }

    [Fact]
    public async Task Full_run_records_token_usage_and_estimated_cost()
    {
        var (job, _) = _store.GetOrCreate(Img("usage"), "image/jpeg");

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        var done = _store.Get(job.Id)!;
        Assert.Equal(4, done.TokenUsage.Count); // extract, classify, price-check, seasonality
        // 4 stages × (1000 in × $1 + 500 out × $2) / 1e6 = $0.008 USD; × 0.80 = £0.0064
        Assert.Equal(0.0064m, done.EstimatedCostGbp);
    }

    [Fact]
    public async Task Completing_a_job_deletes_its_source_image()
    {
        var (job, _) = _store.GetOrCreate(Img("cleanup"), "image/jpeg");
        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);
        Assert.Null(_store.GetImage(job.Id));
    }

    [Fact]
    public async Task Completing_a_job_archives_a_durable_copy_of_the_receipt_image()
    {
        var (job, _) = _store.GetOrCreate(Img("archive-me"), "image/jpeg");
        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        var done = _store.Get(job.Id)!;
        Assert.NotNull(done.ReceiptImagePath);
        Assert.StartsWith("Receipts", done.ReceiptImagePath);

        // The archived file exists under {outputDir}/Receipts and still holds the original bytes,
        // even though the working image has been pruned.
        var archived = Path.Combine(_dir, done.ReceiptImagePath!);
        Assert.True(File.Exists(archived));
        Assert.Equal(Img("archive-me"), await File.ReadAllBytesAsync(archived));
        Assert.Null(_store.GetImage(job.Id)); // working copy still pruned
    }

    [Fact]
    public async Task Same_day_receipts_get_unique_report_files()
    {
        var pipeline = NewPipeline();
        var (first, _) = _store.GetOrCreate(Img("same-day-one"), "image/jpeg");
        var (second, _) = _store.GetOrCreate(Img("same-day-two"), "image/jpeg");

        await pipeline.ProcessAsync(first.Id, CancellationToken.None);
        await pipeline.ProcessAsync(second.Id, CancellationToken.None);

        var firstPath = _store.Get(first.Id)!.ReportPath;
        var secondPath = _store.Get(second.Id)!.ReportPath;
        Assert.NotEqual(firstPath, secondPath);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task Terminal_job_is_a_no_op()
    {
        var (job, _) = _store.GetOrCreate(Img("terminal"), "image/jpeg");
        job.Status = JobStatus.Completed;
        _store.Save(job);

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(0, _agent.ExtractCalls);
        Assert.Equal(0, _agent.ClassifyCalls);
    }

    [Fact]
    public async Task Second_receipt_with_same_items_is_fully_served_from_the_price_cache()
    {
        var pipeline = NewPipeline();

        var (job1, _) = _store.GetOrCreate(Img("first-shop"), "image/jpeg");
        await pipeline.ProcessAsync(job1.Id, CancellationToken.None);
        Assert.Equal(1, _agent.PriceCheckCalls); // first shop populates the cache

        // FakeAgent extracts the same items regardless of image, so the second receipt is all cache hits.
        var (job2, _) = _store.GetOrCreate(Img("second-shop"), "image/jpeg");
        await pipeline.ProcessAsync(job2.Id, CancellationToken.None);

        Assert.Equal(1, _agent.PriceCheckCalls); // still 1 — no new web search
        var done2 = _store.Get(job2.Id)!;
        Assert.Equal(JobStatus.Completed, done2.Status);
        Assert.NotNull(done2.PriceChecks);
        // Saving recomputed against this receipt's price-paid: Maltesers £1.50 paid − £1.00 cached = £0.50.
        var maltesers = done2.PriceChecks!.Items.Single(i => i.Index == 1);
        Assert.Equal(0.50m, maltesers.Saving);
        Assert.DoesNotContain(done2.TokenUsage, u => u.Stage == "price-check"); // no price-check tokens charged
    }

    [Fact]
    public async Task Only_cache_misses_are_sent_to_the_price_check_agent()
    {
        // Pre-seed the cache with one of the two branded items (Maltesers), leaving Bananas a miss.
        var seeded = new PriceCacheData();
        PriceCacheStore.Upsert(seeded, new[]
        {
            new PriceCacheEntry(KeyNormaliser.Normalise("Maltesers 100g"), 1.00m, "Asda", null,
                DateTime.UtcNow.ToString("yyyy-MM-dd")),
        });
        new PriceCacheStore(_dir).Save(seeded);

        var (job, _) = _store.GetOrCreate(Img("partial"), "image/jpeg");
        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(1, _agent.PriceCheckCalls);
        Assert.Single(_agent.LastPriceCheckItems);
        Assert.Equal("Bananas Loose", _agent.LastPriceCheckItems[0].Name); // Maltesers served from cache
    }

    [Fact]
    public async Task Non_receipt_image_fails_the_job_with_a_reason()
    {
        _agent.IsReceipt = false;
        var (job, _) = _store.GetOrCreate(Img("not-a-receipt"), "image/jpeg");

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        var failed = _store.Get(job.Id)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains("receipt", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _agent.ClassifyCalls);   // stopped after extraction
    }
}
