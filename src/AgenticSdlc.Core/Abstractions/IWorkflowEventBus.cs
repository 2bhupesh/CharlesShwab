using System.Threading.Channels;

namespace AgenticSdlc.Core.Abstractions;

/// <summary>
/// A lightweight event emitted alongside each audit record. It is a trigger, not a full state payload:
/// the dashboard refetches detail on any event, so a compact type/id/summary is enough (spec §8.2).
/// </summary>
public sealed record WorkflowEvent(
    string Type,
    Guid WorkflowId,
    Guid? NodeId,
    string Summary,
    DateTimeOffset At);

/// <summary>
/// The seam between Core and the delivery surface: Core publishes workflow events, the Web layer's
/// SSE broadcaster is the sole consumer (spec reconciliation §2). A bounded, drop-oldest channel means
/// a slow or absent consumer never applies backpressure to the engine.
/// </summary>
public interface IWorkflowEventBus
{
    ChannelReader<WorkflowEvent> Reader { get; }
    void Publish(WorkflowEvent evt);
}
