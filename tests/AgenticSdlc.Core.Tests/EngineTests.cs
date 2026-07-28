using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Orchestration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-4 verification: the orchestration engine's mechanics — graph shape, parallel dispatch, join
/// synchronization, retry with backoff, failure isolation, pause/resume, and an end-to-end run to
/// Completed — proven with fake agents (real agents arrive in WP-6).
/// </summary>
public class EngineTests
{
    // A full set of trivial fakes; the planner emits two independent gen tasks plus one that joins them.
    private static List<IAgent> StandardAgents(ConcurrencyTracker? genTracker = null, int genDelayMs = 0)
    {
        var plan = new FakeAgent(AgentType.Planning, (_, _, _) => Task.FromResult(new AgentResult(
            new[] { new ArtifactDraft(ArtifactType.WorkPlan, "plan", "{}", null, Array.Empty<string>()) },
            Array.Empty<DecisionDraft>(), Array.Empty<RiskDraft>(), Array.Empty<RequirementDraft>(),
            new[]
            {
                new ProposedTask("a", "Gen A", "independent", AgentType.Generation, WorkflowPhase.Generation, Array.Empty<string>(), Array.Empty<string>()),
                new ProposedTask("b", "Gen B", "independent", AgentType.Generation, WorkflowPhase.Generation, Array.Empty<string>(), Array.Empty<string>()),
                new ProposedTask("c", "Gen C", "joins a and b", AgentType.Generation, WorkflowPhase.Generation, new[] { "a", "b" }, Array.Empty<string>()),
            },
            "planned")));

        var generation = new FakeAgent(AgentType.Generation, async (_, _, ct) =>
        {
            using var _scope = genTracker?.Enter();
            if (genDelayMs > 0) await Task.Delay(genDelayMs, ct);
            return AgentResult.Empty("generated");
        });

        return new List<IAgent>
        {
            FakeAgent.Ok(AgentType.RequirementIntelligence, ArtifactType.EngineeringSpecification),
            plan,
            FakeAgent.Ok(AgentType.Architecture, ArtifactType.AdrSet),
            FakeAgent.Ok(AgentType.Brownfield, ArtifactType.BrownfieldReport),
            FakeAgent.Ok(AgentType.RiskAssessment, ArtifactType.RiskReport),
            generation,
            FakeAgent.Ok(AgentType.Validation, ArtifactType.ValidationReport),
        };
    }

    [Fact]
    public async Task Template_graph_has_expected_shape()
    {
        await using var h = await EngineHarness.CreateAsync(StandardAgents());
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");

        var nodes = await h.NodesAsync(id);
        var keys = nodes.Select(n => n.Key).ToHashSet();
        foreach (var expected in new[] { "spec", "brownfield", "plan", "arch", "risk", "gen.ready", "gen.done", "validate", "package" })
            Assert.Contains(expected, keys);

        // Greenfield: the brownfield node is Skipped, not run.
        Assert.Equal(NodeStatus.Skipped, nodes.Single(n => n.Key == "brownfield").Status);
    }

    [Fact]
    public async Task Runs_to_completion_and_expands_generation_nodes()
    {
        await using var h = await EngineHarness.CreateAsync(StandardAgents());
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        var status = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkflowStatus.Completed, status);

