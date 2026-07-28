namespace AgenticSdlc.Core.Domain;

/// <summary>
/// A governance checkpoint instance. Covers human approval gates, automated policy evaluations, and
/// interactive clarification gates (the clarification loop rides the same mechanism). Rows are
/// <see cref="ApprovalStatus.Voided"/> on re-plan, never deleted — approval history survives (FR-28).
/// </summary>
public class Approval
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    public Guid NodeId { get; set; }

    public GateStage Stage { get; set; }

    public ApprovalKind Kind { get; set; } = ApprovalKind.Approval;

    public GateType GateType { get; set; }

    /// <summary>Set for policy gates — the policy that produced this row.</summary>
    public string? PolicyName { get; set; }

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Clarification questions (JSON array) rendered to the reviewer; null for plain approvals.</summary>
    public string? QuestionsJson { get; set; }

    /// <summary>Submitted clarification answers (JSON array); null until answered.</summary>
    public string? AnswersJson { get; set; }

    /// <summary>Context artifact ids the reviewer should inspect (JSON array).</summary>
    public string ContextArtifactIdsJson { get; set; } = "[]";

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }

    public string? Approver { get; set; }
    public string? Comment { get; set; }

    /// <summary>Policy evidence (JSON) for auto-resolved gates (FR-18).</summary>
    public string? EvaluationJson { get; set; }
}
