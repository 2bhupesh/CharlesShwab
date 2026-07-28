using System.Text.Json;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;

namespace AgenticSdlc.Core.Governance;

/// <summary>
/// A governance checkpoint declaration, serialized onto a node's <c>GatesJson</c>. Evaluated at the
/// appropriate <see cref="GateStage"/> by an <see cref="IGateEvaluator"/> (spec §5.1).
/// </summary>
public sealed record GateDefinition(
    GateStage Stage,
    GateType Type,
    string? PolicyName,
    string Description,
    string? ParametersJson = null)
{
    public static string Serialize(IEnumerable<GateDefinition> gates) =>
        JsonSerializer.Serialize(gates, JsonExtractor.SerializerOptions);

    public static List<GateDefinition> Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<GateDefinition>()
            : JsonSerializer.Deserialize<List<GateDefinition>>(json, JsonExtractor.SerializerOptions) ?? new();

    public static GateDefinition Human(GateStage stage, string description) =>
        new(stage, GateType.HumanApproval, null, description);

    public static GateDefinition Policy(GateStage stage, string policyName, string description, string? parametersJson = null) =>
        new(stage, GateType.Policy, policyName, description, parametersJson);
}

/// <summary>How the engine should react to a gate evaluation.</summary>
public enum GateDecision
{
    Passed,
    Failed,
    AwaitingHuman
}

/// <summary>Aggregate outcome of evaluating a node's gates at one stage.</summary>
public sealed record GateOutcome(GateDecision Decision, string? Reason = null)
{
    public static readonly GateOutcome Pass = new(GateDecision.Passed);
    public static GateOutcome Fail(string reason) => new(GateDecision.Failed, reason);
    public static GateOutcome Await(string reason) => new(GateDecision.AwaitingHuman, reason);
}

/// <summary>
/// Evaluates a node's gates. The engine depends only on this seam; the real implementation (policies
/// + human approval) arrives in WP-5, while <see cref="AutoPassGateEvaluator"/> keeps the engine
/// testable in isolation.
/// </summary>
public interface IGateEvaluator
{
    Task<GateOutcome> EvaluateAsync(WorkflowNode node, GateStage stage, CancellationToken ct);
}

/// <summary>A no-governance evaluator that passes every gate. Replaced by real governance in WP-5.</summary>
public sealed class AutoPassGateEvaluator : IGateEvaluator
{
    public Task<GateOutcome> EvaluateAsync(WorkflowNode node, GateStage stage, CancellationToken ct) =>
        Task.FromResult(GateOutcome.Pass);
}
