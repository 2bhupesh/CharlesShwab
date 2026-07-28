namespace AgenticSdlc.Core;

/// <summary>
/// Strongly-typed configuration bound from the <c>AgenticSdlc</c> section (spec §10). Credentials are
/// never held here — the API key is read from the <c>ANTHROPIC_API_KEY</c> environment variable only.
/// </summary>
public sealed class CoreOptions
{
    public const string SectionName = "AgenticSdlc";

    public LlmOptions Llm { get; set; } = new();
    public OrchestrationOptions Orchestration { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();
    public WorkspaceOptions Workspace { get; set; } = new();
}

public sealed class LlmOptions
{
    /// <summary><c>Auto</c> (default), <c>Anthropic</c>, or <c>Mock</c>.</summary>
    public string Provider { get; set; } = "Auto";

    public string Model { get; set; } = "claude-sonnet-5";

    public int MaxTokens { get; set; } = 8000;

    /// <summary>In-conversation reparse attempts before escalating to a node-level retry.</summary>
    public int MaxJsonRetries { get; set; } = 2;

    /// <summary>Per-artifact context budget to keep prompts bounded (NFR-10).</summary>
    public int MaxContextCharsPerArtifact { get; set; } = 8000;
}

public sealed class OrchestrationOptions
{
    /// <summary>Global cap on concurrently executing nodes.</summary>
    public int MaxParallelNodes { get; set; } = 3;

    public int DefaultNodeTimeoutSeconds { get; set; } = 300;

    public int MaxAttempts { get; set; } = 3;

    public int RetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>Bound on ambiguous-requirement clarification rounds before proceeding on assumptions.</summary>
    public int ClarificationMaxRounds { get; set; } = 2;
}

public sealed class PersistenceOptions
{
    /// <summary>Relative to content root unless absolute.</summary>
    public string DbPath { get; set; } = "data/agentic.db";
}

public sealed class WorkspaceOptions
{
    public string Root { get; set; } = "workspaces";

    public string SamplesRoot { get; set; } = "samples";
}
