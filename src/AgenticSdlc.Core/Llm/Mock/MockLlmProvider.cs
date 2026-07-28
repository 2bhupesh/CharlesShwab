using AgenticSdlc.Core.Domain;

namespace AgenticSdlc.Core.Llm.Mock;

/// <summary>
/// Deterministic provider that returns canned, schema-valid responses per (scenario, agent). Enables
/// the entire platform — orchestration, governance, validation — to run offline with no credentials
/// and no non-determinism (NFR-5, AS-5). Identical input always yields identical output.
/// </summary>
public sealed class MockLlmProvider : ILlmProvider
{
    private readonly MockResponseCatalog _catalog;

    public MockLlmProvider(MockResponseCatalog catalog) => _catalog = catalog;

    public LlmProviderKind Kind => LlmProviderKind.Mock;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var scenario = request.Metadata?.GetValueOrDefault("scenario") ?? "default";
        var agent = request.Metadata?.GetValueOrDefault("agent") ?? "unknown";
        var variant = request.Metadata?.GetValueOrDefault("node");

        // When a human reviewer has answered a clarification, prefer a '.clarified' response so the
        // ambiguous scenario converges to a concrete spec on the re-run.
        var clarified = request.Messages.Any(m =>
            m.Content.Contains("provided by a human reviewer", StringComparison.OrdinalIgnoreCase));

        var body = (clarified ? _catalog.ResolveExact(scenario, agent, "clarified") : null)
            ?? _catalog.Resolve(scenario, agent, variant)
            ?? throw new InvalidOperationException(
                $"No mock response for scenario '{scenario}', agent '{agent}'. " +
                "Add Llm/Mock/Responses/{scenario}.{agent}.json (or default.{agent}.json).");

        // Deterministic pseudo token counts derived from length — stable across identical inputs.
        var input = (request.SystemPrompt.Length + request.Messages.Sum(m => m.Content.Length)) / 4;
        var output = body.Length / 4;

        return Task.FromResult(new LlmResponse(body, request.Model + "-mock", input, output));
    }
}
