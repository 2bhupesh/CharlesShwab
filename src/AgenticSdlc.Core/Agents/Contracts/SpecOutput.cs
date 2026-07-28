namespace AgenticSdlc.Core.Agents.Contracts;

/// <summary>Structured output of the Requirement Intelligence agent (FR-1).</summary>
public sealed record SpecOutput
{
    public string IntentSummary { get; init; } = "";
    public List<SpecRequirement> FunctionalRequirements { get; init; } = new();
    public List<SpecRequirement> NonFunctionalRequirements { get; init; } = new();
    public List<SpecAmbiguity> Ambiguities { get; init; } = new();
    public List<SpecAssumption> Assumptions { get; init; } = new();
    public List<SpecOpenQuestion> OpenQuestions { get; init; } = new();
}

public sealed record SpecRequirement(string Id, string Title, string Description, string Priority, string? SourceExcerpt);

public sealed record SpecAmbiguity(string Text, string ClarifyingQuestion, string Severity)
{
    public bool IsBlocking => Severity.Equals("blocking", StringComparison.OrdinalIgnoreCase);
}

public sealed record SpecAssumption(string Id, string Text, string Rationale);

public sealed record SpecOpenQuestion(string Id, string Text);
