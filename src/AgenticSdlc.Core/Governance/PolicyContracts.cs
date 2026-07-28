using AgenticSdlc.Core.Domain;

namespace AgenticSdlc.Core.Governance;

/// <summary>A question the platform needs a human to answer to resolve a blocking ambiguity.</summary>
public sealed record ClarificationQuestion(
    string QuestionId,
    string Question,
    string? Rationale = null,
    IReadOnlyList<string>? SuggestedOptions = null);

/// <summary>A human's answer to a clarification question.</summary>
public sealed record ClarificationAnswer(string QuestionId, string Answer);

/// <summary>
/// Outcome of a policy evaluation. A policy may pass, fail, or (only the ambiguity policy) request
/// human clarification instead of failing outright.
/// </summary>
public sealed record PolicyResult(bool Passed, string Evidence, IReadOnlyList<ClarificationQuestion>? Clarifications = null)
{
    public static PolicyResult Ok(string evidence = "passed") => new(true, evidence);
    public static PolicyResult Fail(string evidence) => new(false, evidence);
    public static PolicyResult NeedsClarification(IReadOnlyList<ClarificationQuestion> questions, string evidence) =>
        new(false, evidence, questions);
}

/// <summary>
/// An automated governance policy (spec §5.2). Evaluated by the <see cref="GateEvaluator"/> when a
/// policy gate is reached. Implementations read persisted state to reach a verdict with evidence.
/// </summary>
public interface IGatePolicy
{
    string Name { get; }
    Task<PolicyResult> EvaluateAsync(WorkflowNode node, string? parametersJson, CancellationToken ct);
}
