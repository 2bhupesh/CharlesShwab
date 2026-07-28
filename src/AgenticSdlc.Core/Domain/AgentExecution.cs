namespace AgenticSdlc.Core.Domain;

/// <summary>
/// Prompt lineage (FR-26): one row per model invocation, including malformed-JSON reparse attempts.
/// Captures the exact prompts and response so any agent decision is fully auditable, plus token
/// usage and timing that feed metrics and cost accounting.
/// </summary>
public class AgentExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    public Guid NodeId { get; set; }

    public AgentType AgentType { get; set; }

    /// <summary>Node attempt this call belongs to.</summary>
    public int Attempt { get; set; }

    public LlmProviderKind Provider { get; set; }

    public string Model { get; set; } = "";

    public string SystemPrompt { get; set; } = "";

    public string UserPrompt { get; set; } = "";

    public string RawResponse { get; set; } = "";

    /// <summary>Whether the response parsed into the expected structured output.</summary>
    public bool ParsedOk { get; set; }

    public string? ParseError { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
