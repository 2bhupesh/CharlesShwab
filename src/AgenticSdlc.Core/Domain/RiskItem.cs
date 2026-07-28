namespace AgenticSdlc.Core.Domain;

/// <summary>
/// A risk identified by the Risk Assessment agent (or surfaced during brownfield analysis), with a
/// recommended mitigation. Severity-ranked and surfaced in the Risks tab and review package (FR-7).
/// </summary>
public class RiskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    public Guid NodeId { get; set; }

    public RiskCategory Category { get; set; }

    public RiskLevel Severity { get; set; }

    public RiskLevel Likelihood { get; set; }

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public string Mitigation { get; set; } = "";

    public RiskStatus Status { get; set; } = RiskStatus.Open;

    /// <summary>JSON array of requirement codes this risk relates to.</summary>
    public string RequirementIdsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
