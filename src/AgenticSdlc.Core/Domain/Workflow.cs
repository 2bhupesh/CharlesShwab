namespace AgenticSdlc.Core.Domain;

/// <summary>
/// One end-to-end execution of the SDLC for one natural-language requirement. The root aggregate
/// and the anchor of all traceability. <see cref="RequirementText"/> is stored verbatim.
/// </summary>
public class Workflow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly label.</summary>
    public string Name { get; set; } = "";

    /// <summary>The original ask, verbatim — the root of every lineage chain.</summary>
    public string RequirementText { get; set; } = "";

    /// <summary>
    /// Seed-data selector (<c>greenfield</c>/<c>brownfield</c>/<c>ambiguous</c>). Platform behaviour
    /// is not branched on this beyond graph seeding — scenarios are data, not code paths.
    /// </summary>
    public string ScenarioKey { get; set; } = "greenfield";

    public WorkflowStatus Status { get; set; } = WorkflowStatus.Draft;

    /// <summary>Model identifier in use (e.g. <c>claude-sonnet-5</c>).</summary>
    public string Model { get; set; } = "";

    /// <summary>Absolute root of this workflow's generated artifacts.</summary>
    public string WorkspacePath { get; set; } = "";

    /// <summary>Brownfield: the prior run whose output is being enhanced (null otherwise).</summary>
    public Guid? SourceWorkflowId { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Navigation collections (optional; queries mostly go through the context factory directly).
    public List<WorkflowNode> Nodes { get; set; } = new();
    public List<DependencyEdge> Edges { get; set; } = new();
}
