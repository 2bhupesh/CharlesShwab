using System.Collections.Concurrent;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// Holds one cancellation source per running workflow. Cancelling it stops in-flight node executions
/// (used by safe stop and cancel, spec §6). After cancellation the source is removed, so the next
/// resume transparently gets a fresh token.
/// </summary>
public sealed class WorkflowCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    /// <summary>Returns the current token for a workflow, creating a fresh source if none exists.</summary>
    public CancellationToken GetToken(Guid workflowId) =>
        _sources.GetOrAdd(workflowId, _ => new CancellationTokenSource()).Token;

    /// <summary>Cancels and discards the workflow's source; a later <see cref="GetToken"/> starts fresh.</summary>
    public void Cancel(Guid workflowId)
    {
        if (_sources.TryRemove(workflowId, out var cts))
        {
            try { cts.Cancel(); } finally { cts.Dispose(); }
        }
    }
}
