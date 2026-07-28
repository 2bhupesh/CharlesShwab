namespace AgenticSdlc.Core.Agents.Contracts;

/// <summary>
/// Structured output of the Validation agent (FR-6). Build and test fields are facts obtained from
/// the real toolchain; conformance fields are the model's judgement. Gate policies evaluate the
/// facts, so progression is never authorized by model opinion alone (spec §7.4).
/// </summary>
public sealed record ValidationOutput
{
    public bool BuildSucceeded { get; init; }
    public List<string> BuildErrors { get; init; } = new();
    public int TestsTotal { get; init; }
    public int TestsPassed { get; init; }
    public int TestsFailed { get; init; }
    public List<StaticFinding> StaticFindings { get; init; } = new();
    public List<StaticFinding> SecurityFindings { get; init; } = new();
    public Conformance? ArchitectureConformance { get; init; }
    public Conformance? ApiConformance { get; init; }
    public Conformance? DocCoverage { get; init; }
    /// <summary><c>pass</c>, <c>fail</c>, or <c>skipped</c> (toolchain unavailable).</summary>
    public string Overall { get; init; } = "";
    public List<string> Recommendations { get; init; } = new();
}

public sealed record StaticFinding(string File, int Line, string Rule, string Message, string Severity);

public sealed record Conformance(bool Pass, string Notes);
