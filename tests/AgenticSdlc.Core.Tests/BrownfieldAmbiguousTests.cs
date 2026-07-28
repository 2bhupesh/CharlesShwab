using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Governance;
using AgenticSdlc.Core.Llm;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-12 verification: the ambiguous scenario converges through clarification, and the brownfield
/// scenario seeds an existing codebase, analyzes it before planning, and enhances it while keeping
/// the existing tests green (FR-32/33/34).
/// </summary>
public class BrownfieldAmbiguousTests
{
    [Fact]
    public async Task Ambiguous_requirement_converges_after_clarification()
    {
        await using var h = await EngineHarness.CreateWithRealAgentsAsync();
        var id = await h.Service.CreateAsync("share links", "We need something to share links better.", "ambiguous");
        await h.Service.StartAsync(id);

        // Detects insufficiency and pauses at a clarification gate.
        var s1 = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkflowStatus.AwaitingApproval, s1);
        var clar = (await h.Approvals!.GetPendingAsync(id)).Single(a => a.Kind == ApprovalKind.Clarification);
        Assert.NotNull(clar.QuestionsJson);

        // Answering converges the spec on the re-run.
        await h.Approvals.AnswerClarificationAsync(clar.Id, "alice", new[]
        {
            new ClarificationAnswer("Q1", "Shorten long URLs with click tracking."),
            new ClarificationAnswer("Q2", "Public users; C# minimal API on .NET."),
        });

        // Now it settles at the plan approval gate — spec converged and planning ran.
        var s2 = await h.RunUntilSettledAsync(id, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkflowStatus.AwaitingApproval, s2);

        await using var ctx = h.Db.NewContext();
        Assert.Equal(NodeStatus.Succeeded, (await ctx.Nodes.FirstAsync(n => n.WorkflowId == id && n.Key == "spec")).Status);
        // The converged spec materialized concrete functional requirements.
        Assert.True(await ctx.Requirements.AnyAsync(r => r.WorkflowId == id && r.Code == "FR-1"));
    }

    [Fact]
    public async Task Brownfield_seeds_sample_analyzes_before_planning_and_keeps_tests_green()
    {
        var samplesRoot = Path.Combine(FindRepoDir(), "samples");
        await using var h = await EngineHarness.CreateWithRealAgentsAsync(o => o.Workspace.SamplesRoot = samplesRoot);
        var id = await h.Service.CreateAsync("enhance shortener",
            "Add expiring links (410 Gone) and click analytics (GET /links/{code}/stats) to the existing URL shortener without breaking current contracts or tests.",
            "brownfield");
        await h.Service.StartAsync(id);

        var status = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromMinutes(5));
        Assert.Equal(WorkflowStatus.Completed, status);

        await using var ctx = h.Db.NewContext();
        var wf = await ctx.Workflows.FirstAsync(w => w.Id == id);

        // The seeded existing code was analyzed (brownfield node ran, not skipped) before planning.
        var brownfield = await ctx.Nodes.FirstAsync(n => n.WorkflowId == id && n.Key == "brownfield");
        Assert.Equal(NodeStatus.Succeeded, brownfield.Status);
        Assert.True(await ctx.Artifacts.AnyAsync(a => a.WorkflowId == id && a.Type == ArtifactType.BrownfieldReport));

        // Regression + new tests all pass in the real toolchain.
        var report = await ctx.Artifacts
            .Where(a => a.WorkflowId == id && a.Type == ArtifactType.ValidationReport && a.Status != ArtifactStatus.Superseded)
            .OrderByDescending(a => a.Version).FirstAsync();
        var (ok, v, _) = JsonExtractor.TryParse<ValidationOutput>(report.ContentJson!);
        Assert.True(ok);
        Assert.Equal("pass", v!.Overall);
        Assert.True(v.TestsPassed >= 14, $"expected existing + new tests to pass, got {v.TestsPassed}");

        // The new stats endpoint made it into the enhanced code.
        var program = await File.ReadAllTextAsync(Path.Combine(wf.WorkspacePath, "generated", "src", "UrlShortener.Api", "Program.cs"));
        Assert.Contains("/links/{code}/stats", program);
    }

    private static string FindRepoDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AgenticSdlc.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not locate the repo root (AgenticSdlc.slnx).");
    }
}
