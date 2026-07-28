using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Workspace;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Engineering Generation agent (FR-5): produces real files on disk under the workspace. Each
/// generation node is scoped to one plan task, so the files it emits differ by task — the output is
/// genuinely decomposed, not one monolithic blob. Artifacts reference the files by path so the
/// dashboard can browse the actually-generated code.
/// </summary>
public sealed class EngineeringGenerationAgent : AgentBase<GenOutput>
{
    private readonly WorkspaceManager _workspace;

    public EngineeringGenerationAgent(ILlmProvider llm, IDbContextFactory<AgenticDbContext> db, CoreOptions options, WorkspaceManager workspace)
        : base(llm, db, options) => _workspace = workspace;

    public override AgentType Type => AgentType.Generation;

    protected override string SystemPrompt => """
        You are a senior software engineer implementing one task of an engineering plan. Produce the
        source files, tests, and any docs needed for THIS task only, as complete file contents. Use
        relative paths within a 'generated' project layout. The code must compile.
        Respond with ONLY a JSON object of this exact shape:
        {
          "files": [{"path":"src/Project/File.cs","kind":"source|test|project|openapi|dbscript|iac|doc|releaseNotes","content":"<full file content>"}],
          "buildNotes": "string",
          "requirementIds": ["FR-1"]
        }
        """;

    protected override string BuildUserPrompt(AgentTaskInput input, WorkflowContext ctx)
    {
        var task = input.TaskInstructionsJson is null ? input.TaskName : input.TaskInstructionsJson;
        return $"Implement this task:\n{task}\n\nArchitecture and contracts for context:\n{RenderContext(ctx)}";
    }

    protected override async Task<AgentResult> MapOutputAsync(GenOutput output, AgentTaskInput input, WorkflowContext ctx, CancellationToken ct)
    {
        var generatedRoot = _workspace.GeneratedRoot(ctx.WorkspacePath);
        await _workspace.WriteFilesAsync(generatedRoot, output.Files.Select(f => (f.Path, f.Content)), ct);

        var artifacts = output.Files.Select(f => new ArtifactDraft(
            MapKind(f.Kind),
            Path.GetFileName(f.Path),
            ContentJson: null,
            ContentPath: "generated/" + f.Path.Replace('\\', '/'),
            output.RequirementIds)).ToList();

        return new AgentResult(
            artifacts, Array.Empty<DecisionDraft>(), Array.Empty<RiskDraft>(), Array.Empty<RequirementDraft>(),
            Array.Empty<ProposedTask>(),
            $"Generated {output.Files.Count} file(s). {output.BuildNotes}");
    }

    private static ArtifactType MapKind(string kind) => kind.ToLowerInvariant() switch
    {
        "test" => ArtifactType.TestSuite,
        "openapi" => ArtifactType.OpenApiSpec,
        "dbscript" => ArtifactType.DbScript,
        "iac" => ArtifactType.InfrastructureAsCode,
        "doc" => ArtifactType.DocSet,
        "releasenotes" => ArtifactType.ReleaseNotes,
        _ => ArtifactType.SourceCode
    };
}
