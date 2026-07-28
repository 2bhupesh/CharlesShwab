using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Orchestration;

namespace AgenticSdlc.Core.Tests;

/// <summary>A configurable agent test double for exercising engine mechanics without a real model.</summary>
public sealed class FakeAgent : IAgent
{
    private readonly Func<AgentTaskInput, WorkflowContext, CancellationToken, Task<AgentResult>> _run;

    public FakeAgent(AgentType type, Func<AgentTaskInput, WorkflowContext, CancellationToken, Task<AgentResult>> run)
    {
        Type = type;
        _run = run;
    }

    public AgentType Type { get; }

    public Task<AgentResult> ExecuteAsync(AgentTaskInput input, WorkflowContext context, CancellationToken ct) =>
        _run(input, context, ct);

    /// <summary>A fake that immediately succeeds with a single artifact of the given type.</summary>
    public static FakeAgent Ok(AgentType type, ArtifactType artifact) =>
        new(type, (_, _, _) => Task.FromResult(new AgentResult(
            new[] { new ArtifactDraft(artifact, type.ToString(), "{}", null, Array.Empty<string>()) },
            Array.Empty<DecisionDraft>(), Array.Empty<RiskDraft>(), Array.Empty<RequirementDraft>(),
            Array.Empty<ProposedTask>(), $"{type} done")));
}

/// <summary>Tracks concurrent executions to prove independent nodes actually run in parallel.</summary>
public sealed class ConcurrencyTracker
{
    private int _current;
    public int MaxObserved { get; private set; }
    private readonly object _lock = new();

    public IDisposable Enter()
    {
        lock (_lock)
        {
            _current++;
            if (_current > MaxObserved) MaxObserved = _current;
        }
        return new Exit(this);
    }

    private void Leave() { lock (_lock) _current--; }

    private sealed class Exit(ConcurrencyTracker t) : IDisposable
    {
        public void Dispose() => t.Leave();
    }
}
