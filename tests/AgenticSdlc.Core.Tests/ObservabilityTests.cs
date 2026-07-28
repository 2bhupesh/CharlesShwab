using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Packaging;
using AgenticSdlc.Core.Workspace;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-8 verification: the audit logger publishes to the event bus, all ten metrics compute on a
/// completed run, the timeline reflects the audit stream, and the review package assembles with every
/// required section.
/// </summary>
public class ObservabilityTests
{
    [Fact]
    public async Task Audit_logger_publishes_to_the_event_bus()
    {
        await using var db = await TestDb.CreateAsync();
        var bus = new WorkflowEventBus();
        var audit = new AuditLogger(db.Factory, bus);
        var wfId = Guid.NewGuid();
        await using (var ctx = db.NewContext())
        {
            ctx.Workflows.Add(new Workflow { Id = wfId });
            await ctx.SaveChangesAsync();
        }

        await audit.LogAsync(wfId, null, AuditEventType.WorkflowStarted, "system", "started");

        Assert.True(bus.Reader.TryRead(out var evt));
        Assert.Equal("WorkflowStarted", evt!.Type);
        Assert.Equal(wfId, evt.WorkflowId);
    }

    [Fact]
    public async Task Metrics_compute_on_a_completed_run()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);
        var status = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowStatus.Completed, status);

        var metrics = new MetricsService(h.Db.Factory);
        var m = await metrics.GetForWorkflowAsync(id);
        Assert.Equal("Completed", m.Status);
        Assert.True(m.NodesSucceeded > 0);
        Assert.Equal(1.0, m.AgentSuccessRate);          // all agent nodes succeeded
        Assert.True(m.ValidationPassRate > 0);           // passing fake validation report
        Assert.True(m.MeanApprovalSeconds is not null);  // human gates were resolved

        var global = await metrics.GetGlobalAsync();
        Assert.Equal(1, global.WorkflowsTotal);
        Assert.Equal(1.0, global.WorkflowSuccessRate);
    }

    [Fact]
    public async Task Timeline_reflects_the_audit_stream_in_order()
    {
        await using var h = await EngineHarness.CreateAsync(AgentSets.Governed(), realGovernance: true);
        var id = await h.Service.CreateAsync("t", "Build a thing.", "greenfield");
        await h.Service.StartAsync(id);
        await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(30));

        var timeline = await new TimelineService(h.Db.Factory).GetAsync(id);
        Assert.NotEmpty(timeline);
        Assert.Equal("WorkflowCreated", timeline[0].EventType);
        // Strictly increasing sequence numbers.
        for (var i = 1; i < timeline.Count; i++)
            Assert.True(timeline[i].Seq > timeline[i - 1].Seq);
        // Node events carry a resolved node key.
        Assert.Contains(timeline, e => e.NodeKey == "spec");
    }

    [Fact]
    public async Task Review_package_assembles_with_all_sections()
    {
        var wsRoot = Path.Combine(Path.GetTempPath(), "rp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var h = await EngineHarness.CreateAsync(
                AgentSets.Governed(), o => o.Workspace.Root = wsRoot, realGovernance: true);
            var id = await h.Service.CreateAsync("url shortener", "Build a URL shortener.", "greenfield");
            await h.Service.StartAsync(id);
            await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromSeconds(30));

            var packageNode = await h.NodeAsync(id, "package");
            var builder = new ReviewPackageBuilder(
                h.Db.Factory, new WorkspaceManager(), new MetricsService(h.Db.Factory), new AuditLogger(h.Db.Factory));
            await builder.BuildAsync(id, packageNode.Id);

            await using var ctx = h.Db.NewContext();
            var artifact = await ctx.Artifacts.FirstAsync(a => a.WorkflowId == id && a.Type == ArtifactType.ReviewPackage);
            var wf = await ctx.Workflows.FirstAsync(w => w.Id == id);
            var md = await File.ReadAllTextAsync(Path.Combine(wf.WorkspacePath, "review-package", "ReviewPackage.md"));

            foreach (var section in new[]
            {
                "Requirement Interpretation", "Engineering Plan", "Architecture Rationale",
                "Generated Artifacts", "Validation Results", "Risk Assessment", "Assumptions",
                "Approval History", "Audit Trail", "Metrics", "Release Readiness"
            })
                Assert.Contains(section, md);
            Assert.NotNull(artifact.ContentJson);
        }
        finally
        {
            try { Directory.Delete(wsRoot, recursive: true); } catch { }
        }
    }
}
