using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReceiptAnalyzer.Jobs;

/// <summary>
/// Background processor: drains the job queue one at a time through the pipeline. On startup it
/// re-enqueues any non-terminal jobs left behind by a previous run so they resume from where they
/// stopped.
/// </summary>
public sealed class AnalysisWorker : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly JobStore _store;
    private readonly AnalysisPipeline _pipeline;
    private readonly JobsOptions _options;
    private readonly ILogger<AnalysisWorker> _logger;

    public AnalysisWorker(IJobQueue queue, JobStore store, AnalysisPipeline pipeline,
        JobsOptions options, ILogger<AnalysisWorker> logger)
    {
        _queue = queue;
        _store = store;
        _pipeline = pipeline;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pruned = _store.PruneTerminal(_options.Retention);
        if (pruned > 0) _logger.LogInformation("Pruned {Count} terminal job records older than {Days}d.",
            pruned, _options.Retention.TotalDays);

        foreach (var job in _store.Resumable())
        {
            _logger.LogInformation("Resuming job {Id} (status {Status}).", job.Id, job.Status);
            await _queue.EnqueueAsync(job.Id, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            string id;
            try { id = await _queue.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                await _pipeline.ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // shutting down; pipeline already re-queued the job state
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing job {Id}.", id);
            }
        }
    }
}
