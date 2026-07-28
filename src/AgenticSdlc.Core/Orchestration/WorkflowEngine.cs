using System.Collections.Concurrent;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Governance;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// The scheduler. One tick per workflow (serialized by a per-workflow lock so signals never race):
/// scan for ready nodes, evaluate entry gates, dispatch executors in parallel under a global
/// concurrency limit, and recompute the workflow's terminal state. Join nodes succeed inline — a
/// join becoming ready only when all inbound branches complete IS the synchronization mechanism
/// (spec §4.3, FR-8/10/11).
/// </summary>
public sealed class WorkflowEngine
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly IGateEvaluator _gates;
    private readonly NodeExecutor _executor;
    private readonly AuditLogger _audit;
    private readonly WorkflowSignaler _signaler;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _tickLocks = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly SemaphoreSlim _slots;

    public WorkflowEngine(
        IDbContextFactory<AgenticDbContext> dbFactory,
        IGateEvaluator gates,
        NodeExecutor executor,
        AuditLogger audit,
        WorkflowSignaler signaler,
        CoreOptions options)
    {
        _dbFactory = dbFactory;
        _gates = gates;
        _executor = executor;
        _audit = audit;
        _signaler = signaler;
        _slots = new SemaphoreSlim(Math.Max(1, options.Orchestration.MaxParallelNodes));
    }

    public async Task TickAsync(Guid workflowId, CancellationToken ct = default)
    {
        var gate = _tickLocks.GetOrAdd(workflowId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await TickCoreAsync(workflowId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Restart recovery (FR-9): resets nodes left <see cref="NodeStatus.Running"/> by a crashed process
    /// — their in-flight tasks died — back to Pending, then re-signals active workflows so execution
    /// resumes. Runs once at startup and is safe to call at any time.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stranded = await db.Nodes.Where(n => n.Status == NodeStatus.Running).ToListAsync(ct);
        foreach (var n in stranded)
        {
            n.Status = NodeStatus.Pending;
            n.StartedAt = null;
        }
        if (stranded.Count > 0)
            await db.SaveChangesAsync(ct);

        foreach (var n in stranded)
            await _audit.LogAsync(n.WorkflowId, n.Id, AuditEventType.NodeRecoveredAfterRestart, "system",
                $"Node '{n.Key}' reset to Pending after restart.", ct: ct);

        var active = await db.Workflows
            .Where(w => w.Status == WorkflowStatus.Running || w.Status == WorkflowStatus.AwaitingApproval)
            .Select(w => w.Id)
            .ToListAsync(ct);
        foreach (var id in active)
            _signaler.Signal(id);
    }

    /// <summary>Ticks every workflow currently Running or AwaitingApproval (periodic sweep + recovery).</summary>
    public async Task TickAllActiveAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = await db.Workflows
            .Where(w => w.Status == WorkflowStatus.Running || w.Status == WorkflowStatus.AwaitingApproval)
            .Select(w => w.Id)
            .ToListAsync(ct);
        foreach (var id in ids)
            await TickAsync(id, ct);
    }

    private async Task TickCoreAsync(Guid workflowId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (wf is null || wf.Status is not (WorkflowStatus.Running or WorkflowStatus.AwaitingApproval))
            return;

        var nodes = await db.Nodes.Where(n => n.WorkflowId == workflowId).ToListAsync(ct);
        var edges = await db.Edges.Where(e => e.WorkflowId == workflowId).ToListAsync(ct);
        var byId = nodes.ToDictionary(n => n.Id);
        var now = DateTimeOffset.UtcNow;
        bool inlineProgress = false;

        // Fixpoint scan so inline joins cascade to their dependents within one tick.
        bool progressed;
        var guardIterations = nodes.Count + 2;
        do
        {
            progressed = false;
            var ready = nodes.Where(n => IsReady(n, edges, byId, now)).ToList();

            foreach (var node in ready)
            {
                var entry = await _gates.EvaluateAsync(node, GateStage.Entry, ct);
                if (entry.Decision == GateDecision.Failed)
                {
                    node.Status = NodeStatus.Failed;
                    node.ErrorMessage = entry.Reason;
                    node.CompletedAt = now;
                    await db.SaveChangesAsync(ct);
                    await _audit.LogAsync(workflowId, node.Id, AuditEventType.NodeFailed, "system",
                        $"Node '{node.Key}' blocked by entry gate: {entry.Reason}");
                    if (!node.ContinueOnFailure)
                        await FailWorkflowAsync(db, wf, $"Entry gate failed on '{node.Key}': {entry.Reason}", ct);
                    progressed = true;
                    continue;
                }
                if (entry.Decision == GateDecision.AwaitingHuman)
                {
                    node.Status = NodeStatus.AwaitingApproval;
                    await db.SaveChangesAsync(ct);
                    await _audit.LogAsync(workflowId, node.Id, AuditEventType.ApprovalRequested, "system",
                        $"Node '{node.Key}' awaiting approval at entry.");
                    progressed = true;
                    continue;
                }

                if (node.AgentType is AgentType.Join or AgentType.Packaging)
                {
                    // System node: succeeds inline. Join = synchronization point; Packaging is hooked
                    // to the review-package builder in WP-8.
                    node.Status = NodeStatus.Succeeded;
                    node.StartedAt = now;
                    node.CompletedAt = now;
                    await db.SaveChangesAsync(ct);
                    await _audit.LogAsync(workflowId, node.Id, AuditEventType.NodeSucceeded, "system",
                        node.AgentType == AgentType.Join
                            ? $"Synchronization point '{node.Key}' reached."
                            : $"System node '{node.Key}' completed.");
                    inlineProgress = true;
                    progressed = true;
                }
                else
                {
                    node.Status = NodeStatus.Running;
                    node.Attempt += 1;
                    node.StartedAt ??= now;
                    await db.SaveChangesAsync(ct);
                    await _audit.LogAsync(workflowId, node.Id, AuditEventType.NodeStarted, $"agent:{node.AgentType}",
                        $"Node '{node.Key}' started (attempt {node.Attempt}).");
                    Dispatch(workflowId, node.Id);
                    progressed = true;
                }
            }
        }
        while (progressed && --guardIterations > 0);

        await UpdateWorkflowStatusAsync(db, workflowId, now, ct);

        if (inlineProgress)
            _signaler.Signal(workflowId); // re-tick so join dependents are picked up promptly
    }

    private static bool IsReady(WorkflowNode node, List<DependencyEdge> edges, Dictionary<Guid, WorkflowNode> byId, DateTimeOffset now)
    {
        if (node.Status != NodeStatus.Pending) return false;
        if (node.NextRetryAt is { } retry && retry > now) return false;

        var hardDeps = edges.Where(e => e.ToNodeId == node.Id && e.Kind == EdgeKind.Hard);
        return hardDeps.All(e =>
            byId.TryGetValue(e.FromNodeId, out var src) &&
            src.Status is NodeStatus.Succeeded or NodeStatus.Skipped);
    }

    private async Task UpdateWorkflowStatusAsync(AgenticDbContext db, Guid workflowId, DateTimeOffset now, CancellationToken ct)
    {
        var statuses = await db.Nodes.Where(n => n.WorkflowId == workflowId).Select(n => n.Status).ToListAsync(ct);
        bool anyRunning = statuses.Any(s => s == NodeStatus.Running);
        bool anyAwaiting = statuses.Any(s => s == NodeStatus.AwaitingApproval);
        bool anyPending = statuses.Any(s => s is NodeStatus.Pending or NodeStatus.Ready);

        var next =
            anyRunning ? WorkflowStatus.Running :
            anyAwaiting ? WorkflowStatus.AwaitingApproval :
            anyPending ? WorkflowStatus.Running :
            WorkflowStatus.Completed;

        var wf = await db.Workflows.FirstAsync(w => w.Id == workflowId, ct);
        if (wf.Status is not (WorkflowStatus.Running or WorkflowStatus.AwaitingApproval))
            return; // an executor may have set Failed/Cancelled concurrently — respect it

        if (wf.Status == next) return;

        wf.Status = next;
        if (next == WorkflowStatus.Completed)
            wf.CompletedAt = now;
        await db.SaveChangesAsync(ct);

        if (next == WorkflowStatus.Completed)
            await _audit.LogAsync(workflowId, null, AuditEventType.WorkflowCompleted, "system", "Workflow completed.");
    }

    private async Task FailWorkflowAsync(AgenticDbContext db, Workflow wf, string reason, CancellationToken ct)
    {
        wf.Status = WorkflowStatus.Failed;
        wf.FailureReason = reason;
        wf.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync(wf.Id, null, AuditEventType.WorkflowFailed, "system", reason);
    }

    private void Dispatch(Guid workflowId, Guid nodeId)
    {
        if (!_inFlight.TryAdd(nodeId, 0)) return; // already running
        _ = Task.Run(async () =>
        {
            await _slots.WaitAsync();
            try
            {
                await _executor.ExecuteAsync(workflowId, nodeId);
            }
            finally
            {
                _slots.Release();
                _inFlight.TryRemove(nodeId, out _);
            }
        });
    }
}
