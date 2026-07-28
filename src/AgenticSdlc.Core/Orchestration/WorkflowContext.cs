using AgenticSdlc.Core.Domain;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>A trimmed reference to an upstream artifact injected into an agent prompt.</summary>
public sealed record ArtifactRef(
    Guid Id,
    ArtifactType Type,
    string Name,
    int Version,
    string ContentSnippet,
    string? ContentPath);

/// <summary>
/// The complete engineering context assembled for one agent execution (spec §4.5). It is the entire
/// integration surface between agents — decisions and assumptions made upstream reach downstream
/// agents only through this record. Built by <c>WorkflowContextBuilder</c> (WP-4).
/// </summary>
public sealed record WorkflowContext(
    Guid WorkflowId,
    string RequirementText,
    string ScenarioKey,
    IReadOnlyList<RequirementItem> Requirements,
    IReadOnlyList<Decision> Decisions,
    IReadOnlyList<RiskItem> OpenRisks,
    IReadOnlyList<ArtifactRef> UpstreamArtifacts,
    string WorkspacePath)
{
    /// <summary>An empty context for early nodes with no upstream (e.g. the spec node).</summary>
    public static WorkflowContext Empty(Guid workflowId, string requirementText, string scenarioKey, string workspacePath) =>
        new(workflowId, requirementText, scenarioKey,
            Array.Empty<RequirementItem>(), Array.Empty<Decision>(), Array.Empty<RiskItem>(),
            Array.Empty<ArtifactRef>(), workspacePath);
}
