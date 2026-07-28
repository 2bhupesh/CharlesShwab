using System.Diagnostics;
using System.Text;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Template method base owning all model plumbing: prompt assembly from context, invocation,
/// structured-output extraction, in-conversation reparse retry, and prompt-lineage recording. A
/// concrete agent supplies only its system prompt, a user-prompt builder, and a mapping from parsed
/// output to <see cref="AgentResult"/> (spec §7.1). Every call — including reparse attempts — is
/// persisted as an <see cref="AgentExecution"/> row (FR-26).
/// </summary>
public abstract class AgentBase<TOutput> : IAgent
{
    private readonly ILlmProvider _llm;
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly CoreOptions _options;

    protected AgentBase(ILlmProvider llm, IDbContextFactory<AgenticDbContext> dbFactory, CoreOptions options)
    {
        _llm = llm;
        _dbFactory = dbFactory;
        _options = options;
    }

    public abstract AgentType Type { get; }

    /// <summary>Role instructions plus the literal output JSON schema the model must follow.</summary>
    protected abstract string SystemPrompt { get; }

    /// <summary>Builds the task-specific user prompt from the input and assembled context.</summary>
    protected abstract string BuildUserPrompt(AgentTaskInput input, WorkflowContext ctx);

    /// <summary>Maps validated structured output into artifacts, decisions, risks, and follow-ups.</summary>
    protected abstract Task<AgentResult> MapOutputAsync(TOutput output, AgentTaskInput input, WorkflowContext ctx, CancellationToken ct);

    public async Task<AgentResult> ExecuteAsync(AgentTaskInput input, WorkflowContext ctx, CancellationToken ct)
    {
        var messages = new List<LlmMessage> { new("user", BuildUserPrompt(input, ctx)) };
        var metadata = new Dictionary<string, string>
        {
            ["agent"] = Type.ToString(),
            ["scenario"] = ctx.ScenarioKey,
            ["node"] = input.NodeKey // lets the mock return per-node responses (e.g. per generation task)
        };

        string? lastError = null;
        for (int attempt = 1; attempt <= _options.Llm.MaxJsonRetries + 1; attempt++)
        {
            var request = new LlmRequest(SystemPrompt, messages, _options.Llm.Model, _options.Llm.MaxTokens, 0.2, metadata);

            var sw = Stopwatch.StartNew();
            var response = await _llm.CompleteAsync(request, ct);
            sw.Stop();

            var (ok, value, error) = JsonExtractor.TryParse<TOutput>(response.Text);
            await RecordExecutionAsync(input, messages, response, ok, error, (int)sw.ElapsedMilliseconds, ct);

            if (ok)
                return await MapOutputAsync(value!, input, ctx, ct);

            // If the response hit the output-token cap it was truncated mid-JSON. Re-asking would only
            // re-truncate (and with a larger context each time, risking a node timeout), so fail fast
            // with an actionable message instead of the in-conversation reparse.
            if (response.OutputTokens >= _options.Llm.MaxTokens)
                throw new AgentOutputException(
                    $"{Type} agent response was truncated at the {_options.Llm.MaxTokens}-token output cap. " +
                    "The generated content exceeds the limit — raise AgenticSdlc:Llm:MaxTokens (and, for large " +
                    "outputs, AgenticSdlc:Orchestration:DefaultNodeTimeoutSeconds).");

            lastError = error;
            // Feed the malformed reply back and ask for a clean retry (cheap, in-conversation).
            messages.Add(new("assistant", response.Text));
            messages.Add(new("user",
                $"Your previous response was not valid JSON matching the required schema ({error}). " +
                "Respond again with ONLY the JSON object — no prose, no markdown fences."));
        }

        throw new AgentOutputException(
            $"{Type} agent failed to produce valid structured output after {_options.Llm.MaxJsonRetries + 1} attempts: {lastError}");
    }

    private async Task RecordExecutionAsync(
        AgentTaskInput input, IReadOnlyList<LlmMessage> messages, LlmResponse response, bool ok, string? error, int durationMs, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.AgentExecutions.Add(new AgentExecution
        {
            WorkflowId = input.WorkflowId,
            NodeId = input.NodeId,
            AgentType = Type,
            Attempt = input.Attempt,
            Provider = _llm.Kind,
            Model = response.Model,
            SystemPrompt = SystemPrompt,
            // Capture the actual conversation sent, including any reparse turns (FR-26).
            UserPrompt = string.Join("\n---\n", messages.Select(m => $"[{m.Role}] {m.Content}")),
            RawResponse = response.Text,
            ParsedOk = ok,
            ParseError = error,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            DurationMs = durationMs
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Renders the assembled context (requirements, prior decisions, upstream artifacts) as text for
    /// inclusion in a user prompt. Shared by all agents so context propagation is uniform.
    /// </summary>
    protected string RenderContext(WorkflowContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Original requirement");
        sb.AppendLine(ctx.RequirementText);

        if (ctx.Requirements.Count > 0)
        {
            sb.AppendLine("\n## Established requirements");
            foreach (var r in ctx.Requirements)
                sb.AppendLine($"- {r.Code} ({r.Kind}): {r.Text}");
        }

        if (ctx.Decisions.Count > 0)
        {
            sb.AppendLine("\n## Prior decisions");
            foreach (var d in ctx.Decisions)
                sb.AppendLine($"- {d.Title}: {d.Rationale}");
        }

        if (ctx.UpstreamArtifacts.Count > 0)
        {
            sb.AppendLine("\n## Upstream artifacts");
            foreach (var a in ctx.UpstreamArtifacts)
            {
                sb.AppendLine($"### {a.Type} — {a.Name} (v{a.Version})");
                sb.AppendLine(a.ContentSnippet);
            }
        }

        return sb.ToString();
    }
}
