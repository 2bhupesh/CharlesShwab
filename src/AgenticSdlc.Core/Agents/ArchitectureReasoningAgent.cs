using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Architecture Reasoning agent (FR-3): selects an architecture, records ADRs, decomposes components,
/// designs service contracts, and chooses a tech stack. Each ADR and tech choice becomes a queryable
/// <see cref="Decision"/> — the architecture rationale in the review package.
/// </summary>
public sealed class ArchitectureReasoningAgent : AgentBase<ArchOutput>
{
    public ArchitectureReasoningAgent(ILlmProvider llm, IDbContextFactory<AgenticDbContext> db, CoreOptions options)
        : base(llm, db, options) { }

    public override AgentType Type => AgentType.Architecture;

    protected override string SystemPrompt => """
        You are a principal software architect. From the specification and plan, choose a solution
        architecture and justify it. Produce architecture decision records, a component decomposition,
        service contracts (API operations), technology choices, and a mermaid component diagram.
        Respond with ONLY a JSON object of this exact shape:
        {
          "selectedStyle": "string",
          "alternatives": [{"name":"...","pros":["..."],"cons":["..."]}],
          "adrs": [{"id":"ADR-1","title":"...","context":"...","decision":"...","consequences":"...","requirementIds":["FR-1"]}],
          "components": [{"name":"...","responsibility":"...","dependsOn":["..."]}],
          "serviceContracts": [{"operation":"...","method":"POST","path":"/x","requestShape":"...","responseShape":"..."}],
          "techStack": [{"area":"...","choice":"...","rationale":"..."}],
          "componentDiagramMermaid": "graph TD; A-->B"
        }
        """;

    protected override string BuildUserPrompt(AgentTaskInput input, WorkflowContext ctx) =>
        "Design the architecture for the following specification and plan.\n\n" + RenderContext(ctx);

    protected override Task<AgentResult> MapOutputAsync(ArchOutput output, AgentTaskInput input, WorkflowContext ctx, CancellationToken ct)
    {
        var adrCodes = output.Adrs.SelectMany(a => a.RequirementIds).Distinct().ToList();

        var artifacts = new List<ArtifactDraft>
        {
            new(ArtifactType.AdrSet, "Architecture Decision Records",
                JsonSerializer.Serialize(output.Adrs, JsonExtractor.SerializerOptions), null, adrCodes),
            new(ArtifactType.ComponentDiagram, "Component Diagram",
                JsonSerializer.Serialize(new { output.SelectedStyle, output.Components, output.ComponentDiagramMermaid }, JsonExtractor.SerializerOptions), null, adrCodes),
            new(ArtifactType.ServiceContracts, "Service Contracts",
                JsonSerializer.Serialize(output.ServiceContracts, JsonExtractor.SerializerOptions), null, adrCodes),
        };

        var decisions = new List<DecisionDraft>
        {
            new($"Selected architecture: {output.SelectedStyle}",
                $"Chosen over {string.Join(", ", output.Alternatives.Select(a => a.Name))}.",
                output.Alternatives.Select(a => new AlternativeDraft(a.Name, a.Pros, a.Cons)).ToList(),
                adrCodes)
        };
        decisions.AddRange(output.Adrs.Select(a =>
            new DecisionDraft(a.Title, $"{a.Context} — {a.Decision}. Consequences: {a.Consequences}",
                Array.Empty<AlternativeDraft>(), a.RequirementIds)));
        decisions.AddRange(output.TechStack.Select(t =>
            new DecisionDraft($"Technology: {t.Area} = {t.Choice}", t.Rationale, Array.Empty<AlternativeDraft>(), Array.Empty<string>())));

        return Task.FromResult(new AgentResult(
            artifacts, decisions, Array.Empty<RiskDraft>(), Array.Empty<RequirementDraft>(),
            Array.Empty<ProposedTask>(),
            $"Selected {output.SelectedStyle} with {output.Adrs.Count} ADRs and {output.ServiceContracts.Count} service contracts."));
    }
}
