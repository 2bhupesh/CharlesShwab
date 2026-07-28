using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Risk Assessment agent (FR-7): identifies technical, security, and operational risks with
/// mitigations, linked to requirements. Produces <see cref="RiskDraft"/>s that become the Risks tab
/// and feed the review package's risk register.
/// </summary>
public sealed class RiskAssessmentAgent : AgentBase<RiskOutput>
{
    public RiskAssessmentAgent(ILlmProvider llm, IDbContextFactory<AgenticDbContext> db, CoreOptions options)
        : base(llm, db, options) { }

    public override AgentType Type => AgentType.RiskAssessment;

    protected override string SystemPrompt => """
        You are a risk engineer. Analyze the specification, plan, and architecture for technical,
        security, operational, performance, and reliability risks. For each, give a severity and
        likelihood (Low|Medium|High|Critical) and a concrete mitigation.
        Respond with ONLY a JSON object of this exact shape:
        {
          "risks": [{"id":"R-1","category":"Technical|Security|Operational|Performance|Reliability|Architectural","severity":"Low|Medium|High|Critical","likelihood":"Low|Medium|High|Critical","title":"...","description":"...","mitigation":"...","requirementIds":["FR-1"]}]
        }
        """;

    protected override string BuildUserPrompt(AgentTaskInput input, WorkflowContext ctx) =>
        "Assess engineering risks for the following context.\n\n" + RenderContext(ctx);

    protected override Task<AgentResult> MapOutputAsync(RiskOutput output, AgentTaskInput input, WorkflowContext ctx, CancellationToken ct)
    {
        var risks = output.Risks.Select(r => new RiskDraft(
            ParseEnum(r.Category, RiskCategory.Technical),
            ParseEnum(r.Severity, RiskLevel.Medium),
            ParseEnum(r.Likelihood, RiskLevel.Medium),
            r.Title, r.Description, r.Mitigation, r.RequirementIds)).ToList();

        var artifact = new ArtifactDraft(ArtifactType.RiskReport, "Risk Report",
            JsonSerializer.Serialize(output, JsonExtractor.SerializerOptions), null,
            output.Risks.SelectMany(r => r.RequirementIds).Distinct().ToList());

        return Task.FromResult(new AgentResult(
            new[] { artifact }, Array.Empty<DecisionDraft>(), risks, Array.Empty<RequirementDraft>(),
            Array.Empty<ProposedTask>(), $"Identified {risks.Count} risk(s)."));
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var v) ? v : fallback;
}
