using AgenticSdlc.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-1 verification: every entity round-trips through SQLite, enums persist as readable strings,
/// the (WorkflowId, Key) uniqueness holds, and parallel contexts from the factory can write
/// concurrently (the property NFR-7 depends on).
/// </summary>
public class DomainPersistenceTests
{
    [Fact]
    public async Task Workflow_and_graph_round_trip()
    {
        await using var db = await TestDb.CreateAsync();
        var wf = new Workflow { Name = "demo", RequirementText = "build X", ScenarioKey = "greenfield" };
        var spec = new WorkflowNode { WorkflowId = wf.Id, Key = "spec", Name = "Spec", AgentType = AgentType.RequirementIntelligence, Phase = WorkflowPhase.Intake };
        var plan = new WorkflowNode { WorkflowId = wf.Id, Key = "plan", Name = "Plan", AgentType = AgentType.Planning, Phase = WorkflowPhase.Planning };
        var edge = new DependencyEdge { WorkflowId = wf.Id, FromNodeId = spec.Id, ToNodeId = plan.Id, Kind = EdgeKind.Hard };

        await using (var ctx = db.NewContext())
        {
            ctx.Workflows.Add(wf);
            ctx.Nodes.AddRange(spec, plan);
            ctx.Edges.Add(edge);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.NewContext())
        {
            var loaded = await ctx.Workflows.SingleAsync();
            Assert.Equal("demo", loaded.Name);
            Assert.Equal(2, await ctx.Nodes.CountAsync());
            Assert.Equal(EdgeKind.Hard, (await ctx.Edges.SingleAsync()).Kind);
        }
    }

    [Fact]
    public async Task Enums_persist_as_strings()
    {
        await using var db = await TestDb.CreateAsync();
        var wf = new Workflow { Status = WorkflowStatus.AwaitingApproval };
        await using (var ctx = db.NewContext())
        {
            ctx.Workflows.Add(wf);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.NewContext())
        {
            // Read the raw column value (only one row exists): it must be the enum name, not the ordinal.
            var raw = await ctx.Database
                .SqlQueryRaw<string>("SELECT Status AS Value FROM Workflows")
                .SingleAsync();
            Assert.Equal("AwaitingApproval", raw);
        }
    }

    [Fact]
    public async Task All_entities_round_trip()
    {
        await using var db = await TestDb.CreateAsync();
        var wfId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        await using (var ctx = db.NewContext())
        {
            ctx.Workflows.Add(new Workflow { Id = wfId, Name = "w" });
            ctx.Nodes.Add(new WorkflowNode { Id = nodeId, WorkflowId = wfId, Key = "spec" });
            ctx.Requirements.Add(new RequirementItem { WorkflowId = wfId, Code = "FR-1", Kind = RequirementKind.Functional, Text = "t" });
            ctx.Artifacts.Add(new Artifact { WorkflowId = wfId, ProducedByNodeId = nodeId, Type = ArtifactType.EngineeringSpecification, Name = "spec" });
            ctx.Decisions.Add(new Decision { WorkflowId = wfId, NodeId = nodeId, AgentType = AgentType.Architecture, Title = "d" });
            ctx.Approvals.Add(new Approval { WorkflowId = wfId, NodeId = nodeId, Stage = GateStage.Exit, GateType = GateType.HumanApproval, Title = "a" });
            ctx.AuditEvents.Add(new AuditEvent { WorkflowId = wfId, Seq = 1, EventType = AuditEventType.WorkflowCreated, Summary = "created" });
            ctx.AgentExecutions.Add(new AgentExecution { WorkflowId = wfId, NodeId = nodeId, AgentType = AgentType.Planning, Provider = LlmProviderKind.Mock });
            ctx.Risks.Add(new RiskItem { WorkflowId = wfId, NodeId = nodeId, Category = RiskCategory.Security, Severity = RiskLevel.High, Likelihood = RiskLevel.Medium, Title = "r" });
            ctx.MetricSnapshots.Add(new MetricSnapshot { WorkflowId = wfId, MetricsJson = "{\"x\":1}" });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.NewContext())
        {
            Assert.Equal(1, await ctx.Requirements.CountAsync());
            Assert.Equal(1, await ctx.Artifacts.CountAsync());
            Assert.Equal(1, await ctx.Decisions.CountAsync());
            Assert.Equal(1, await ctx.Approvals.CountAsync());
            Assert.Equal(1, await ctx.AuditEvents.CountAsync());
            Assert.Equal(1, await ctx.AgentExecutions.CountAsync());
            Assert.Equal(1, await ctx.Risks.CountAsync());
            Assert.Equal(1, await ctx.MetricSnapshots.CountAsync());
        }
    }

    [Fact]
    public async Task Node_key_is_unique_per_workflow()
    {
        await using var db = await TestDb.CreateAsync();
        var wfId = Guid.NewGuid();
        await using (var ctx = db.NewContext())
        {
            ctx.Workflows.Add(new Workflow { Id = wfId });
            ctx.Nodes.Add(new WorkflowNode { WorkflowId = wfId, Key = "spec" });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = db.NewContext())
        {
            ctx.Nodes.Add(new WorkflowNode { WorkflowId = wfId, Key = "spec" });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Parallel_contexts_write_concurrently()
    {
        await using var db = await TestDb.CreateAsync();
        var wfId = Guid.NewGuid();
        await using (var ctx = db.NewContext())
        {
            ctx.Workflows.Add(new Workflow { Id = wfId });
            await ctx.SaveChangesAsync();
        }

        // Simulate parallel node executors: each opens its own short-lived context and writes.
        var writes = Enumerable.Range(0, 12).Select(async i =>
        {
            await using var ctx = db.NewContext();
            ctx.AuditEvents.Add(new AuditEvent { WorkflowId = wfId, Seq = i, EventType = AuditEventType.NodeStarted, Summary = $"n{i}" });
            await ctx.SaveChangesAsync();
        });
        await Task.WhenAll(writes);

        await using (var ctx = db.NewContext())
        {
            Assert.Equal(12, await ctx.AuditEvents.CountAsync());
        }
    }
}
