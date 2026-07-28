using System.Text;
using AgenticSdlc.Core.Domain;
using Anthropic;
using Anthropic.Models.Messages;

namespace AgenticSdlc.Core.Llm;

/// <summary>
/// The single file coupled to the Anthropic 12.39.0 SDK surface (spec §7, WP-2 risk isolation). The
/// client is constructed lazily and reads <c>ANTHROPIC_API_KEY</c> from the environment — matching
/// the selector's trigger — so no key is ever held in configuration.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider, IDisposable
{
    private readonly Lazy<AnthropicClient> _client = new(() => new AnthropicClient());

    public LlmProviderKind Kind => LlmProviderKind.Anthropic;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,                 // implicit string -> ApiEnum<string, Model>
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            System = request.SystemPrompt,          // implicit string -> MessageCreateParamsSystem
            Messages = request.Messages
                .Select(m => new MessageParam
                {
                    Role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                        ? Role.Assistant
                        : Role.User,
                    Content = m.Content            // implicit string -> MessageParamContent
                })
                .ToList()
        };

        var message = await _client.Value.Messages.Create(parameters, ct);

        var text = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock))
                text.Append(textBlock.Text);
        }

        return new LlmResponse(
            text.ToString(),
            request.Model,
            (int)message.Usage.InputTokens,
            (int)message.Usage.OutputTokens);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
            _client.Value.Dispose();
    }
}
