namespace AgenticSdlc.Core.Agents.Contracts;

/// <summary>Structured output of the Engineering Generation agent (FR-5): a set of files to write.</summary>
public sealed record GenOutput
{
    public List<GenFile> Files { get; init; } = new();
    public string BuildNotes { get; init; } = "";
    public List<string> RequirementIds { get; init; } = new();
}

/// <summary><see cref="Kind"/> is source|test|openapi|dbscript|iac|doc|releaseNotes|project.</summary>
public sealed record GenFile(string Path, string Kind, string Content);

/// <summary>The judgement portion of validation — the one LLM call the Validation agent makes.</summary>
public sealed record ConformanceOutput
{
    public Conformance? ArchitectureConformance { get; init; }
    public Conformance? ApiConformance { get; init; }
    public Conformance? DocCoverage { get; init; }
    public List<string> Recommendations { get; init; } = new();
}
