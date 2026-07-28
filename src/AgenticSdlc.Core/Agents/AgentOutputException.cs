namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Thrown when an agent cannot obtain schema-valid structured output after exhausting in-conversation
/// reparse attempts. The node executor's node-level retry (with backoff) then takes over.
/// </summary>
public sealed class AgentOutputException : Exception
{
    public AgentOutputException(string message) : base(message) { }
}
