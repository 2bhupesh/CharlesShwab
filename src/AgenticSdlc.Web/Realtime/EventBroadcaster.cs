using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgenticSdlc.Core.Abstractions;

namespace AgenticSdlc.Web.Realtime;

/// <summary>The envelope streamed to browsers over SSE: a sequenced, JSON-serializable event.</summary>
public sealed record SseEnvelope(long Seq, string Type, string WorkflowId, string? NodeId, string Summary, DateTimeOffset At);

/// <summary>
/// Bridges the core <see cref="IWorkflowEventBus"/> to SSE clients (spec §8.2). It sequences events,
/// keeps a bounded replay buffer for <c>Last-Event-ID</c> reconnection, and fans out to per-client
/// bounded (drop-oldest) channels so a stalled browser can never backpressure the engine. A periodic
/// heartbeat keeps idle connections alive.
/// </summary>
public sealed class EventBroadcaster : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly IWorkflowEventBus _bus;
    private readonly int _ringSize;
    private readonly Queue<SseEnvelope> _ring = new();
    private readonly object _ringLock = new();
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private long _seq;

    private sealed record Subscriber(Channel<SseEnvelope> Channel, string? WorkflowFilter);

    public EventBroadcaster(IWorkflowEventBus bus, IConfiguration config)
    {
        _bus = bus;
        _ringSize = config.GetValue("AgenticSdlc:Events:RingBufferSize", 1000);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var heartbeat = HeartbeatLoopAsync(stoppingToken);
        try
        {
            await foreach (var ev in _bus.Reader.ReadAllAsync(stoppingToken))
            {
                var env = new SseEnvelope(
                    Interlocked.Increment(ref _seq), ev.Type, ev.WorkflowId.ToString(),
                    ev.NodeId?.ToString(), ev.Summary, ev.At);
                AppendToRing(env);
                Fanout(env);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        await heartbeat;
    }

    /// <summary>
    /// Subscribes a client: replays buffered events newer than <paramref name="lastSeenSeq"/> (filtered),
    /// then streams live events until the caller disconnects.
    /// </summary>
    public async IAsyncEnumerable<SseEnvelope> Subscribe(
        string? workflowId, long? lastSeenSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        var replay = SnapshotRing(workflowId, lastSeenSeq);
        var channel = Channel.CreateBounded<SseEnvelope>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(channel, workflowId);
        try
        {
            foreach (var e in replay)
                yield return e;
            await foreach (var e in channel.Reader.ReadAllAsync(ct))
                yield return e;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private void Fanout(SseEnvelope env)
    {
        foreach (var sub in _subscribers.Values)
            if (sub.WorkflowFilter is null || sub.WorkflowFilter == env.WorkflowId)
                sub.Channel.Writer.TryWrite(env);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var hb = new SseEnvelope(0, "heartbeat", "", null, "", DateTimeOffset.UtcNow);
                foreach (var sub in _subscribers.Values)
                    sub.Channel.Writer.TryWrite(hb);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private void AppendToRing(SseEnvelope env)
    {
        lock (_ringLock)
        {
            _ring.Enqueue(env);
            while (_ring.Count > _ringSize)
                _ring.Dequeue();
        }
    }

    private List<SseEnvelope> SnapshotRing(string? workflowId, long? lastSeenSeq)
    {
        lock (_ringLock)
        {
            return _ring
                .Where(e => (lastSeenSeq is null || e.Seq > lastSeenSeq) &&
                            (workflowId is null || e.WorkflowId == workflowId))
                .ToList();
        }
    }
}
