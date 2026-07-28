using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;

namespace AgenticSdlc.Core.Tests;

/// <summary>A test double that returns a scripted sequence of raw responses, one per call.</summary>
public sealed class ScriptedLlmProvider : ILlmProvider
{
    private readonly Queue<string> _responses;
    public int CallCount { get; private set; }

    public ScriptedLlmProvider(params string[] responses) => _responses = new Queue<string>(responses);

    public LlmProviderKind Kind => LlmProviderKind.Mock;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        CallCount++;
        var text = _responses.Count > 0 ? _responses.Dequeue() : "{}";
        return Task.FromResult(new LlmResponse(text, request.Model, 10, 20));
    }
}
