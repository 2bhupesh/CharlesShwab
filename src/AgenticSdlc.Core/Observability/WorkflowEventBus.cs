using System.Threading.Channels;
using AgenticSdlc.Core.Abstractions;

namespace AgenticSdlc.Core.Observability;

/// <summary>
/// In-process event bus backed by a bounded, drop-oldest channel so the engine is never blocked by a
/// slow reader. The Web layer subscribes via <see cref="Reader"/>.
/// </summary>
public sealed class WorkflowEventBus : IWorkflowEventBus
{
    private readonly Channel<WorkflowEvent> _channel = Channel.CreateBounded<WorkflowEvent>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false });

    public ChannelReader<WorkflowEvent> Reader => _channel.Reader;

    public void Publish(WorkflowEvent evt) => _channel.Writer.TryWrite(evt);
}
