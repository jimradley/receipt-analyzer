using Microsoft.Extensions.Logging;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;
using ReceiptAnalyzer.Reports;

namespace ReceiptAnalyzer.Jobs;

/// <summary>
/// Runs an <see cref="AnalysisJob"/> through extract → classify → price-check → seasonality →
/// render/persist. Every stage is guarded by its cached result, so resuming a partially-completed
/// job skips work that already succeeded. Each stage is saved immediately after it completes.
/// </summary>
public sealed class AnalysisPipeline
{
    private readonly IAnalysisAgent _agent;
    private readonly LedgerStore _ledger;
    private readonly PriceCacheStore _priceCache;
    private readonly UsageLedgerStore _usageLedger;
    private readonly PurchaseHistoryStore _history;
    private readonly JobStore _store;
    private readonly string _outputDir;
    private readonly JobsOptions _options;
    private readonly ILogger<AnalysisPipeline> _logger;

    public AnalysisPipeline(IAnalysisAgent agent, LedgerStore ledger, PriceCacheStore priceCache,
        UsageLedgerStore usageLedger, PurchaseHistoryStore history, JobStore store, string outputDir,
        JobsOptions options, ILogger<AnalysisPipeline> logger)
    {
        _agent = agent;
        _ledger = ledger;
        _priceCache = priceCache;
        _usageLedger = usageLedger;
        _history = history;
        _store = store;
        _outputDir = outputDir;
        _options = options;
        _logger = logger;
    }

