using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm.Mock;
using Microsoft.Extensions.Logging;

namespace AgenticSdlc.Core.Llm;

/// <summary>
/// The <see cref="ILlmProvider"/> the rest of the platform depends on. Resolves the active backend
/// from configuration and environment (spec §10): <c>Auto</c> uses Anthropic when
/// <c>ANTHROPIC_API_KEY</c> is present, otherwise the deterministic mock. When the primary is
/// Anthropic and a call fails in Auto mode, it degrades to the mock rather than aborting the
/// workflow (FR-25) — both paths run identical orchestration code.
/// </summary>
public sealed class LlmProviderSelector : ILlmProvider
{
    private readonly AnthropicLlmProvider _anthropic;
    private readonly MockLlmProvider _mock;
    private readonly ILogger<LlmProviderSelector>? _logger;
    private readonly bool _allowFallback;

    public LlmProviderSelector(
        CoreOptions options,
        AnthropicLlmProvider anthropic,
        MockLlmProvider mock,
        ILogger<LlmProviderSelector>? logger = null)
    {
        _anthropic = anthropic;
        _mock = mock;
        _logger = logger;

        var configured = options.Llm.Provider?.Trim() ?? "Auto";
        var keyPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        Kind = configured.ToLowerInvariant() switch
        {
            "anthropic" => LlmProviderKind.Anthropic,
            "mock" => LlmProviderKind.Mock,
            _ => keyPresent ? LlmProviderKind.Anthropic : LlmProviderKind.Mock // Auto
        };

        // Only fall back Anthropic->Mock when we were auto-selecting, never when explicitly pinned.
        _allowFallback = configured.Equals("Auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The active provider kind, resolved once at construction.</summary>
    public LlmProviderKind Kind { get; }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (Kind == LlmProviderKind.Mock)
            return await _mock.CompleteAsync(request, ct);

        try
        {
            return await _anthropic.CompleteAsync(request, ct);
        }
        catch (Exception ex) when (_allowFallback && ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Anthropic provider failed; falling back to deterministic mock.");
            return await _mock.CompleteAsync(request, ct);
        }
    }
}
