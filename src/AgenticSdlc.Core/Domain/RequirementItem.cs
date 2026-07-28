namespace AgenticSdlc.Core.Domain;

/// <summary>
/// A single requirement materialized from the Requirement Intelligence agent's specification.
/// This is the anchor every downstream artifact and decision references, and the denominator of
/// requirement coverage (FR-30). <see cref="Code"/> is scenario-agnostic (<c>FR-1</c>, <c>NFR-2</c>,
/// <c>AS-1</c>, <c>OQ-1</c>).
/// </summary>
public class RequirementItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    public string Code { get; set; } = "";

    public RequirementKind Kind { get; set; }

    public string Text { get; set; } = "";

    public string Priority { get; set; } = "";

    /// <summary>Excerpt of the original requirement text that motivated this item.</summary>
    public string? SourceExcerpt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