    public async Task ProcessAsync(string id, CancellationToken ct)
    {
        var job = _store.Get(id);
        if (job is null) { _logger.LogWarning("Job {Id} not found.", id); return; }
        if (job.IsTerminal) return;

        var image = _store.GetImage(id);
        if (image is null)
        {
            Fail(job, "Source image missing from job store.");
            return;
        }

        job.Status = JobStatus.Running;
        job.Attempts++;
        job.Error = null;
        _store.Save(job);

        using var usageScope = UsageReporter.BeginScope();
        var persistenceStarted = false;
        try
        {
            // 1. Extract (cached on job.Extraction)
            if (job.Extraction is null)
            {
                var extraction = ModelOutputValidator.Repair(
                    await _agent.ExtractReceiptAsync(image, job.MediaType, ct));
                if (!extraction.IsReceipt)
                {
                    Fail(job, "Photo does not appear to be a receipt.");
                    return;
                }

                // If the line items don't reconcile with the printed total, the vision step most likely
                // dropped rows. Re-read once with a correction hint and keep whichever read is closer.
                extraction = await ReExtractIfItemsMissingAsync(extraction, image, job.MediaType, ct);

                job.Extraction = extraction;
                _store.Save(job);
            }
            // Re-validate cached jobs too: older job records may pre-date this boundary.
            var ext = ModelOutputValidator.Repair(job.Extraction!);
            job.Extraction = ext;

            // 2. Classify
            if (job.Classifications is null)
            {
                var classifications = ModelOutputValidator.Repair(
                    ext.Items, await _agent.ClassifyAsync(ext.Items, ct));
                // Deterministic correction: the model reliably mislabels plain alcohol as ultra-processed.
                job.Classifications = AlcoholNovaGuard.Apply(ext.Items, classifications);
                _store.Save(job);
            }
            job.Classifications = AlcoholNovaGuard.Apply(
                ext.Items, ModelOutputValidator.Repair(ext.Items, job.Classifications!));
            var classByIndex = job.Classifications.Items.ToDictionary(c => c.Index);
            var london = ToLondon(job.CreatedAt);

            // 3. Price check (branded, non-own-label items) — served from the price cache where fresh,
            //    so only cache-misses incur a web search.
            if (!job.PriceChecksDone)
            {
                // The "M " prefix is Morrisons own-label shorthand — only meaningful on a Morrisons
                // receipt. Applying it everywhere silently dropped genuine brands starting "M ".
                var isMorrisons = ext.Retailer.Contains("Morrisons", StringComparison.OrdinalIgnoreCase);
                var branded = ext.Items
                    .Select((item, idx) => (item, idx, c: classByIndex.GetValueOrDefault(idx)))
                    .Where(t => t.c is not null && !t.c.IsOwnLabel &&
                                !(isMorrisons && t.item.Name.StartsWith("M ", StringComparison.OrdinalIgnoreCase)))
                    // Search on the expanded canonical name when the classifier provided one, so cryptic
                    // receipt abbreviations don't make the price-checker give up.
                    .Select(t => new BrandedItemForCheck(
                        t.idx,
                        string.IsNullOrWhiteSpace(t.c!.CanonicalName) ? t.item.Name : t.c!.CanonicalName!,
                        t.item.UnitPrice, ext.Retailer, t.item.Quantity))
                    .ToList();

                if (branded.Count > 0)
                    job.PriceChecks = await PriceCheckWithCacheAsync(branded, london, ct);

                job.PriceChecksDone = true;
                _store.Save(job);
            }

            // 4. Seasonality. Only catalogue-recognised produce reaches the model; the deterministic
            // catalogue remains authoritative for whether the product is actually in season.
            if (!job.SeasonalityDone)
            {
                var produce = ext.Items
                    .Select((item, idx) => (item, idx, c: classByIndex.GetValueOrDefault(idx)))
                    .Where(t => t.c?.NovaLevel == 1 && UkSeasonalityCatalog.TryResolve(t.item.Name, out _))
                    .Select(t => new ProduceItem(t.idx, t.item.Name))
                    .ToList();

                if (produce.Count > 0)
                {
                    SeasonalityResult? modelSeasonality = null;
                    try
                    {
                        modelSeasonality = ModelOutputValidator.Repair(
                            produce, await _agent.AssessSeasonalityAsync(produce, london.Month, ct));
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Seasonality enrichment failed; using the static catalogue."); }
                    var modelByIndex = modelSeasonality?.Items.ToDictionary(x => x.Index)
                        ?? new Dictionary<int, SeasonalityAssessment>();
                    job.Seasonality = new SeasonalityResult(produce
                        .Select(p => UkSeasonalityCatalog.Apply(
                            p, london.Month, modelByIndex.GetValueOrDefault(p.Index)))
                        .ToList());
                }
                job.SeasonalityDone = true;
                _store.Save(job);
            }

            // Fold this attempt's token usage into the job (one entry per stage; re-runs replace).
            MergeUsage(job, UsageReporter.Snapshot());
            job.EstimatedCostGbp = _options.EstimateGbp(job.TokenUsage);

            // 5. Render, write report, merge ledger.
            // Compare against YOUR OWN past prices (distinct from the market price-check above). Computed
            // before the append below and excluding this job, so a shop is never compared against itself.
            var personalPrices = PersonalPriceHistoryBuilder.Build(
                _history.Load(), ext.Items, ext.Retailer,
                ext.ReceiptDate ?? DateOnly.FromDateTime(london), job.Id);

            var result = new AnalysisResult(ext, job.Classifications!, job.PriceChecks, job.Seasonality,
                job.CreatedAt, job.TokenUsage, job.EstimatedCostGbp, personalPrices);
            var markdown = ReportRenderer.Render(result);

            Directory.CreateDirectory(_outputDir);
            var reportDate = ext.ReceiptDate ?? DateOnly.FromDateTime(london);
            var safeRetailer = PathSanitizer.SanitizeFolderName(ext.Retailer);
            var shortId = job.Id.Length >= 8 ? job.Id[..8] : job.Id;
            var fullPath = PathSanitizer.EnsureSafePath(
                _outputDir, $"{reportDate:dd-MMMM-yy}-{safeRetailer}-{shortId}.md");
            persistenceStarted = true;
            await WriteAtomicAsync(fullPath, markdown, ct);

            var ledger = _ledger.Load();
            _ledger.Merge(ledger, result, london.ToString("yyyy-MM-dd"));
            _ledger.Save(ledger);
            _ledger.ReRenderMarkdown(ledger);

            // Durable purchase history for replenishment + longitudinal habit flags — keyed by job id so a
            // re-run replaces, not doubles. Classifications carry NOVA / US-ownership forward per item.
            _history.AppendReceipt(
                job.Id, ext.Retailer, ext.ReceiptDate ?? DateOnly.FromDateTime(london), ext.Items,
                job.Classifications!.Items);

            job.Markdown = markdown;
            job.ReportPath = fullPath;
            job.Retailer = ext.Retailer;
            job.ReceiptDate = ext.ReceiptDate?.ToString("yyyy-MM-dd");
            job.ItemCount = ext.Items.Count;
            // Archive a durable copy of the receipt image before the working image is pruned, so
            // receipts can be browsed and reprocessed later (the working copy below is deleted).
            job.ReceiptImagePath = ArchiveReceiptImage(image, ext.Retailer, reportDate, job.Id, job.MediaType);

            // Durable cost record is part of the idempotent commit. If any persistence step fails,
            // the job remains resumable and repeats these keyed upserts rather than becoming terminal.
            _usageLedger.Upsert(new UsageLedgerEntry(
                job.Id, DateTimeOffset.UtcNow, ext.Retailer, job.TokenUsage.ToList(), job.EstimatedCostGbp));

            job.Status = JobStatus.Completed;
            _store.Save(job);
            _store.DeleteImage(id); // working copy no longer needed once archived + terminal

            _logger.LogInformation("Job {Id} completed → {Path} (est. £{Cost})", id, fullPath, job.EstimatedCostGbp);
        }
        catch (OperationCanceledException)
        {
            // Shutdown: leave the job non-terminal so it resumes on next start.
            job.Status = JobStatus.Queued;
            _store.Save(job);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Id} failed at attempt {Attempt}.", id, job.Attempts);
            if (persistenceStarted)
            {
                job.Status = JobStatus.Queued;
                job.Error = $"Persistence interrupted; safe to retry: {ex.Message}";
                _store.Save(job);
            }
            else
            {
                Fail(job, ex.Message);
            }
        }
    }

