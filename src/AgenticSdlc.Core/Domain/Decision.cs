namespace AgenticSdlc.Core.Domain;

/// <summary>
/// A recorded engineering choice with rationale, linked to the requirements that motivated it and
/// the artifacts it produced. Every ADR, technology selection, sequencing choice, and documented
/// assumption becomes one of these rows — this is decision lineage (FR-14).
/// </summary>
public class Decision
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    public Guid NodeId { get; set; }

    public AgentType AgentType { get; set; }

    public string Title { get; set; } = "";

    public string Rationale { get; set; } = "";

    /// <summary>JSON array of alternatives considered, each with pros/cons — feeds trade-off analysis.</summary>
    public string AlternativesJson { get; set; } = "[]";

    /// <summary>JSON array of requirement codes this decision serves.</summary>
    public string RequirementIdsJson { get; set; } = "[]";

    /// <summary>JSON array of artifact ids produced or affected by this decision.</summary>
    public string ArtifactIdsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
