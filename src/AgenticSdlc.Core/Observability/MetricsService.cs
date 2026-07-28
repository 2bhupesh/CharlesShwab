using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Observability;

/// <summary>Per-workflow engineering-intelligence metrics (FR-30).</summary>
public sealed record WorkflowMetrics(
    Guid WorkflowId,
    string Status,
    int NodesTotal,
    int NodesSucceeded,
    int NodesFailed,
    double AgentSuccessRate,
    int Retries,
    int Rollbacks,
    double? WorkflowLatencySeconds,
    double? MttrSeconds,
    double? MeanApprovalSeconds,
    double ValidationPassRate,
    double RequirementCoverage,
    double TestCoverage,
    long InputTokens,
    long OutputTokens,
    int AgentInvocations);

/// <summary>Platform-wide metrics aggregated across all workflows (FR-30).</summary>
public sealed record GlobalMetrics(
    int WorkflowsTotal,
    IReadOnlyDictionary<string, int> ByStatus,
    double WorkflowSuccessRate,
    double AgentSuccessRate,
    double RetryFrequency,
    double RollbackFrequency,
    double? MttrSeconds,
    double? MeanWorkflowLatencySeconds,
    double? MeanApprovalSeconds,
    double ValidationPassRate,
    double RequirementCoverage,
    double TestCoverage,
    long TotalInputTokens,
    long TotalOutputTokens,
    int PendingApprovals,
    int ActiveWorkflows);

