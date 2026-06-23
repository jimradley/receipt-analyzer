using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Jobs;

public enum JobStatus { Queued, Running, Completed, Failed }

/// <summary>
/// A single receipt analysis, persisted so it survives restarts. Each pipeline stage caches its
/// output here; a stage with a non-null result is skipped on resume (so a crash or client timeout
/// never re-charges an LLM call that already succeeded). The Id is a content hash of the uploaded
/// image, which makes re-uploading the same receipt idempotent.
/// </summary>
public sealed class AnalysisJob
{
    public required string Id { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public required string MediaType { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Attempts { get; set; }
    public string? Error { get; set; }

    // Cached stage outputs — null until that stage has completed.
    public ReceiptExtraction? Extraction { get; set; }
    public ItemClassifications? Classifications { get; set; }
    public PriceCheckResult? PriceChecks { get; set; }
    public bool PriceChecksDone { get; set; }      // distinguishes "skipped/failed" from "ran, nothing to check"
    public SeasonalityResult? Seasonality { get; set; }
    public bool SeasonalityDone { get; set; }

    // Token telemetry, accumulated across stages (one entry per stage; re-running a stage replaces it).
    public List<StageUsage> TokenUsage { get; set; } = new();
    public decimal? EstimatedCostGbp { get; set; }

    // Final output.
    public string? Markdown { get; set; }
    public string? ReportPath { get; set; }
    public string? Retailer { get; set; }
    public string? ReceiptDate { get; set; }
    public int ItemCount { get; set; }

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed;
}
