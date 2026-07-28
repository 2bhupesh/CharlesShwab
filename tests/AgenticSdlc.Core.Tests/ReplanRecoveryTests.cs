using AgenticSdlc.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-7 verification: dynamic re-planning invalidates downstream while preserving lineage and
/// governance history; compensating rollback re-runs a node; and restart recovery resumes a workflow
/// stranded by a crash.
/// </summary>
public class ReplanRecoveryTests
{
    [Fact]
    public async Task Replan_from_node_stales_downstream_and_reruns_to_completion()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);
        var status = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowStatus.Completed, status);

        var archNode = await h.NodeAsync(id, "arch");

        // Re-plan from architecture: it and everything downstream must re-run.
        await h.Service.ReplanFromNodeAsync(id, archNode.Id, "revised architecture");

        await using (var ctx = h.Db.NewContext())
        {
            var arch = await ctx.Nodes.FirstAsync(n => n.Id == archNode.Id);
            Assert.Equal(NodeStatus.Pending, arch.Status);
            // Downstream 'validate' was invalidated back to Pending too.
            var validate = await ctx.Nodes.FirstAsync(n => n.WorkflowId == id && n.Key == "validate");
            Assert.Equal(NodeStatus.Pending, validate.Status);
            // The prior AdrSet is retained as Superseded (lineage), not deleted.
            Assert.Contains(await ctx.Artifacts.ToListAsync(),
                a => a.ProducedByNodeId == archNode.Id && a.Status == ArtifactStatus.Superseded);
            // The workflow was reactivated.
            Assert.NotEqual(WorkflowStatus.Completed, (await ctx.Workflows.FirstAsync(w => w.Id == id)).Status);
        }

        // It converges again with fresh approvals.
        var final = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowStatus.Completed, final);
    }

    [Fact]
    public async Task Replan_voids_approvals_but_never_deletes_them()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);
        await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(30));

        int approvalsBefore;
        await using (var ctx = h.Db.NewContext())
            approvalsBefore = await ctx.Approvals.CountAsync(a => a.WorkflowId == id);

        var planNode = await h.NodeAsync(id, "plan");
        await h.Service.ReplanFromNodeAsync(id, planNode.Id, "revise the plan");

        await using (var ctx = h.Db.NewContext())
        {
            // No approval rows were removed — the count only grows.
            Assert.True(await ctx.Approvals.CountAsync(a => a.WorkflowId == id) >= approvalsBefore);
            // The plan's prior approval is Voided (history retained), not gone.
            Assert.Contains(await ctx.Approvals.ToListAsync(),
                a => a.NodeId == planNode.Id && a.Status == ApprovalStatus.Voided);
        }
    }

    [Fact]
    public async Task Rollback_reruns_a_node_and_supersedes_its_artifact()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);
        await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(30));

        var specNode = await h.NodeAsync(id, "spec");
        await h.Service.RollbackNodeAsync(id, specNode.Id, "spec was wrong");

        await using var ctx = h.Db.NewContext();
        Assert.Equal(NodeStatus.Pending, (await ctx.Nodes.FirstAsync(n => n.Id == specNode.Id)).Status);
        Assert.Contains(await ctx.Artifacts.ToListAsync(),
            a => a.ProducedByNodeId == specNode.Id && a.Status == ArtifactStatus.Superseded);
        // The rollback is recorded distinctly in the audit trail.
        Assert.Contains(await ctx.AuditEvents.ToListAsync(),
            e => e.EventType == AuditEventType.RollbackTriggered);
    }

    [Fact]
    public async Task Restart_recovery_resumes_a_stranded_workflow()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        // Simulate a crash: a node was left Running when the process died.
        await using (var ctx = h.Db.NewContext())
        {
            var spec = await ctx.Nodes.FirstAsync(n => n.WorkflowId == id && n.Key == "spec");
            spec.Status = NodeStatus.Running;
            spec.StartedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        // A normal tick cannot unstick a Running node with no live executor.
        await h.Engine.TickAsync(id);
        Assert.Equal(NodeStatus.Running, (await h.NodeAsync(id, "spec")).Status);

        // The restart recovery pass resets it and the workflow resumes to completion.
        await h.Engine.RecoverAsync();
        Assert.Equal(NodeStatus.Pending, (await h.NodeAsync(id, "spec")).Status);

        var status = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowStatus.Completed, status);

        await using var ctx2 = h.Db.NewContext();
        Assert.Contains(await ctx2.AuditEvents.ToListAsync(),
            e => e.EventType == AuditEventType.NodeRecoveredAfterRestart);
    }
}
