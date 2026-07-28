using AgenticSdlc.Core.Domain;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// Compensating rollback (spec §6, FR-24): rather than destructively deleting anything, it supersedes
/// a node's artifacts, resets the node, and invalidates downstream work so everything re-runs — full
/// lineage retained. Implemented on top of <see cref="ReplanService"/>; the audit distinguishes a
/// rollback from an ordinary re-plan.
/// </summary>
public sealed class RollbackService
{
    private readonly ReplanService _replan;
    public RollbackService(ReplanService replan) => _replan = replan;

    public Task RollbackNodeAsync(Guid workflowId, Guid nodeId, string reason, CancellationToken ct = default) =>
        _replan.ReplanFromNodeAsync(workflowId, nodeId, reason, AuditEventType.RollbackTriggered, ct);
}
