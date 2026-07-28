using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Orchestration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-5 verification: gates genuinely enforce — human approval pauses execution, unrelated branches
/// keep running, reject-with-changes re-runs and invalidates downstream, policies block progression,
/// and the clarification loop converges an ambiguous requirement.
/// </summary>
public class GovernanceTests
{
    [Fact]
    public async Task Human_gate_pauses_until_approved()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        // Runs until the plan's human exit gate, then stops — nothing downstream may proceed.
        var status = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowStatus.AwaitingApproval, status);
        var pending = await h.Approvals!.GetPendingAsync(id);
        Assert.NotEmpty(pending);
        Assert.Equal(NodeStatus.Pending, (await h.NodeAsync(id, "arch")).Status); // downstream blocked

        // Approving each gate as it appears drives the workflow to completion.
        var final = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowStatus.Completed, final);

        await using var ctx = h.Db.NewContext();
        var granted = await ctx.Approvals.CountAsync(a => a.WorkflowId == id
            && a.GateType == GateType.HumanApproval && a.Status == ApprovalStatus.Approved);
        Assert.True(granted >= 3, $"expected plan+arch+package approvals, got {granted}"); // FR-16/17
    }

    [Fact]
    public async Task Unrelated_branch_runs_while_a_node_awaits_approval()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        // Approve only the plan gate, then settle at the architecture gate.
        await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(10));
        foreach (var a in await h.Approvals!.GetPendingAsync(id))
            await h.Approvals.ApproveAsync(a.Id, true, "alice", null, false);
        var status = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowStatus.AwaitingApproval, status);
        Assert.Equal(NodeStatus.AwaitingApproval, (await h.NodeAsync(id, "arch")).Status);
        // 'risk' depends on plan (hard) and arch (soft), so it runs while arch awaits approval.
        Assert.Equal(NodeStatus.Succeeded, (await h.NodeAsync(id, "risk")).Status);
    }

    [Fact]
    public async Task Reject_with_changes_reruns_node_and_supersedes_its_artifact()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);
        await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(10));

        var planApproval = (await h.Approvals!.GetPendingAsync(id)).Single();
        await h.Approvals.ApproveAsync(planApproval.Id, approved: false, "alice", "tighten the plan", requestChanges: true);

        // The plan node re-runs and raises a fresh approval; its first plan artifact is superseded.
        await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(10));
        await using var ctx = h.Db.NewContext();
        var planNode = await ctx.Nodes.FirstAsync(n => n.WorkflowId == id && n.Key == "plan");
        var planArtifacts = await ctx.Artifacts
            .Where(a => a.WorkflowId == id && a.ProducedByNodeId == planNode.Id && a.Type == ArtifactType.WorkPlan)
            .ToListAsync();
        Assert.Equal(2, planArtifacts.Count); // v1 superseded, v2 fresh
        Assert.Contains(planArtifacts, a => a.Status == ArtifactStatus.Superseded);
        // The rejected approval is voided (audit log preserves the rejection), and a new one is pending.
        Assert.Contains(await h.Approvals.GetPendingAsync(id), a => a.NodeId == planNode.Id);
    }

    [Fact]
    public async Task Failing_build_policy_blocks_release()
    {
        await using var h = await EngineHarness.CreateAsync(
            AgentSets.Governed(validationContent: AgentSets.FailingValidation),
            o => { o.Orchestration.MaxAttempts = 1; o.Orchestration.RetryBaseDelaySeconds = 1; },
            realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);

        var status = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowStatus.Failed, status);
        Assert.Equal(NodeStatus.Failed, (await h.NodeAsync(id, "validate")).Status);
    }

    [Fact]
    public async Task Clarification_loop_converges_an_ambiguous_requirement()
    {
        const string blockingSpec = """
            {"intentSummary":"unclear","functionalRequirements":[],"nonFunctionalRequirements":[],
             "ambiguities":[{"text":"scope is unclear","clarifyingQuestion":"What should it do?","severity":"blocking"}],
             "assumptions":[],"openQuestions":[]}
            """;
        var calls = 0;
        // First run emits a blocking ambiguity; after clarification, the re-run emits a clean spec.
        Task<AgentResult> Spec(AgentTaskInput i, WorkflowContext c, CancellationToken ct)
        {
            var content = Interlocked.Increment(ref calls) == 1 ? blockingSpec : "{}";
            return Task.FromResult(new AgentResult(
                new[] { new ArtifactDraft(ArtifactType.EngineeringSpecification, "spec", content, null, Array.Empty<string>()) },
                Array.Empty<DecisionDraft>(), Array.Empty<RiskDraft>(), Array.Empty<RequirementDraft>(),
                Array.Empty<ProposedTask>(), "spec"));
        }

        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(Spec), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "make link sharing better", "ambiguous");
        await h.Service.StartAsync(id);

        var status = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowStatus.AwaitingApproval, status);
        var clarification = (await h.Approvals!.GetPendingAsync(id)).Single(a => a.Kind == ApprovalKind.Clarification);
        Assert.NotNull(clarification.QuestionsJson);

        await h.Approvals.AnswerClarificationAsync(clarification.Id, "alice",
            new[] { new Governance.ClarificationAnswer("Q1", "It should shorten and share links.") });

        // Spec re-runs clean, the workflow proceeds, and the Q&A trail is recorded.
        var final = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowStatus.Completed, final);
        await using var ctx = h.Db.NewContext();
        Assert.True(await ctx.Artifacts.AnyAsync(a => a.WorkflowId == id && a.Type == ArtifactType.ClarificationAnswers));
    }
}
