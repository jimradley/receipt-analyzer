using System.Threading.Channels;

namespace ReceiptAnalyzer.Jobs;

public interface IJobQueue
{
    ValueTask EnqueueAsync(string jobId, CancellationToken ct = default);
    ValueTask<string> DequeueAsync(CancellationToken ct);
}

/// <summary>In-process unbounded FIFO queue of job ids awaiting processing.</summary>
public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(string jobId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(jobId, ct);

    public ValueTask<string> DequeueAsync(CancellationToken ct) =>
        _channel.Reader.ReadAsync(ct);
}