    private void Fail(AnalysisJob job, string reason)
    {
        job.Status = JobStatus.Failed;
        job.Error = reason;
        _store.Save(job);
        _store.DeleteImage(job.Id); // terminal — won't resume
    }

    private const string RetryHint =
        "This is a retry — search harder before giving up: try alternative product-name phrasings " +
        "and check trolley.co.uk directly. Report the best price you find even if it is not cheaper " +
        "than what was paid.";

    /// <summary>
    /// Price-checks the branded items, reusing cache entries that are still fresh so only
    /// cache-misses hit the web-search agent (a fully-cached receipt makes no agent call at all).
    /// Misses are checked in small chunks — each gets its own search budget and a failure only
    /// loses its chunk — then unresolved items get one individual retry with a stronger prompt.
    /// Every branded item comes back with an outcome; nothing silently vanishes. Priced and
    /// not-found results are written back to the cache (never errors).
    /// </summary>
    private async Task<PriceCheckResult?> PriceCheckWithCacheAsync(
        IReadOnlyList<BrandedItemForCheck> branded, DateTime london, CancellationToken ct)
    {
        var cache = _priceCache.Load();
        var today = DateOnly.FromDateTime(london.Date);
        var foundCutoff = today.AddDays(-_options.PriceCacheDays);
        var notFoundCutoff = today.AddDays(-_options.PriceCacheNotFoundDays);
        var checkedOn = london.ToString("yyyy-MM-dd");

        var hits = new List<PriceCheckItem>();
        var misses = new List<BrandedItemForCheck>();
        foreach (var b in branded)
        {
            var key = KeyNormaliser.PriceKey(b.Name);
            if (PriceCacheStore.TryGetFresh(cache, key, foundCutoff, notFoundCutoff, out var entry)
                && entry is not null)
            {
                hits.Add(FromCacheEntry(b, entry));
            }
            else
            {
                misses.Add(b);
            }
        }

        // Pass 1: chunked checks.
        var results = new Dictionary<int, PriceCheckItem>();
        var summaries = new List<string>();
        foreach (var chunk in misses.Chunk(Math.Max(1, _options.PriceCheckChunkSize)))
            await CheckChunkAsync(chunk, hint: null, replaceOnlyWithPrice: false, results, summaries, ct);

        // Pass 2: retry unresolved items individually. Errored items first — they were never
        // actually searched — then not-founds, up to the configured cap.
        var retryable = misses
            .Where(b => results[b.Index].Outcome
                is PriceCheckOutcome.Unchecked or PriceCheckOutcome.NotFound)
            .OrderBy(b => results[b.Index].Outcome == PriceCheckOutcome.NotFound ? 1 : 0)
            .Take(_options.PriceCheckRetryMax)
            .ToList();
        foreach (var b in retryable)
            await CheckChunkAsync(new[] { b }, RetryHint, replaceOnlyWithPrice: true, results, summaries, ct);

        _logger.LogInformation(
            "Price check: {Hits} from cache, {Misses} searched ({Retried} retried).",
            hits.Count, misses.Count, retryable.Count);

        // Cache real answers (a price, or a genuine not-found) — never errors, so an unchecked
        // item is re-attempted on the next receipt.
        var cacheable = misses
            .Select(b => results[b.Index])
            .Where(f => f.Outcome != PriceCheckOutcome.Unchecked)
            .ToList();
        if (cacheable.Count > 0)
        {
            PriceCacheStore.Upsert(cache, cacheable.Select(f => new PriceCacheEntry(
                KeyNormaliser.PriceKey(f.Name), f.BestPrice, f.BestPriceStore, f.Notes, checkedOn,
                KeyNormaliser.Product(f.Name), KeyNormaliser.Pack(f.Name), f.Outcome)));
            _priceCache.Save(cache);
        }

        var items = hits.Concat(misses.Select(b => results[b.Index]))
            .OrderBy(i => i.Index)
            .ToList();
        if (items.Count == 0) return null;
        var summary = summaries.Count > 0 ? string.Join("; ", summaries.Distinct()) : null;
        return new PriceCheckResult(items, summary);
    }

