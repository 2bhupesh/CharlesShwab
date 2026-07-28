using System.Threading.Channels;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// Wakes the background runner when a workflow needs a scheduler tick. An unbounded channel makes the
/// engine event-driven (with a periodic sweep as a safety net). Signals are best-effort deduplicated —
/// a coalesced signal is harmless because a tick always recomputes full state.
/// </summary>
public sealed class WorkflowSignaler
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public void Signal(Guid workflowId) => _channel.Writer.TryWrite(workflowId);

    /// <summary>Waits for the next signal, or returns null if <paramref name="timeout"/> elapses first.</summary>
    public async Task<Guid?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timeout: caller should run a periodic sweep
        }
    }
}
