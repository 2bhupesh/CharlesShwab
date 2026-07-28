namespace AgenticSdlc.Core.Agents.Contracts;

/// <summary>Structured output of the Risk Assessment agent (FR-7); also embedded in brownfield output.</summary>
public sealed record RiskOutput
{
    public List<RiskEntry> Risks { get; init; } = new();
}

public sealed record RiskEntry(
    string Id,
    string Category,
    string Severity,
    string Likelihood,
    string Title,
    string Description,
    string Mitigation,
    List<string> RequirementIds);
