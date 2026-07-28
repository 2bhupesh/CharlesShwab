using System.Text.Json;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Governance;

/// <summary>
/// The real governance evaluator (spec §5). Policy gates auto-resolve to pass/fail with recorded
/// evidence; human gates create a pending approval and suspend the node; the ambiguity policy raises
/// an interactive clarification gate. Existing resolved approvals are honoured so an approved node is
/// not asked again, and clarification rounds are bounded — after the cap the workflow proceeds on
/// documented assumptions (FR-34).
/// </summary>
public sealed class GateEvaluator : IGateEvaluator
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly IReadOnlyDictionary<string, IGatePolicy> _policies;
    private readonly AuditLogger _audit;
    private readonly CoreOptions _options;

    public GateEvaluator(
        IDbContextFactory<AgenticDbContext> dbFactory,
        IEnumerable<IGatePolicy> policies,
        AuditLogger audit,
        CoreOptions options)
    {
        _dbFactory = dbFactory;
        _policies = policies.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _audit = audit;
        _options = options;
    }

    public async Task<GateOutcome> EvaluateAsync(WorkflowNode node, GateStage stage, CancellationToken ct)
    {
        var gates = GateDefinition.Deserialize(node.GatesJson).Where(g => g.Stage == stage).ToList();
        if (gates.Count == 0) return GateOutcome.Pass;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var gate in gates)
        {
            var outcome = gate.Type == GateType.HumanApproval
                ? await EvaluateHumanGateAsync(db, node, stage, gate, ct)
                : await EvaluatePolicyGateAsync(db, node, stage, gate, ct);

            if (outcome.Decision != GateDecision.Passed)
                return outcome; // first non-pass short-circuits
        }
        return GateOutcome.Pass;
    }

    private async Task<GateOutcome> EvaluateHumanGateAsync(AgenticDbContext db, WorkflowNode node, GateStage stage, GateDefinition gate, CancellationToken ct)
    {
        var existing = await db.Approvals
            .Where(a => a.NodeId == node.Id && a.Stage == stage && a.Description == gate.Description
                        && a.Kind == ApprovalKind.Approval && a.Status != ApprovalStatus.Voided)
            .OrderByDescending(a => a.RequestedAt)
            .FirstOrDefaultAsync(ct);

        switch (existing?.Status)
        {
            case ApprovalStatus.Approved:
                return GateOutcome.Pass;
            case ApprovalStatus.Rejected:
                return GateOutcome.Fail(existing.Comment ?? "rejected by reviewer");
            case ApprovalStatus.Pending:
                return GateOutcome.Await("awaiting human approval");
        }

        db.Approvals.Add(new Approval
        {
            WorkflowId = node.WorkflowId,
            NodeId = node.Id,
            Stage = stage,
            Kind = ApprovalKind.Approval,
            GateType = GateType.HumanApproval,
            Title = gate.Description,
            Description = gate.Description,
            Status = ApprovalStatus.Pending
        });
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync(node.WorkflowId, node.Id, AuditEventType.ApprovalRequested, "system",
            $"Human approval requested at {stage.ToString().ToLowerInvariant()} of '{node.Key}': {gate.Description}", ct: ct);
        return GateOutcome.Await(gate.Description);
    }

    private async Task<GateOutcome> EvaluatePolicyGateAsync(AgenticDbContext db, WorkflowNode node, GateStage stage, GateDefinition gate, CancellationToken ct)
    {
        if (gate.PolicyName is null || !_policies.TryGetValue(gate.PolicyName, out var policy))
            return GateOutcome.Fail($"unknown policy '{gate.PolicyName}'");

        var result = await policy.EvaluateAsync(node, gate.ParametersJson, ct);

        // Clarification path (only the ambiguity policy uses this).
        if (result.Clarifications is { Count: > 0 })
            return await HandleClarificationAsync(db, node, stage, result, ct);

        db.Approvals.Add(new Approval
        {
            WorkflowId = node.WorkflowId,
            NodeId = node.Id,
            Stage = stage,
            Kind = ApprovalKind.Approval,
            GateType = GateType.Policy,
            PolicyName = policy.Name,
            Title = gate.Description,
            Description = gate.Description,
            Status = result.Passed ? ApprovalStatus.AutoPassed : ApprovalStatus.AutoFailed,
            EvaluationJson = result.Evidence,
            ResolvedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync(node.WorkflowId, node.Id, AuditEventType.GateEvaluated, "system",
            $"Policy '{policy.Name}' {(result.Passed ? "passed" : "failed")} on '{node.Key}': {result.Evidence}", ct: ct);

        return result.Passed ? GateOutcome.Pass : GateOutcome.Fail(result.Evidence);
    }

    private async Task<GateOutcome> HandleClarificationAsync(AgenticDbContext db, WorkflowNode node, GateStage stage, PolicyResult result, CancellationToken ct)
    {
        // Bound the clarification loop; after the cap, proceed on documented assumptions.
        var priorRounds = await db.Approvals.CountAsync(a =>
            a.NodeId == node.Id && a.Kind == ApprovalKind.Clarification && a.Status == ApprovalStatus.Answered, ct);
        if (priorRounds >= _options.Orchestration.ClarificationMaxRounds)
        {
            db.Decisions.Add(new Decision
            {
                WorkflowId = node.WorkflowId,
                NodeId = node.Id,
                AgentType = node.AgentType,
                Title = "Proceeded on documented assumptions",
                Rationale = $"Ambiguity persisted after {priorRounds} clarification round(s); proceeding on stated assumptions.",
            });
            await db.SaveChangesAsync(ct);
            await _audit.LogAsync(node.WorkflowId, node.Id, AuditEventType.GateEvaluated, "system",
                $"Clarification cap reached on '{node.Key}'; proceeding on assumptions.", ct: ct);
            return GateOutcome.Pass;
        }

        var alreadyPending = await db.Approvals.AnyAsync(a =>
            a.NodeId == node.Id && a.Kind == ApprovalKind.Clarification && a.Status == ApprovalStatus.Pending, ct);
        if (alreadyPending)
            return GateOutcome.Await("awaiting clarification answers");

        db.Approvals.Add(new Approval
        {
            WorkflowId = node.WorkflowId,
            NodeId = node.Id,
            Stage = stage,
            Kind = ApprovalKind.Clarification,
            GateType = GateType.HumanApproval,
            Title = "Clarification needed",
            Description = result.Evidence,
            QuestionsJson = JsonSerializer.Serialize(result.Clarifications, JsonExtractor.SerializerOptions),
            Status = ApprovalStatus.Pending
        });
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync(node.WorkflowId, node.Id, AuditEventType.ClarificationRequested, "system",
            $"Clarification requested on '{node.Key}': {result.Evidence}", ct: ct);
        return GateOutcome.Await("clarification requested");
    }
}
