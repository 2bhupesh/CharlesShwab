using AgenticSdlc.Core;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm.Mock;
using AgenticSdlc.Core.Orchestration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-3 verification: the agent base reparse loop recovers from malformed JSON and records every
/// call as prompt lineage; the spec and planning agents map their output into the right drafts.
/// </summary>
public class AgentTests
{
    private static AgentTaskInput Input(Guid wfId, Guid nodeId, string? instructions = null) =>
        new(wfId, nodeId, "spec", "Interpret requirement", instructions, Attempt: 1);

    private static WorkflowContext GreenfieldContext(Guid wfId) =>
        WorkflowContext.Empty(wfId, "Build a URL shortener web service.", "greenfield", "workspace");

    [Fact]
    public async Task Reparse_loop_recovers_and_logs_each_call()
    {
        await using var db = await TestDb.CreateAsync();
        var wfId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        await Seed(db, wfId, nodeId);

        // First response is malformed prose; second is a valid (minimal) spec.
        const string valid = """{"intentSummary":"ok","functionalRequirements":[{"id":"FR-1","title":"t","description":"d","priority":"High","sourceExcerpt":"x"}],"nonFunctionalRequirements":[],"ambiguities":[],"assumptions":[],"openQuestions":[]}""";
        var scripted = new ScriptedLlmProvider("not json at all", valid);
        var agent = new RequirementIntelligenceAgent(scripted, db.Factory, new CoreOptions());

        var result = await agent.ExecuteAsync(Input(wfId, nodeId), GreenfieldContext(wfId), default);

        Assert.Equal(2, scripted.CallCount);
        Assert.Single(result.Requirements);

        await using var ctx = db.NewContext();
        var logged = await ctx.AgentExecutions.Where(e => e.NodeId == nodeId).ToListAsync();
        Assert.Equal(2, logged.Count);                       // one row per call, including the reparse
        Assert.False(logged[0].ParsedOk);
        Assert.True(logged[1].ParsedOk);
    }

    [Fact]
    public async Task Requirement_agent_materializes_all_requirement_kinds()
    {
        await using var db = await TestDb.CreateAsync();
        var wfId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        await Seed(db, wfId, nodeId);

        var provider = new MockLlmProvider(new MockResponseCatalog());
        var agent = new RequirementIntelligenceAgent(provider, db.Factory, new CoreOptions());

        var result = await agent.ExecuteAsync(Input(wfId, nodeId), GreenfieldContext(wfId), default);

        Assert.Contains(result.Requirements, r => r.Code == "FR-1" && r.Kind == RequirementKind.Functional);
        Assert.Contains(result.Requirements, r => r.Kind == RequirementKind.NonFunctional);
        Assert.Contains(result.Requirements, r => r.Kind == RequirementKind.Assumption);
        Assert.Single(result.Artifacts, a => a.Type == ArtifactType.EngineeringSpecification);
        Assert.NotEmpty(result.Decisions);
    }

    [Fact]
    public async Task Planning_agent_emits_dependency_wired_generation_tasks()
    {
        await using var db = await TestDb.CreateAsync();
        var wfId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        await Seed(db, wfId, nodeId);

        var provider = new MockLlmProvider(new MockResponseCatalog());
        var agent = new EngineeringPlanningAgent(provider, db.Factory, new CoreOptions());

        var ctx = GreenfieldContext(wfId);
        var result = await agent.ExecuteAsync(new AgentTaskInput(wfId, nodeId, "plan", "Plan", null, 1), ctx, default);

        Assert.Single(result.Artifacts, a => a.Type == ArtifactType.WorkPlan);
        Assert.All(result.FollowUpTasks, t => Assert.Equal(AgentType.Generation, t.Agent));
        // The 'api' task depends on 'domain' — the dependency graph the engine will expand from.
        var api = Assert.Single(result.FollowUpTasks, t => t.Id == "api");
        Assert.Contains("domain", api.DependsOn);
    }

    private static async Task Seed(TestDb db, Guid wfId, Guid nodeId)
    {
        await using var ctx = db.NewContext();
        ctx.Workflows.Add(new Workflow { Id = wfId, ScenarioKey = "greenfield" });
        ctx.Nodes.Add(new WorkflowNode { Id = nodeId, WorkflowId = wfId, Key = "spec" });
        await ctx.SaveChangesAsync();
    }
}
