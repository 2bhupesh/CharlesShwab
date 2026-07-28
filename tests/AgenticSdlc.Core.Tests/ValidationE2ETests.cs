using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-6 milestone (M2): the full platform, offline, generates a URL shortener whose code genuinely
/// compiles and passes its tests via the real dotnet toolchain — and that result gates the workflow.
/// This is the proof that validation is real, not simulated (spec §7.4, A-7).
/// </summary>
public class ValidationE2ETests
{
    private const string Requirement =
        "Build a URL shortener web service with POST /links, GET /{code} redirect, URL validation, " +
        "collision-free short codes, storage behind an interface, and xUnit tests. C# minimal API on .NET 10.";

    [Fact]
    public async Task Greenfield_run_produces_code_that_really_builds_and_tests_green()
    {
        await using var h = await EngineHarness.CreateWithRealAgentsAsync();
        var id = await h.Service.CreateAsync("url shortener", Requirement, "greenfield");
        await h.Service.StartAsync(id);

        // Cold build + restore + test can take a while; allow generous headroom.
        var status = await h.RunAutoApprovingAsync(id, "alice", TimeSpan.FromMinutes(5));
        Assert.Equal(WorkflowStatus.Completed, status);

        await using var ctx = h.Db.NewContext();
        var wf = await ctx.Workflows.FirstAsync(w => w.Id == id);

        // The generated project exists on disk.
        var program = Path.Combine(wf.WorkspacePath, "generated", "src", "UrlShortener.Api", "Program.cs");
        Assert.True(File.Exists(program), $"expected generated {program}");

        // The validation report reflects a real, passing build and test run.
        var report = await ctx.Artifacts
            .Where(a => a.WorkflowId == id && a.Type == ArtifactType.ValidationReport && a.Status != ArtifactStatus.Superseded)
            .OrderByDescending(a => a.Version)
            .FirstAsync();
        var (ok, v, _) = JsonExtractor.TryParse<ValidationOutput>(report.ContentJson!);
        Assert.True(ok);
        Assert.Equal("pass", v!.Overall);
        Assert.True(v.BuildSucceeded);
        Assert.True(v.TestsPassed > 0, "expected real tests to have run and passed");
        Assert.Equal(0, v.TestsFailed);
    }
}
