namespace AgenticSdlc.Core.Domain;

/// <summary>
/// A versioned engineering output. Content is either inline (<see cref="ContentJson"/>) or a
/// workspace-relative file path (<see cref="ContentPath"/>). Supersession is a lineage chain —
/// predecessors are retained, never deleted (spec §3.2). <see cref="RequirementIdsJson"/> is the
/// traceability link back to the originating requirement (FR-29).
/// </summary>
public class Artifact
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    public Guid ProducedByNodeId { get; set; }

    public ArtifactType Type { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Incremented each time the producing node re-executes.</summary>
    public int Version { get; set; } = 1;

    public ArtifactStatus Status { get; set; } = ArtifactStatus.Draft;

    /// <summary>Structured payload for inline artifacts (spec, plan, ADRs, validation report…).</summary>
    public string? ContentJson { get; set; }

    /// <summary>Workspace-relative path for file artifacts (generated source, tests…).</summary>
    public string? ContentPath { get; set; }

    /// <summary>JSON array of requirement codes (e.g. <c>["FR-1","FR-4"]</c>) this artifact satisfies.</summary>
    public string RequirementIdsJson { get; set; } = "[]";

    /// <summary>Set when a newer version replaces this one; the row itself is retained.</summary>
    public Guid? SupersededByArtifactId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
