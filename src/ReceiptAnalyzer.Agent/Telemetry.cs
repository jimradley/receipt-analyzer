namespace ReceiptAnalyzer.Agent;

/// <summary>Token usage for a single LLM call in the pipeline.</summary>
public sealed record StageUsage(
    string Stage,
    string Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens);

/// <summary>Per-million-token rates (USD) for one model.</summary>
public sealed record ModelPricing(
    decimal InputPerMTok,
    decimal OutputPerMTok,
    decimal CacheReadPerMTok,
    decimal CacheWritePerMTok);

/// <summary>
/// Ambient, async-flowing collector for token usage. The pipeline opens a scope around a job;
/// the agents call <see cref="Report"/> after each LLM response. This keeps usage capture out of
/// the <c>IAnalysisAgent</c> return types.
/// </summary>
public static class UsageReporter
{
    private static readonly AsyncLocal<List<StageUsage>?> Sink = new();

    public static IDisposable BeginScope() => new Scope();

    public static void Report(StageUsage usage) => Sink.Value?.Add(usage);

    public static IReadOnlyList<StageUsage> Snapshot() =>
        Sink.Value is { } list ? list.ToArray() : Array.Empty<StageUsage>();

    private sealed class Scope : IDisposable
    {
        private readonly List<StageUsage>? _previous;
        public Scope() { _previous = Sink.Value; Sink.Value = new List<StageUsage>(); }
        public void Dispose() => Sink.Value = _previous;
    }
}

public static class UsageCost
{
    /// <summary>Total USD cost for the given usage, or null if no rates cover any of the models seen.</summary>
    public static decimal? EstimateUsd(
        IReadOnlyList<StageUsage> usages, IReadOnlyDictionary<string, ModelPricing> pricing)
    {
        decimal total = 0m;
        var matched = false;
        foreach (var u in usages)
        {
            if (!pricing.TryGetValue(u.Model, out var p)) continue;
            matched = true;
            total += (u.InputTokens * p.InputPerMTok
                      + u.OutputTokens * p.OutputPerMTok
                      + u.CacheReadTokens * p.CacheReadPerMTok
                      + u.CacheCreationTokens * p.CacheWritePerMTok) / 1_000_000m;
        }
        return matched ? total : null;
    }
}
