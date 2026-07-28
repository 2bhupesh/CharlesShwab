using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Workspace;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Brownfield Reasoning agent (FR-4): understands an existing codebase (scanned from the seeded
/// workspace) and produces impact analysis, dependency findings, refactoring recommendations, and
/// risks — before planning commits to an approach.
/// </summary>
public sealed class BrownfieldReasoningAgent : AgentBase<BrownfieldOutput>
{
    private readonly WorkspaceManager _workspace;

    public BrownfieldReasoningAgent(ILlmProvider llm, IDbContextFactory<AgenticDbContext> db, CoreOptions options, WorkspaceManager workspace)
        : base(llm, db, options) => _workspace = workspace;

    public override AgentType Type => AgentType.Brownfield;

    protected override string SystemPrompt => """
        You are a staff engineer performing brownfield analysis of an existing codebase. Given the
        repository digest and the requested enhancement, produce a change-impact assessment,
        dependency findings, refactoring recommendations, and risks.
        Respond with ONLY a JSON object of this exact shape:
        {
          "repoSummary": "string",
          "modules": [{"path":"...","purpose":"..."}],
          "dependencyFindings": ["..."],
          "impactAnalysis": [{"proposedChange":"...","affectedModules":["..."],"riskLevel":"Low|Medium|High"}],
          "refactorings": [{"title":"...","priority":"High|Medium|Low","rationale":"..."}],
          "risks": [{"id":"R-1","category":"Technical","severity":"Medium","likelihood":"Medium","title":"...","description":"...","mitigation":"...","requirementIds":[]}]
        }
        """;

    protected override string BuildUserPrompt(AgentTaskInput input, WorkflowContext ctx)
    {
        var digest = _workspace.ScanRepo(_workspace.GeneratedRoot(ctx.WorkspacePath));
        return $"Requested enhancement:\n{ctx.RequirementText}\n\nExisting codebase:\n{digest}";
    }

    protected override Task<AgentResult> MapOutputAsync(BrownfieldOutput output, AgentTaskInput input, WorkflowContext ctx, CancellationToken ct)
    {
        var report = new ArtifactDraft(ArtifactType.BrownfieldReport, "Brownfield Impact Assessment",
            JsonSerializer.Serialize(output, JsonExtractor.SerializerOptions), null, Array.Empty<string>());

        var risks = output.Risks.Select(r => new RiskDraft(
            ParseEnum(r.Category, RiskCategory.Technical),
            ParseEnum(r.Severity, RiskLevel.Medium),
            ParseEnum(r.Likelihood, RiskLevel.Medium),
            r.Title, r.Description, r.Mitigation, r.RequirementIds)).ToList();

        var decision = new DecisionDraft(
            "Brownfield impact assessed",
            $"{output.ImpactAnalysis.Count} impacted area(s); {output.Refactorings.Count} refactoring(s) recommended.",
            Array.Empty<AlternativeDraft>(), Array.Empty<string>());

        return Task.FromResult(new AgentResult(
            new[] { report }, new[] { decision }, risks, Array.Empty<RequirementDraft>(),
            Array.Empty<ProposedTask>(),
            $"Analyzed existing codebase: {output.Modules.Count} module(s), {output.ImpactAnalysis.Count} impact(s)."));
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var v) ? v : fallback;
}
