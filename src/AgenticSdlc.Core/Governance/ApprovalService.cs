using System.Text;
using System.Text.Json;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Governance;

/// <summary>
/// Resolves human approvals and clarifications (spec §5.3). Each resolution mutates persisted state
/// and signals the runner, so approving from the API immediately unblocks the background engine.
/// Rejection is actionable: reject-with-changes re-runs the node with the reviewer's feedback and
/// invalidates downstream work (FR-19); clarification answers feed the agent's next run (FR-34).
/// </summary>
public sealed class ApprovalService
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly AuditLogger _audit;
    private readonly WorkflowSignaler _signaler;
    private readonly ReplanService _replan;

    public ApprovalService(
        IDbContextFactory<AgenticDbContext> dbFactory,
        AuditLogger audit,
        WorkflowSignaler signaler,
        ReplanService replan)
    {
        _dbFactory = dbFactory;
        _audit = audit;
        _signaler = signaler;
        _replan = replan;
    }

    public async Task<IReadOnlyList<Approval>> GetPendingAsync(Guid? workflowId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Pending);
        if (workflowId is { } id) q = q.Where(a => a.WorkflowId == id);
        return await q.ToListAsync(ct);
    }

    /// <summary>Resolves a plain approval gate (approve, hard-reject, or reject-with-changes).</summary>
    public Task ApproveAsync(Guid approvalId, bool approved, string approver, string? comment, bool requestChanges, CancellationToken ct = default) =>
        ResolveAsync(approvalId, approved, approver, comment, requestChanges, answers: null, ct);

    /// <summary>Submits answers to a clarification gate.</summary>
    public Task AnswerClarificationAsync(Guid approvalId, string respondent, IReadOnlyList<ClarificationAnswer> answers, CancellationToken ct = default) =>
        ResolveAsync(approvalId, approved: true, respondent, comment: null, requestChanges: false, answers, ct);

    private async Task ResolveAsync(Guid approvalId, bool approved, string approver, string? comment, bool requestChanges,
        IReadOnlyList<ClarificationAnswer>? answers, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == approvalId, ct)
                       ?? throw new InvalidOperationException($"Approval {approvalId} not found.");
        if (approval.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException($"Approval {approvalId} is already {approval.Status}.");

        var node = await db.Nodes.FirstAsync(n => n.Id == approval.NodeId, ct);
        approval.Approver = approver;
        approval.Comment = comment;
        approval.ResolvedAt = DateTimeOffset.UtcNow;

        if (approval.Kind == ApprovalKind.Clarification)
            await ResolveClarificationAsync(db, approval, node, answers ?? Array.Empty<ClarificationAnswer>(), ct);
        else if (approved)
            await GrantAsync(db, approval, node, ct);
        else if (requestChanges)
            await RejectWithChangesAsync(db, approval, node, comment, ct);
        else
            await RejectHardAsync(db, approval, node, comment, ct);

        await db.SaveChangesAsync(ct);
        _signaler.Signal(approval.WorkflowId);
    }

    private async Task GrantAsync(AgenticDbContext db, Approval approval, WorkflowNode node, CancellationToken ct)
    {
        approval.Status = ApprovalStatus.Approved;
        if (approval.Stage == GateStage.Exit)
        {
            node.Status = NodeStatus.Succeeded;
            node.CompletedAt = DateTimeOffset.UtcNow;
            var drafts = await db.Artifacts
                .Where(a => a.ProducedByNodeId == node.Id && a.Status == ArtifactStatus.Draft).ToListAsync(ct);
            foreach (var a in drafts) a.Status = ArtifactStatus.Approved;
        }
        else
        {
            node.Status = NodeStatus.Pending; // re-dispatch; the entry gate now passes
            node.StartedAt = null;
        }
        await _audit.LogAsync(approval.WorkflowId, node.Id, AuditEventType.ApprovalGranted, $"human:{approval.Approver}",
            $"Approved '{node.Key}' at {approval.Stage.ToString().ToLowerInvariant()}.", ct: ct);
    }

    private async Task RejectWithChangesAsync(AgenticDbContext db, Approval approval, WorkflowNode node, string? comment, CancellationToken ct)
    {
        // The rejection is preserved in the audit log; the row is voided so the re-run re-requests.
        approval.Status = ApprovalStatus.Voided;
        node.TaskInstructionsJson = AppendFeedback(node.TaskInstructionsJson, comment);
        node.Status = NodeStatus.Pending;
        node.Attempt = 0;
        node.StartedAt = null;
        node.CompletedAt = null;
        await _replan.SupersedeNodeArtifactsAsync(db, node.Id, ct);
        await _replan.MarkDownstreamStaleAsync(db, approval.WorkflowId, node.Id, "changes requested by reviewer", ct);
        await _audit.LogAsync(approval.WorkflowId, node.Id, AuditEventType.ApprovalRejected, $"human:{approval.Approver}",
            $"Requested changes on '{node.Key}': {comment}", ct: ct);
    }

    private async Task RejectHardAsync(AgenticDbContext db, Approval approval, WorkflowNode node, string? comment, CancellationToken ct)
    {
        approval.Status = ApprovalStatus.Rejected;
        node.Status = NodeStatus.Failed;
        node.CompletedAt = DateTimeOffset.UtcNow;
        node.ErrorMessage = $"Rejected: {comment}";

        var wf = await db.Workflows.FirstAsync(w => w.Id == approval.WorkflowId, ct);
        if (wf.Status is WorkflowStatus.Running or WorkflowStatus.AwaitingApproval)
        {
            wf.Status = WorkflowStatus.Failed;
            wf.FailureReason = $"'{node.Key}' rejected: {comment}";
            wf.CompletedAt = DateTimeOffset.UtcNow;
        }
        await _audit.LogAsync(approval.WorkflowId, node.Id, AuditEventType.ApprovalRejected, $"human:{approval.Approver}",
            $"Rejected '{node.Key}': {comment}", ct: ct);
    }

    private async Task ResolveClarificationAsync(AgenticDbContext db, Approval approval, WorkflowNode node, IReadOnlyList<ClarificationAnswer> answers, CancellationToken ct)
    {
        approval.Status = ApprovalStatus.Answered;
        approval.AnswersJson = JsonSerializer.Serialize(answers, JsonExtractor.SerializerOptions);

        // Store an answers artifact for the review-package trail.
        db.Artifacts.Add(new Artifact
        {
            WorkflowId = approval.WorkflowId,
            ProducedByNodeId = node.Id,
            Type = ArtifactType.ClarificationAnswers,
            Name = "Clarification Answers",
            Status = ArtifactStatus.Approved,
            ContentJson = approval.AnswersJson
        });

        // Feed the answers into the node's next run and reset it to re-run.
        node.TaskInstructionsJson = BuildClarificationInstructions(approval.QuestionsJson, answers);
        node.Status = NodeStatus.Pending;
        node.Attempt = 0;
        node.NextRetryAt = null;
        node.StartedAt = null;
        node.CompletedAt = null;
        await _replan.SupersedeNodeArtifactsAsync(db, node.Id, ct);
        await _audit.LogAsync(approval.WorkflowId, node.Id, AuditEventType.ClarificationAnswered, $"human:{approval.Approver}",
            $"Clarification answered for '{node.Key}' ({answers.Count} answer(s)).", ct: ct);
    }

    private static string AppendFeedback(string? existing, string? comment)
    {
        var feedback = $"Reviewer requested changes: {comment}";
        return string.IsNullOrWhiteSpace(existing) ? feedback : existing + "\n" + feedback;
    }

    private static string BuildClarificationInstructions(string? questionsJson, IReadOnlyList<ClarificationAnswer> answers)
    {
        var questions = string.IsNullOrWhiteSpace(questionsJson)
            ? new List<ClarificationQuestion>()
            : JsonSerializer.Deserialize<List<ClarificationQuestion>>(questionsJson, JsonExtractor.SerializerOptions) ?? new();
        var byId = questions.ToDictionary(q => q.QuestionId, q => q.Question);

        var sb = new StringBuilder("Clarifications provided by a human reviewer:\n");
        foreach (var a in answers)
            sb.AppendLine($"- Q: {(byId.TryGetValue(a.QuestionId, out var q) ? q : a.QuestionId)}\n  A: {a.Answer}");
        return sb.ToString();
    }
}
