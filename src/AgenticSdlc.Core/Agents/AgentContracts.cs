using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Orchestration;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// A specialized agent. Adding one requires implementing this interface and registering it — the
/// engine resolves agents by <see cref="Type"/> and needs no modification (NFR-9).
/// </summary>
public interface IAgent
{
    AgentType Type { get; }
    Task<AgentResult> ExecuteAsync(AgentTaskInput input, WorkflowContext context, CancellationToken ct);
}

/// <summary>Scoping information for one node execution.</summary>
public sealed record AgentTaskInput(
    Guid WorkflowId,
    Guid NodeId,
    string NodeKey,
    string TaskName,
    string? TaskInstructionsJson,
    int Attempt);

/// <summary>
/// Everything an agent produced in one execution. The <c>NodeExecutor</c> persists this as a single
/// transaction: drafts become versioned rows, follow-up tasks drive graph expansion.
/// </summary>
public sealed record AgentResult(
    IReadOnlyList<ArtifactDraft> Artifacts,
    IReadOnlyList<DecisionDraft> Decisions,
    IReadOnlyList<RiskDraft> Risks,
    IReadOnlyList<RequirementDraft> Requirements,
    IReadOnlyList<ProposedTask> FollowUpTasks,
    string SummaryMarkdown)
{
    public static AgentResult Empty(string summary = "") => new(
        Array.Empty<ArtifactDraft>(), Array.Empty<DecisionDraft>(), Array.Empty<RiskDraft>(),
        Array.Empty<RequirementDraft>(), Array.Empty<ProposedTask>(), summary);
}

/// <summary>A produced artifact before the executor assigns id/version and persists it.</summary>
public sealed record ArtifactDraft(
    ArtifactType Type,
    string Name,
    string? ContentJson,
    string? ContentPath,
    IReadOnlyList<string> RequirementIds);

/// <summary>A recorded engineering decision before persistence.</summary>
public sealed record DecisionDraft(
    string Title,
    string Rationale,
    IReadOnlyList<AlternativeDraft> Alternatives,
    IReadOnlyList<string> RequirementIds);

public sealed record AlternativeDraft(string Name, IReadOnlyList<string> Pros, IReadOnlyList<string> Cons);

/// <summary>An identified risk before persistence.</summary>
public sealed record RiskDraft(
    RiskCategory Category,
    RiskLevel Severity,
    RiskLevel Likelihood,
    string Title,
    string Description,
    string Mitigation,
    IReadOnlyList<string> RequirementIds);

/// <summary>A requirement materialized by the Requirement Intelligence agent — the traceability anchor.</summary>
public sealed record RequirementDraft(
    string Code,
    RequirementKind Kind,
    string Text,
    string Priority,
    string? SourceExcerpt);

/// <summary>
/// A follow-up task proposed by an agent (chiefly the planner). The engine expands
/// <see cref="AgentType.Generation"/> tasks into graph nodes wired by <see cref="DependsOn"/>.
/// </summary>
public sealed record ProposedTask(
    string Id,
    string Name,
    string Description,
    AgentType Agent,
    WorkflowPhase Phase,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> RequirementIds);