    /// <summary>
    /// Runs one agent call for <paramref name="chunk"/> and folds the repaired results into
    /// <paramref name="results"/>. A failed call marks the chunk's items "unchecked" instead of
    /// discarding everything. With <paramref name="replaceOnlyWithPrice"/> (the retry pass), an
    /// existing genuine answer is never downgraded by a retry that produced nothing better.
    /// </summary>
    private async Task CheckChunkAsync(
        IReadOnlyList<BrandedItemForCheck> chunk, string? hint, bool replaceOnlyWithPrice,
        Dictionary<int, PriceCheckItem> results, List<string> summaries, CancellationToken ct)
    {
        PriceCheckResult? fresh = null;
        try
        {
            fresh = ModelOutputValidator.Repair(chunk, await _agent.PriceCheckAsync(chunk, ct, hint));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Price check call failed for {Count} item(s); continuing.", chunk.Count);
        }

        foreach (var b in chunk)
        {
            var item = fresh?.Items.FirstOrDefault(i => i.Index == b.Index)
                ?? new PriceCheckItem(b.Index, b.Name, b.PricePaid, b.Retailer,
                    null, null, null, "Price check failed.", PriceCheckOutcome.Unchecked, b.Quantity);

            if (replaceOnlyWithPrice && item.BestPrice is null
                && results.TryGetValue(b.Index, out var existing)
                && existing.Outcome != PriceCheckOutcome.Unchecked)
            {
                continue; // keep the pass-1 answer; the retry found nothing better
            }
            results[b.Index] = item;
        }

        if (!string.IsNullOrWhiteSpace(fresh?.SkippedSummary))
            summaries.Add(fresh.SkippedSummary);
    }

    /// <summary>Maps a fresh cache entry onto this receipt's item (saving/outcome are per-receipt).</summary>
    private static PriceCheckItem FromCacheEntry(BrandedItemForCheck b, PriceCacheEntry entry)
    {
        var saving = entry.BestPrice is { } best ? b.PricePaid - best : (decimal?)null;
        // Legacy rows (null Outcome) with no price meant "checked, nothing cheaper" — closest to
        // already-best. Rows written by this version carry an explicit outcome.
        var outcome = entry.BestPrice is not null
            ? (saving > 0 ? PriceCheckOutcome.CheaperElsewhere : PriceCheckOutcome.AlreadyBest)
            : entry.Outcome == PriceCheckOutcome.NotFound
                ? PriceCheckOutcome.NotFound
                : PriceCheckOutcome.AlreadyBest;
        return new PriceCheckItem(
            b.Index, b.Name, b.PricePaid, b.Retailer,
            entry.BestPrice, entry.BestPriceStore, saving, entry.Notes, outcome, b.Quantity);
    }

