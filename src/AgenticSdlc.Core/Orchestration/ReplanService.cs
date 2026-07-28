using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// Invalidates work when an upstream artifact changes (spec §4.6, FR-12). Operates on the caller's
/// db context so the change is part of one transaction. Downstream succeeded/awaiting/failed nodes
/// are reset to Pending, their artifacts superseded (lineage retained), and their pending approvals
/// voided — so re-run nodes request approval afresh and governance is preserved under re-planning.
/// WP-7 layers rollback and broader triggers on top.
/// </summary>
public sealed class ReplanService
{
    private readonly AuditLogger _audit;
    public ReplanService(AuditLogger audit) => _audit = audit;

    /// <summary>Supersedes a single node's current artifacts (used when the node itself will re-run).</summary>
    public async Task SupersedeNodeArtifactsAsync(AgenticDbContext db, Guid nodeId, CancellationToken ct)
    {
        var artifacts = await db.Artifacts
            .Where(a => a.ProducedByNodeId == nodeId && a.Status != ArtifactStatus.Superseded)
            .ToListAsync(ct);
        foreach (var a in artifacts)
            a.Status = ArtifactStatus.Superseded;
    }

    /// <summary>
    /// Breadth-first invalidation of everything downstream of <paramref name="fromNodeId"/>. The
    /// origin node itself is not touched (the caller decides its fate); only its dependents are staled.
    /// </summary>
    public async Task MarkDownstreamStaleAsync(AgenticDbContext db, Guid workflowId, Guid fromNodeId, string reason, CancellationToken ct)
    {
        var edges = await db.Edges.Where(e => e.WorkflowId == workflowId).ToListAsync(ct);
        var nodes = await db.Nodes.Where(n => n.WorkflowId == workflowId).ToListAsync(ct);
        var byId = nodes.ToDictionary(n => n.Id);

        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        foreach (var e in edges.Where(e => e.FromNodeId == fromNodeId))
            queue.Enqueue(e.ToNodeId);

        var staled = new List<WorkflowNode>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            if (byId.TryGetValue(id, out var node) &&
                node.Status is NodeStatus.Succeeded or NodeStatus.AwaitingApproval or NodeStatus.Failed)
            {
                // Transient Stale is audit-visible, then immediately reset to Pending for re-run.
                node.Status = NodeStatus.Pending;
                node.Attempt = 0;
                node.NextRetryAt = null;
                node.CompletedAt = null;
                node.ErrorMessage = null;
                await SupersedeNodeArtifactsAsync(db, id, ct);
                await VoidNodeApprovalsAsync(db, id, ct);
                staled.Add(node);
            }
            foreach (var e in edges.Where(e => e.FromNodeId == id))
                queue.Enqueue(e.ToNodeId);
        }

        foreach (var n in staled)
            await _audit.LogAsync(workflowId, n.Id, AuditEventType.NodeStale, "system",
                $"Node '{n.Key}' invalidated ({reason}); will re-run.", ct: ct);

        if (staled.Count > 0)
            await _audit.LogAsync(workflowId, fromNodeId, AuditEventType.ReplanTriggered, "system",
                $"Re-plan invalidated {staled.Count} downstream node(s): {reason}.", ct: ct);
    }

    private static readonly ApprovalStatus[] Voidable =
    {
        ApprovalStatus.Pending, ApprovalStatus.Approved, ApprovalStatus.Rejected,
        ApprovalStatus.AutoPassed, ApprovalStatus.AutoFailed
    };

    /// <summary>
    /// Voids (does not delete) a re-run node's approvals so it re-requests approval afresh — governance
    /// is preserved under re-planning. Answered clarifications are retained (their count bounds the
    /// clarification loop); the durable record of what was granted/rejected lives in the audit log.
    /// </summary>
    public async Task VoidNodeApprovalsAsync(AgenticDbContext db, Guid nodeId, CancellationToken ct)
    {
        var approvals = await db.Approvals
            .Where(a => a.NodeId == nodeId && Voidable.Contains(a.Status))
            .ToListAsync(ct);
        foreach (var a in approvals)
        {
            a.Status = ApprovalStatus.Voided;
            a.ResolvedAt ??= DateTimeOffset.UtcNow;
        }
    }
}
