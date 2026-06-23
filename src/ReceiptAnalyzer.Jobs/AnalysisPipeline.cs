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
    private readonly JobStore _store;
    private readonly string _outputDir;
    private readonly JobsOptions _options;
    private readonly ILogger<AnalysisPipeline> _logger;

    public AnalysisPipeline(IAnalysisAgent agent, LedgerStore ledger, JobStore store,
        string outputDir, JobsOptions options, ILogger<AnalysisPipeline> logger)
    {
        _agent = agent;
        _ledger = ledger;
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
        try
        {
            // 1. Extract (cached on job.Extraction)
            if (job.Extraction is null)
            {
                var extraction = await _agent.ExtractReceiptAsync(image, job.MediaType, ct);
                if (!extraction.IsReceipt)
                {
                    Fail(job, "Photo does not appear to be a receipt.");
                    return;
                }
                job.Extraction = extraction;
                _store.Save(job);
            }
            var ext = job.Extraction!;

            // 2. Classify
            if (job.Classifications is null)
            {
                job.Classifications = await _agent.ClassifyAsync(ext.Items, ct);
                _store.Save(job);
            }
            var classByIndex = job.Classifications!.Items.ToDictionary(c => c.Index);

            // 3. Price check (branded, non-own-label items)
            if (!job.PriceChecksDone)
            {
                var branded = ext.Items
                    .Select((item, idx) => (item, idx, c: classByIndex.GetValueOrDefault(idx)))
                    .Where(t => t.c is not null && !t.c.IsOwnLabel &&
                                !t.item.Name.StartsWith("M ", StringComparison.OrdinalIgnoreCase))
                    .Select(t => new BrandedItemForCheck(t.idx, t.item.Name, t.item.UnitPrice, ext.Retailer))
                    .ToList();

                if (branded.Count > 0)
                {
                    try { job.PriceChecks = await _agent.PriceCheckAsync(branded, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Price check failed; continuing without it."); }
                }
                job.PriceChecksDone = true;
                _store.Save(job);
            }

            // 4. Seasonality (NOVA-1 produce)
            var london = ToLondon(job.CreatedAt);
            if (!job.SeasonalityDone)
            {
                var produce = ext.Items
                    .Select((item, idx) => (item, idx, c: classByIndex.GetValueOrDefault(idx)))
                    .Where(t => t.c?.NovaLevel == 1)
                    .Select(t => new ProduceItem(t.idx, t.item.Name))
                    .ToList();

                if (produce.Count > 0)
                {
                    try { job.Seasonality = await _agent.AssessSeasonalityAsync(produce, london.Month, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Seasonality check failed; continuing without it."); }
                }
                job.SeasonalityDone = true;
                _store.Save(job);
            }

            // Fold this attempt's token usage into the job (one entry per stage; re-runs replace).
            MergeUsage(job, UsageReporter.Snapshot());
            job.EstimatedCostGbp = _options.EstimateGbp(job.TokenUsage);

            // 5. Render, write report, merge ledger
            var result = new AnalysisResult(ext, job.Classifications!, job.PriceChecks, job.Seasonality,
                job.CreatedAt, job.TokenUsage, job.EstimatedCostGbp);
            var markdown = ReportRenderer.Render(result);

            Directory.CreateDirectory(_outputDir);
            var fullPath = PathSanitizer.EnsureSafePath(_outputDir, $"{london:dd-MMMM-yy}.md");
            await WriteAtomicAsync(fullPath, markdown, ct);

            var ledger = _ledger.Load();
            _ledger.Merge(ledger, result, london.ToString("yyyy-MM-dd"));
            _ledger.Save(ledger);
            _ledger.ReRenderMarkdown(ledger);

            job.Markdown = markdown;
            job.ReportPath = fullPath;
            job.Retailer = ext.Retailer;
            job.ReceiptDate = ext.ReceiptDate?.ToString("yyyy-MM-dd");
            job.ItemCount = ext.Items.Count;
            job.Status = JobStatus.Completed;
            _store.Save(job);
            _store.DeleteImage(id); // no longer needed once terminal
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
            Fail(job, ex.Message);
        }
    }

    private void Fail(AnalysisJob job, string reason)
    {
        job.Status = JobStatus.Failed;
        job.Error = reason;
        _store.Save(job);
        _store.DeleteImage(job.Id); // terminal — won't resume
    }

    /// <summary>Adds this attempt's per-stage usage to the job, replacing any prior entry for the same stage.</summary>
    private static void MergeUsage(AnalysisJob job, IReadOnlyList<StageUsage> attempt)
    {
        foreach (var u in attempt)
        {
            job.TokenUsage.RemoveAll(e => e.Stage == u.Stage);
            job.TokenUsage.Add(u);
        }
    }

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
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