/// <summary>
/// Computes metrics on demand by querying the tables — there is no accumulated counter to drift from
/// the source of truth (spec §9.2). All ten required metrics are covered.
/// </summary>
public sealed class MetricsService
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    public MetricsService(IDbContextFactory<AgenticDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<WorkflowMetrics> GetForWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wf = await db.Workflows.FirstAsync(w => w.Id == workflowId, ct);
        var nodes = await db.Nodes.Where(n => n.WorkflowId == workflowId).ToListAsync(ct);
        var approvals = await db.Approvals.Where(a => a.WorkflowId == workflowId).ToListAsync(ct);
        var executions = await db.AgentExecutions.Where(e => e.WorkflowId == workflowId).ToListAsync(ct);
        var audit = await db.AuditEvents.Where(e => e.WorkflowId == workflowId).ToListAsync(ct);
        var requirements = await db.Requirements.Where(r => r.WorkflowId == workflowId).ToListAsync(ct);
        var artifacts = await db.Artifacts.Where(a => a.WorkflowId == workflowId).ToListAsync(ct);

        var agentNodes = nodes.Where(n => n.AgentType is not (AgentType.Join or AgentType.Packaging)).ToList();
        var succeeded = agentNodes.Count(n => n.Status == NodeStatus.Succeeded);
        var failed = agentNodes.Count(n => n.Status == NodeStatus.Failed);
        var attempted = agentNodes.Count(n => n.Attempt > 0);
        var retries = agentNodes.Sum(n => Math.Max(0, n.Attempt - 1));
        var rollbacks = audit.Count(e => e.EventType is AuditEventType.RollbackTriggered or AuditEventType.ReplanTriggered);

        double? latency = wf.StartedAt is { } s && wf.CompletedAt is { } c ? (c - s).TotalSeconds : null;

        return new WorkflowMetrics(
            workflowId,
            wf.Status.ToString(),
            nodes.Count,
            nodes.Count(n => n.Status == NodeStatus.Succeeded),
            failed,
            AgentSuccessRate(succeeded, failed),
            retries,
            rollbacks,
            latency,
            Mttr(audit),
            MeanApprovalSeconds(approvals),
            ValidationPassRate(artifacts),
            RequirementCoverage(requirements, artifacts),
            TestCoverage(artifacts),
            executions.Sum(e => (long)e.InputTokens),
            executions.Sum(e => (long)e.OutputTokens),
            executions.Count);
    }

    public async Task<GlobalMetrics> GetGlobalAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var workflows = await db.Workflows.ToListAsync(ct);
        var nodes = await db.Nodes.ToListAsync(ct);
        var approvals = await db.Approvals.ToListAsync(ct);
        var executions = await db.AgentExecutions.ToListAsync(ct);
        var audit = await db.AuditEvents.ToListAsync(ct);
        var requirements = await db.Requirements.ToListAsync(ct);
        var artifacts = await db.Artifacts.ToListAsync(ct);

        var byStatus = workflows.GroupBy(w => w.Status.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var terminal = workflows.Count(w => w.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled);
        var completed = workflows.Count(w => w.Status == WorkflowStatus.Completed);

        var agentNodes = nodes.Where(n => n.AgentType is not (AgentType.Join or AgentType.Packaging)).ToList();
        var succeeded = agentNodes.Count(n => n.Status == NodeStatus.Succeeded);
        var failed = agentNodes.Count(n => n.Status == NodeStatus.Failed);
        var latencies = workflows
            .Where(w => w.StartedAt is not null && w.CompletedAt is not null)
            .Select(w => (w.CompletedAt!.Value - w.StartedAt!.Value).TotalSeconds).ToList();

        return new GlobalMetrics(
            workflows.Count,
            byStatus,
            terminal == 0 ? 0 : (double)completed / terminal,
            AgentSuccessRate(succeeded, failed),
            agentNodes.Count == 0 ? 0 : (double)agentNodes.Sum(n => Math.Max(0, n.Attempt - 1)) / agentNodes.Count,
            workflows.Count == 0 ? 0 : (double)audit.Count(e => e.EventType is AuditEventType.RollbackTriggered or AuditEventType.ReplanTriggered) / workflows.Count,
            Mttr(audit),
            latencies.Count == 0 ? null : latencies.Average(),
            MeanApprovalSeconds(approvals),
            ValidationPassRate(artifacts),
            RequirementCoverage(requirements, artifacts),
            TestCoverage(artifacts),
            executions.Sum(e => (long)e.InputTokens),
            executions.Sum(e => (long)e.OutputTokens),
            approvals.Count(a => a.Status == ApprovalStatus.Pending),
            workflows.Count(w => w.Status is WorkflowStatus.Running or WorkflowStatus.AwaitingApproval));
    }

    private static double AgentSuccessRate(int succeeded, int failed) =>
        succeeded + failed == 0 ? 1.0 : (double)succeeded / (succeeded + failed);

    /// <summary>Mean time to recovery: failure/retry of a node to its subsequent success (A-4).</summary>
    private static double? Mttr(List<AuditEvent> audit)
    {
        var deltas = new List<double>();
        foreach (var byNode in audit.Where(e => e.NodeId is not null).GroupBy(e => e.NodeId!.Value))
        {
            var ordered = byNode.OrderBy(e => e.Seq).ToList();
            DateTimeOffset? firstFailure = null;
            foreach (var e in ordered)
            {
                if (e.EventType is AuditEventType.NodeFailed or AuditEventType.NodeRetryScheduled or AuditEventType.NodeTimedOut)
                    firstFailure ??= e.Timestamp;
                else if (e.EventType == AuditEventType.NodeSucceeded && firstFailure is { } f)
                {
                    deltas.Add((e.Timestamp - f).TotalSeconds);
                    firstFailure = null;
                }
            }
        }
        return deltas.Count == 0 ? null : deltas.Average();
    }

    private static double? MeanApprovalSeconds(List<Approval> approvals)
    {
        var durations = approvals
            .Where(a => a.GateType == GateType.HumanApproval && a.ResolvedAt is not null)
            .Select(a => (a.ResolvedAt!.Value - a.RequestedAt).TotalSeconds)
            .Where(d => d >= 0).ToList();
        return durations.Count == 0 ? null : durations.Average();
    }

    private static double ValidationPassRate(List<Artifact> artifacts)
    {
        var v = LatestValidation(artifacts);
        if (v is null || v.TestsTotal == 0) return v is null ? 0 : 1;
        return (double)v.TestsPassed / v.TestsTotal;
    }

    private static double TestCoverage(List<Artifact> artifacts)
    {
        var v = LatestValidation(artifacts);
        return v is null || v.TestsTotal == 0 ? 0 : (double)v.TestsPassed / v.TestsTotal;
    }

    private static ValidationOutput? LatestValidation(List<Artifact> artifacts)
    {
        var report = artifacts
            .Where(a => a.Type == ArtifactType.ValidationReport && a.Status != ArtifactStatus.Superseded)
            .OrderByDescending(a => a.Version)
            .FirstOrDefault();
        if (report?.ContentJson is null) return null;
        var (ok, v, _) = JsonExtractor.TryParse<ValidationOutput>(report.ContentJson);
        return ok ? v : null;
    }

    /// <summary>Fraction of functional/non-functional requirements referenced by a live artifact.</summary>
    private static double RequirementCoverage(List<RequirementItem> requirements, List<Artifact> artifacts)
    {
        var target = requirements.Where(r => r.Kind is RequirementKind.Functional or RequirementKind.NonFunctional).Select(r => r.Code).ToHashSet();
        if (target.Count == 0) return 0;

        var referenced = new HashSet<string>();
        foreach (var a in artifacts.Where(a => a.Status != ArtifactStatus.Superseded))
        {
            foreach (var code in ParseCodes(a.RequirementIdsJson))
                if (target.Contains(code)) referenced.Add(code);
        }
        return (double)referenced.Count / target.Count;
    }

    private static IEnumerable<string> ParseCodes(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return Array.Empty<string>(); }
    }
}
