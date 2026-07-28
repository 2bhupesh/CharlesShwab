namespace AgenticSdlc.Core.Agents.Contracts;

/// <summary>Structured output of the Architecture Reasoning agent (FR-3).</summary>
public sealed record ArchOutput
{
    public string SelectedStyle { get; init; } = "";
    public List<ArchAlternative> Alternatives { get; init; } = new();
    public List<Adr> Adrs { get; init; } = new();
    public List<Component> Components { get; init; } = new();
    public List<ServiceContract> ServiceContracts { get; init; } = new();
    public List<TechChoice> TechStack { get; init; } = new();
    public string ComponentDiagramMermaid { get; init; } = "";
}

public sealed record ArchAlternative(string Name, List<string> Pros, List<string> Cons);
public sealed record Adr(string Id, string Title, string Context, string Decision, string Consequences, List<string> RequirementIds);
public sealed record Component(string Name, string Responsibility, List<string> DependsOn);
public sealed record ServiceContract(string Operation, string Method, string Path, string RequestShape, string ResponseShape);
public sealed record TechChoice(string Area, string Choice, string Rationale);
