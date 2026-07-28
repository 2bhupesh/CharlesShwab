using AgenticSdlc.Core.Domain;

namespace AgenticSdlc.Core.Llm;

/// <summary>A single conversational turn passed to a provider.</summary>
public sealed record LlmMessage(string Role, string Content);

/// <summary>
/// A provider-agnostic completion request. <see cref="Metadata"/> carries routing hints — notably
/// <c>agent</c> and <c>scenario</c> — that the mock provider uses to select a canned response and
/// that the live provider records for lineage.
/// </summary>
public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    string Model,
    int MaxTokens,
    double Temperature = 0.2,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>A provider-agnostic completion result.</summary>
public sealed record LlmResponse(string Text, string Model, int InputTokens, int OutputTokens);

/// <summary>
/// Abstraction over the model backend. Two implementations exist: a live Anthropic adapter and a
/// deterministic mock. The selector chooses between them; agents never depend on a concrete provider.
/// </summary>
public interface ILlmProvider
{
    LlmProviderKind Kind { get; }
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
