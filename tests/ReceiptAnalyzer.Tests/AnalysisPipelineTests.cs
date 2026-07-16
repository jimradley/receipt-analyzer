using Microsoft.Extensions.Logging.Abstractions;
using ReceiptAnalyzer.Agent;
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

    private static JobsOptions OptionsWith(int chunkSize = 4, int retryMax = 8) => new()
    {
        UsdToGbp = 0.80m,
        PriceCheckChunkSize = chunkSize,
        PriceCheckRetryMax = retryMax,
        Pricing = Options.Pricing,
    };

    private AnalysisPipeline NewPipeline(JobsOptions? options = null) => new(
        _agent,
        new LedgerStore(_dir, NullLogger<LedgerStore>.Instance),
        new PriceCacheStore(_dir),
        new UsageLedgerStore(_dir),
        new PurchaseHistoryStore(_dir, NullLogger<PurchaseHistoryStore>.Instance),
        _store,
        _dir,
        options ?? Options,
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

    [Fact]
    public async Task Price_checks_run_in_chunks()
    {
        var (job, _) = _store.GetOrCreate(Img("chunked"), "image/jpeg");

        await NewPipeline(OptionsWith(chunkSize: 1)).ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(2, _agent.PriceCheckCalls); // 2 branded items, 1 per chunk
        Assert.All(_agent.PriceCheckCallItems, chunk => Assert.Single(chunk));
        Assert.All(_agent.PriceCheckHints, Assert.Null); // first pass carries no retry hint
        Assert.Equal(JobStatus.Completed, _store.Get(job.Id)!.Status);
    }

    [Fact]
    public async Task A_failed_chunk_only_loses_its_own_items_and_is_retried_individually()
    {
        _agent.ThrowOnPriceCheckCalls.Add(1); // first chunk (Bananas) dies
        var (job, _) = _store.GetOrCreate(Img("chunk-fail"), "image/jpeg");

        await NewPipeline(OptionsWith(chunkSize: 1)).ProcessAsync(job.Id, CancellationToken.None);

        // 2 chunk calls + 1 individual retry of the failed item — the other chunk survived.
        Assert.Equal(3, _agent.PriceCheckCalls);
        Assert.NotNull(_agent.PriceCheckHints[2]); // retry carries the "search harder" hint
        var checks = _store.Get(job.Id)!.PriceChecks!;
        Assert.Equal(2, checks.Items.Count);
        Assert.All(checks.Items, i => Assert.NotNull(i.BestPrice)); // both resolved in the end
    }

    [Fact]
    public async Task Not_found_items_get_one_individual_retry_with_a_hint()
    {
        _agent.NotFoundUntilHinted.Add(1); // Maltesers comes back not-found on the first pass
        var (job, _) = _store.GetOrCreate(Img("retry-notfound"), "image/jpeg");

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(2, _agent.PriceCheckCalls); // one chunk + one individual retry
        Assert.Null(_agent.PriceCheckHints[0]);
        Assert.NotNull(_agent.PriceCheckHints[1]);
        var maltesers = _store.Get(job.Id)!.PriceChecks!.Items.Single(i => i.Index == 1);
        Assert.Equal(PriceCheckOutcome.CheaperElsewhere, maltesers.Outcome);
        Assert.Equal(0.50m, maltesers.Saving);
    }

    [Fact]
    public async Task With_retries_disabled_a_not_found_is_kept_and_cached_with_its_outcome()
    {
        _agent.NotFoundUntilHinted.Add(1);
        var (job, _) = _store.GetOrCreate(Img("notfound-cached"), "image/jpeg");

        await NewPipeline(OptionsWith(retryMax: 0)).ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(1, _agent.PriceCheckCalls);
        var maltesers = _store.Get(job.Id)!.PriceChecks!.Items.Single(i => i.Index == 1);
        Assert.Equal(PriceCheckOutcome.NotFound, maltesers.Outcome);

        var cache = new PriceCacheStore(_dir).Load();
        Assert.Contains(cache.Entries, e => e.Outcome == PriceCheckOutcome.NotFound && e.BestPrice is null);
        // Bananas were priced above what was paid — the market price is cached as "already best".
        Assert.Contains(cache.Entries, e => e.Outcome == PriceCheckOutcome.AlreadyBest && e.BestPrice == 1.00m);
    }

    [Fact]
    public async Task An_omitted_item_is_backfilled_as_unchecked_and_never_cached()
    {
        _agent.OmitFromPriceCheck.Add(1); // model drops Maltesers from every response
        var (job, _) = _store.GetOrCreate(Img("omitted"), "image/jpeg");

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(2, _agent.PriceCheckCalls); // chunk + individual retry (still omitted)
        var maltesers = _store.Get(job.Id)!.PriceChecks!.Items.Single(i => i.Index == 1);
        Assert.Equal(PriceCheckOutcome.Unchecked, maltesers.Outcome);

        // Errors are never cached, so the item is re-attempted on the next receipt.
        var cache = new PriceCacheStore(_dir).Load();
        Assert.Single(cache.Entries); // only Bananas
    }

    [Fact]
    public async Task M_prefixed_items_are_price_checked_outside_morrisons()
    {
        var (job, _) = _store.GetOrCreate(Img("m-waitrose"), "image/jpeg");
        SeedMItemReceipt(job, retailer: "Waitrose");

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(1, _agent.PriceCheckCalls);
        Assert.Equal("M Signature Pale Ale", Assert.Single(_agent.LastPriceCheckItems).Name);
    }

    [Fact]
    public async Task M_prefixed_items_stay_excluded_on_a_morrisons_receipt()
    {
        var (job, _) = _store.GetOrCreate(Img("m-morrisons"), "image/jpeg");
        SeedMItemReceipt(job, retailer: "Morrisons");

        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(0, _agent.PriceCheckCalls);
        Assert.Null(_store.Get(job.Id)!.PriceChecks);
    }

    /// <summary>A receipt whose only item is a genuine brand printed with an "M " prefix.</summary>
    private void SeedMItemReceipt(AnalysisJob job, string retailer)
    {
        var sample = TestData.SampleResult();
        job.Extraction = sample.Extraction with
        {
            Retailer = retailer,
            Items = new List<RawItem> { new("M Signature Pale Ale", 1m, 2.50m, 2.50m) },
            PrintedSubtotal = 2.50m,
            PrintedTotal = 2.50m,
            Savings = 0m,
        };
        job.Classifications = new ItemClassifications(new List<ItemClassification>
        {
            new(0, 4, false, null, null, IsOwnLabel: false, SwapSuggestion: null, Notes: null),
        });
        job.Status = JobStatus.Queued;
        _store.Save(job);
    }

    [Fact]
    public async Task Chunked_price_check_usage_is_summed_into_one_stage_entry()
    {
        var (job, _) = _store.GetOrCreate(Img("usage-chunks"), "image/jpeg");

        await NewPipeline(OptionsWith(chunkSize: 1)).ProcessAsync(job.Id, CancellationToken.None);

        var done = _store.Get(job.Id)!;
        Assert.Equal(4, done.TokenUsage.Count); // still one entry per stage
        var priceCheck = done.TokenUsage.Single(u => u.Stage == "price-check");
        Assert.Equal(2000, priceCheck.InputTokens);  // two calls of 1000 each, summed
        Assert.Equal(1000, priceCheck.OutputTokens);
        // 5 calls × (1000 in × $1 + 500 out × $2) / 1e6 = $0.01 USD; × 0.80 = £0.008
        Assert.Equal(0.008m, done.EstimatedCostGbp);
    }

    [Fact]
    public async Task Item_count_mismatch_triggers_a_re_extraction_even_when_totals_reconcile()
    {
        // Totals reconcile on the first read, but the printed item count (5) doesn't match the
        // summed quantities (3) — that alone must trigger a retry.
        var itemsA = new List<RawItem> { new("Item A", 3m, 1m, 3m) };
        var extA = new ReceiptExtraction("Morrisons", new DateOnly(2026, 7, 16), itemsA, 3m, 3m, null, true, 0.9, null, PrintedItemCount: 5);
        var itemsB = new List<RawItem> { new("Item A", 5m, 1m, 5m) };
        var extB = new ReceiptExtraction("Morrisons", new DateOnly(2026, 7, 16), itemsB, 5m, 5m, null, true, 0.9, null, PrintedItemCount: 5);
        _agent.ExtractionSequence = new List<ReceiptExtraction> { extA, extB };

        var (job, _) = _store.GetOrCreate(Img("count-mismatch"), "image/jpeg");
        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(2, _agent.ExtractCalls);
        Assert.Null(_agent.ExtractHints[0]);
        Assert.Contains("number of items", _agent.ExtractHints[1], StringComparison.OrdinalIgnoreCase);

        var done = _store.Get(job.Id)!;
        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.Equal(5, done.Extraction!.Items.Sum(i => i.Quantity)); // kept the read whose count matches
    }

    [Fact]
    public async Task A_persistent_mismatch_stops_after_the_retry_cap_and_never_hard_fails()
    {
        var items = new List<RawItem> { new("Item A", 3m, 1m, 3m) };
        var ext = new ReceiptExtraction("Morrisons", new DateOnly(2026, 7, 16), items, 3m, 3m, null, true, 0.9, null, PrintedItemCount: 5);
        _agent.ExtractionSequence = new List<ReceiptExtraction> { ext, ext, ext };

        var (job, _) = _store.GetOrCreate(Img("persistent-mismatch"), "image/jpeg");
        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(3, _agent.ExtractCalls); // 1 initial + capped at 2 retries
        Assert.Equal(JobStatus.Completed, _store.Get(job.Id)!.Status);
    }

    [Fact]
    public async Task Large_unreconciled_mismatch_suppresses_ledger_and_price_cache_writes()
    {
        // Items sum to £4.69 but the printed subtotal claims £40 — a mismatch far beyond a missed
        // line or discount, per ReportRenderer.IsLargeMismatch. Garbage like this must not poison
        // the buy-elsewhere ledger or the price cache, even though the report still renders.
        var badExtraction = TestData.SampleResult().Extraction with { PrintedSubtotal = 40.00m, PrintedTotal = 40.00m, Savings = null };
        _agent.ExtractionSequence = new List<ReceiptExtraction> { badExtraction, badExtraction, badExtraction };

        var (job, _) = _store.GetOrCreate(Img("large-mismatch"), "image/jpeg");
        await NewPipeline().ProcessAsync(job.Id, CancellationToken.None);

        var done = _store.Get(job.Id)!;
        Assert.Equal(JobStatus.Completed, done.Status); // still completes — never hard-fails
        Assert.Contains("re-shoot", done.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_dir, ReceiptAnalyzer.Reports.ReportLibrary.BuyElsewhereFile)));
        Assert.True(_agent.PriceCheckCalls > 0); // price-check still runs for the report...
        Assert.Empty(new PriceCacheStore(_dir).Load().Entries); // ...but nothing is written to the durable cache
    }
}
