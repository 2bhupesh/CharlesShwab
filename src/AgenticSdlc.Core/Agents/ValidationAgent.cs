using System.Diagnostics;
using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Workspace;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Validation agent (FR-6), deliberately hybrid (spec §7.4): it shells out to the real dotnet CLI for
/// build and test <em>facts</em>, then makes a single LLM call for conformance <em>judgement</em>.
/// Gate policies evaluate the facts, so progression is never authorized by model opinion alone. Absent
/// a toolchain it degrades to a skipped verdict rather than failing the workflow (FR-25).
/// </summary>
public sealed class ValidationAgent : IAgent
{
    private readonly ILlmProvider _llm;
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly CoreOptions _options;
    private readonly DotnetCliRunner _cli;
    private readonly WorkspaceManager _workspace;
    private readonly AuditLogger _audit;

    public ValidationAgent(
        ILlmProvider llm,
        IDbContextFactory<AgenticDbContext> dbFactory,
        CoreOptions options,
        DotnetCliRunner cli,
        WorkspaceManager workspace,
        AuditLogger audit)
    {
        _llm = llm;
        _dbFactory = dbFactory;
        _options = options;
        _cli = cli;
        _workspace = workspace;
        _audit = audit;
    }

    public AgentType Type => AgentType.Validation;

    public async Task<AgentResult> ExecuteAsync(AgentTaskInput input, WorkflowContext ctx, CancellationToken ct)
    {
        var root = _workspace.GeneratedRoot(ctx.WorkspacePath);
        var (build, tests, overall, buildErrors) = await RunToolchainAsync(root, ct);
        var conformance = await AssessConformanceAsync(input, ctx, build, tests, ct);

        var result = new ValidationOutput
        {
            BuildSucceeded = build,
            BuildErrors = buildErrors,
            TestsTotal = tests?.Total ?? 0,
            TestsPassed = tests?.Passed ?? 0,
            TestsFailed = tests?.Failed ?? 0,
            ArchitectureConformance = conformance?.ArchitectureConformance,
            ApiConformance = conformance?.ApiConformance,
            DocCoverage = conformance?.DocCoverage,
            Overall = overall,
            Recommendations = conformance?.Recommendations ?? new()
        };

        await _audit.LogAsync(ctx.WorkflowId, input.NodeId, AuditEventType.ValidationRun, "agent:Validation",
            $"Validation {overall}: build={build}, tests {result.TestsPassed}/{result.TestsTotal}.", ct: ct);

        var artifact = new ArtifactDraft(ArtifactType.ValidationReport, "Validation Report",
            JsonSerializer.Serialize(result, JsonExtractor.SerializerOptions), null, Array.Empty<string>());
        var decision = new DecisionDraft("Validation executed",
            $"Build {(build ? "succeeded" : overall == "skipped" ? "skipped" : "failed")}; " +
            $"tests {result.TestsPassed}/{result.TestsTotal} passed.",
            Array.Empty<AlternativeDraft>(), Array.Empty<string>());

        return new AgentResult(
            new[] { artifact }, new[] { decision }, Array.Empty<RiskDraft>(), Array.Empty<RequirementDraft>(),
            Array.Empty<ProposedTask>(), $"Validation {overall}.");
    }

    private async Task<(bool build, TestCounts? tests, string overall, List<string> errors)> RunToolchainAsync(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root))
            return (false, null, "skipped", new());

        var csprojs = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).ToList();
        if (csprojs.Count == 0)
            return (false, null, "skipped", new());

        var testProj = csprojs.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase));
        var buildTargets = csprojs.Where(p => p != testProj).ToList();
        if (buildTargets.Count == 0) buildTargets = csprojs; // only a test project exists

        var buildSucceeded = true;
        var errors = new List<string>();
        foreach (var proj in buildTargets)
        {
            var buildResult = await _cli.RunAsync($"build \"{proj}\" --nologo", root, TimeSpan.FromSeconds(240), ct);
            if (!buildResult.Available)
                return (false, null, "skipped", new() { "dotnet CLI unavailable" });
            if (!buildResult.Succeeded)
            {
                buildSucceeded = false;
                errors.AddRange(ExtractErrors(buildResult.StdOut + buildResult.StdErr));
            }
        }

        TestCounts? counts = null;
        if (buildSucceeded && testProj is not null)
        {
            var resultsDir = Path.Combine(root, "TestResults");
            await _cli.RunAsync(
                $"test \"{testProj}\" --nologo --results-directory \"{resultsDir}\" --logger \"trx;LogFileName=results.trx\"",
                root, TimeSpan.FromSeconds(240), ct);
            counts = DotnetCliRunner.ParseTrx(resultsDir);
        }

        var overall = !buildSucceeded ? "fail"
            : (counts?.Failed ?? 0) > 0 ? "fail"
            : "pass";
        return (buildSucceeded, counts, overall, errors);
    }

    private async Task<ConformanceOutput?> AssessConformanceAsync(AgentTaskInput input, WorkflowContext ctx, bool build, TestCounts? tests, CancellationToken ct)
    {
        var systemPrompt =
            "You are a validation engineer judging whether an implementation conforms to its architecture, " +
            "API contracts, and documentation needs. Respond with ONLY a JSON object: " +
            "{\"architectureConformance\":{\"pass\":true,\"notes\":\"...\"},\"apiConformance\":{\"pass\":true,\"notes\":\"...\"},\"docCoverage\":{\"pass\":true,\"notes\":\"...\"},\"recommendations\":[\"...\"]}";
        var userPrompt =
            $"Build succeeded: {build}. Tests: {tests?.Passed ?? 0}/{tests?.Total ?? 0}.\n\n" +
            $"Architecture and contracts:\n{string.Join("\n", ctx.UpstreamArtifacts.Where(a => a.Type is ArtifactType.AdrSet or ArtifactType.ServiceContracts).Select(a => a.ContentSnippet))}";

        var request = new LlmRequest(systemPrompt, new[] { new LlmMessage("user", userPrompt) },
            _options.Llm.Model, _options.Llm.MaxTokens, 0.2,
            new Dictionary<string, string> { ["agent"] = "Validation", ["scenario"] = ctx.ScenarioKey });

        try
        {
            var sw = Stopwatch.StartNew();
            var response = await _llm.CompleteAsync(request, ct);
            sw.Stop();
            var (ok, output, error) = JsonExtractor.TryParse<ConformanceOutput>(response.Text);
            await LogExecutionAsync(input, systemPrompt, userPrompt, response, ok, error, (int)sw.ElapsedMilliseconds, ct);
            return ok ? output : null;
        }
        catch
        {
            return null; // conformance is advisory; build/test facts already drive the gates
        }
    }

    private async Task LogExecutionAsync(AgentTaskInput input, string system, string user, LlmResponse response, bool ok, string? error, int durationMs, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.AgentExecutions.Add(new AgentExecution
        {
            WorkflowId = input.WorkflowId, NodeId = input.NodeId, AgentType = Type, Attempt = input.Attempt,
            Provider = _llm.Kind, Model = response.Model, SystemPrompt = system, UserPrompt = user,
            RawResponse = response.Text, ParsedOk = ok, ParseError = error,
            InputTokens = response.InputTokens, OutputTokens = response.OutputTokens, DurationMs = durationMs
        });
        await db.SaveChangesAsync(ct);
    }

    private static List<string> ExtractErrors(string output) =>
        output.Split('\n')
            .Where(l => l.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim())
            .Distinct()
            .Take(5)
            .ToList();
}