        var nodes = await h.NodesAsync(id);
        // The three planner tasks became gen nodes.
        foreach (var key in new[] { "gen.a", "gen.b", "gen.c" })
            Assert.Equal(NodeStatus.Succeeded, nodes.Single(n => n.Key == key).Status);
        // Every non-skipped node succeeded.
        Assert.All(nodes.Where(n => n.Status != NodeStatus.Skipped),
            n => Assert.Equal(NodeStatus.Succeeded, n.Status));
    }

    [Fact]
    public async Task Independent_generation_nodes_run_in_parallel()
    {
        var tracker = new ConcurrencyTracker();
        await using var h = await EngineHarness.CreateAsync(
            StandardAgents(tracker, genDelayMs: 100),
            o => o.Orchestration.MaxParallelNodes = 4);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        var status = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowStatus.Completed, status);
        // gen.a and gen.b are independent — they must have overlapped.
        Assert.True(tracker.MaxObserved >= 2, $"expected parallel generation, max observed = {tracker.MaxObserved}");
    }

    [Fact]
    public async Task Join_waits_for_all_inbound_branches()
    {
        await using var h = await EngineHarness.CreateAsync(StandardAgents(genDelayMs: 50));
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);
        await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(20));

        var nodes = await h.NodesAsync(id);
        var genDone = nodes.Single(n => n.Key == "gen.done");
        var genNodes = nodes.Where(n => n.Key.StartsWith("gen.") && n.AgentType == AgentType.Generation).ToList();
        Assert.Equal(3, genNodes.Count);
        // The synchronization point completed only after every generation branch finished.
        foreach (var g in genNodes)
            Assert.True(genDone.CompletedAt >= g.CompletedAt, $"{g.Key} finished after the join");
    }

    [Fact]
    public async Task Node_retries_with_backoff_then_succeeds()
    {
        var attempts = 0;
        var agents = StandardAgents();
        agents.RemoveAll(a => a.Type == AgentType.RiskAssessment);
        agents.Add(new FakeAgent(AgentType.RiskAssessment, (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("transient");
            return Task.FromResult(AgentResult.Empty("ok"));
        }));

        await using var h = await EngineHarness.CreateAsync(agents, o => o.Orchestration.RetryBaseDelaySeconds = 1);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        var status = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowStatus.Completed, status);
        var risk = await h.NodeAsync(id, "risk");
        Assert.Equal(NodeStatus.Succeeded, risk.Status);
        Assert.Equal(2, risk.Attempt); // failed once, succeeded on the second attempt
    }

    [Fact]
    public async Task Critical_node_failure_fails_the_workflow()
    {
        var agents = StandardAgents();
        agents.RemoveAll(a => a.Type == AgentType.Architecture);
        agents.Add(new FakeAgent(AgentType.Architecture, (_, _, _) =>
            throw new InvalidOperationException("boom")));

        await using var h = await EngineHarness.CreateAsync(agents, o =>
        {
            o.Orchestration.MaxAttempts = 1;
            o.Orchestration.RetryBaseDelaySeconds = 1;
        });
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        var status = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowStatus.Failed, status);
        Assert.Equal(NodeStatus.Failed, (await h.NodeAsync(id, "arch")).Status);
    }

    [Fact]
    public async Task Safe_stop_reverts_in_flight_node_without_consuming_attempt()
    {
        // A slow spec agent lets us pause while it is executing.
        var agents = StandardAgents();
        agents.RemoveAll(a => a.Type == AgentType.RequirementIntelligence);
        agents.Add(new FakeAgent(AgentType.RequirementIntelligence, async (_, _, ct) =>
        {
            await Task.Delay(2000, ct); // long enough to be interrupted
            return AgentResult.Empty("spec");
        }));

        await using var h = await EngineHarness.CreateAsync(agents);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        await h.Engine.TickAsync(id);   // dispatches spec (Running, attempt=1)
        await Task.Delay(100);          // let the executor enter the slow agent
        await h.Service.PauseAsync(id); // safe stop cancels the in-flight execution
        await Task.Delay(200);          // let the executor observe cancellation and revert

        Assert.Equal(WorkflowStatus.Paused, await h.StatusAsync(id));
        var spec = await h.NodeAsync(id, "spec");
        Assert.Equal(NodeStatus.Pending, spec.Status);
        Assert.Equal(0, spec.Attempt); // the interrupted attempt was not consumed

        // Resume: spec re-runs from Pending and the workflow proceeds to completion.
        await h.Service.ResumeAsync(id);
        var status = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowStatus.Completed, status);
    }
}
