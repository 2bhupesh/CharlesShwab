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

    // Generation agents emit full source files; the real model can exceed a small cap and get its JSON
    // truncated. 64k output tokens (the Sonnet 4.x ceiling) covers even a large single file. Mock unaffected.
    public int MaxTokens { get; set; } = 64000;

    /// <summary>In-conversation reparse attempts before escalating to a node-level retry.</summary>
    public int MaxJsonRetries { get; set; } = 2;

    /// <summary>Per-artifact context budget to keep prompts bounded (NFR-10).</summary>
    public int MaxContextCharsPerArtifact { get; set; } = 8000;
}

public sealed class OrchestrationOptions
{
    /// <summary>Global cap on concurrently executing nodes.</summary>
    public int MaxParallelNodes { get; set; } = 3;

    // Large live-model generations are slow (a full 64k-token file can stream for many minutes); 20
    // minutes leaves room for one to complete rather than timing out.
    public int DefaultNodeTimeoutSeconds { get; set; } = 1200;

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