    /// <summary>
    /// Adds this attempt's per-stage usage to the job, replacing any prior entry for the same
    /// stage. Multiple calls within one attempt (chunked price checks, a re-extraction) are summed
    /// per stage+model rather than overwriting each other.
    /// </summary>
    private static void MergeUsage(AnalysisJob job, IReadOnlyList<StageUsage> attempt)
    {
        foreach (var stage in attempt.GroupBy(u => u.Stage))
        {
            job.TokenUsage.RemoveAll(e => e.Stage == stage.Key);
            foreach (var model in stage.GroupBy(u => u.Model))
            {
                job.TokenUsage.Add(new StageUsage(stage.Key, model.Key,
                    model.Sum(u => u.InputTokens), model.Sum(u => u.OutputTokens),
                    model.Sum(u => u.CacheReadTokens), model.Sum(u => u.CacheCreationTokens)));
            }
        }
    }

    /// <summary>
    /// When the first extraction's line items don't reconcile with the receipt's printed total, the
    /// read may have missed rows or discount/multibuy accounting. Re-asks once with a neutral correction hint and keeps
    /// whichever read lands closer to the printed figure. Best-effort: a failed retry keeps the first read.
    /// </summary>
    private async Task<ReceiptExtraction> ReExtractIfItemsMissingAsync(
        ReceiptExtraction first, byte[] image, string mediaType, CancellationToken ct)
    {
        var check = ReceiptMath.Check(first);
        if (check.Reconciles != false || check.Reference is not { } reference || check.Delta is not { } delta)
            return first; // reconciles, or no printed total to reconcile against

        var hint =
            $"Your previous read listed {first.Items.Count} item(s) totalling £{check.SumOfItems:F2}, " +
            $"but the receipt's {check.ReferenceLabel} is £{reference:F2} (off by £{delta:F2}). " +
            "The difference may be a missed line, discount, weighted item, or multibuy accounting. " +
            "Re-read the receipt carefully from top to bottom. Return only printed line items and preserve " +
            "discount/multibuy lines; do not invent an item merely to force the arithmetic to match.";

        try
        {
            var retry = ModelOutputValidator.Repair(
                await _agent.ExtractReceiptAsync(image, mediaType, ct, hint));
            if (!retry.IsReceipt) return first;

            var retryDelta = ReceiptMath.Check(retry).Delta is { } d ? Math.Abs(d) : decimal.MaxValue;
            if (retryDelta < Math.Abs(delta))
            {
                _logger.LogInformation(
                    "Re-extraction improved reconciliation: {OldItems}→{NewItems} items, |delta| £{OldDelta}→£{NewDelta}.",
                    first.Items.Count, retry.Items.Count, Math.Abs(delta), retryDelta);
                return retry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-extraction attempt failed; keeping the first read.");
        }
        return first;
    }

    /// <summary>
    /// Writes a durable copy of the receipt image into <c>{outputDir}/Receipts</c>, named by date +
    /// retailer + short job id so it's browsable and matchable to its report. Best-effort: a failure
    /// here is logged but never fails the (already successful) analysis. Returns the path relative to
    /// the output dir, or null if it couldn't be written.
    /// </summary>
    private string? ArchiveReceiptImage(byte[] image, string retailer, DateOnly date, string jobId, string mediaType)
    {
        try
        {
            var dir = Path.Combine(_outputDir, "Receipts");
            Directory.CreateDirectory(dir);
            var safeRetailer = PathSanitizer.SanitizeFolderName(retailer);
            var shortId = jobId.Length >= 8 ? jobId[..8] : jobId;
            var fileName = $"{date:dd-MMMM-yy}-{safeRetailer}-{shortId}{ExtensionFor(mediaType)}";
            var path = PathSanitizer.EnsureSafePath(dir, fileName);
            File.WriteAllBytes(path, image);
            return Path.Combine("Receipts", fileName);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not archive receipt image for job {Id}.", jobId);
            return null;
        }
    }

    private static string ExtensionFor(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };

    private static DateTime ToLondon(DateTimeOffset now)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
            return TimeZoneInfo.ConvertTime(now, tz).DateTime;
        }
        catch (TimeZoneNotFoundException)
        {
            return now.LocalDateTime;
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, true);
    }
}
