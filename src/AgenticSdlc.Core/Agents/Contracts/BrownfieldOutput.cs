namespace AgenticSdlc.Core.Agents.Contracts;

/// <summary>Structured output of the Brownfield Reasoning agent (FR-4).</summary>
public sealed record BrownfieldOutput
{
    public string RepoSummary { get; init; } = "";
    public List<Module> Modules { get; init; } = new();
    public List<string> DependencyFindings { get; init; } = new();
    public List<ImpactEntry> ImpactAnalysis { get; init; } = new();
    public List<Refactoring> Refactorings { get; init; } = new();
    public List<RiskEntry> Risks { get; init; } = new();
}

public sealed record Module(string Path, string Purpose);
public sealed record ImpactEntry(string ProposedChange, List<string> AffectedModules, string RiskLevel);
public sealed record Refactoring(string Title, string Priority, string Rationale);
