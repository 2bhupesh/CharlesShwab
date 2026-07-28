using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Requirement Intelligence agent (FR-1): extracts intent, materializes functional and non-functional
/// requirements, detects ambiguity, and records assumptions and open questions. Its
/// <see cref="RequirementDraft"/>s become the traceability anchors every downstream artifact
/// references.
/// </summary>
public sealed class RequirementIntelligenceAgent : AgentBase<SpecOutput>
{
    public RequirementIntelligenceAgent(ILlmProvider llm, IDbContextFactory<AgenticDbContext> db, CoreOptions options)
        : base(llm, db, options) { }

    public override AgentType Type => AgentType.RequirementIntelligence;

    protected override string SystemPrompt => """
        You are a senior requirements engineer. Read the engineering requirement and produce a
        precise engineering specification. Identify functional requirements, non-functional
        requirements, ambiguities (each with a clarifying question and a severity of "blocking" or
        "minor"), assumptions, and open questions. Assign stable ids: FR-1.., NFR-1.., AS-1.., OQ-1...
        Respond with ONLY a JSON object of this exact shape:
        {
          "intentSummary": "string",
          "functionalRequirements": [{"id":"FR-1","title":"...","description":"...","priority":"High|Medium|Low","sourceExcerpt":"..."}],
          "nonFunctionalRequirements": [{"id":"NFR-1","title":"...","description":"...","priority":"...","sourceExcerpt":"..."}],
          "ambiguities": [{"text":"...","clarifyingQuestion":"...","severity":"blocking|minor"}],
          "assumptions": [{"id":"AS-1","text":"...","rationale":"..."}],
          "openQuestions": [{"id":"OQ-1","text":"..."}]
        }
        """;

    protected override string BuildUserPrompt(AgentTaskInput input, WorkflowContext ctx)
    {
        var prompt = $"Engineering requirement to interpret:\n\n{ctx.RequirementText}\n";
        // On a clarification re-run, the answers were appended to the node instructions.
        if (!string.IsNullOrWhiteSpace(input.TaskInstructionsJson))
            prompt += $"\nAdditional clarification provided by a human reviewer:\n{input.TaskInstructionsJson}\n" +
                      "Incorporate these answers, resolve the previously blocking ambiguities, and produce the specification.";
        return prompt;
    }

    protected override Task<AgentResult> MapOutputAsync(SpecOutput output, AgentTaskInput input, WorkflowContext ctx, CancellationToken ct)
    {
        var requirements = new List<RequirementDraft>();
        foreach (var r in output.FunctionalRequirements)
            requirements.Add(new RequirementDraft(r.Id, RequirementKind.Functional, r.Description, r.Priority, r.SourceExcerpt));
        foreach (var r in output.NonFunctionalRequirements)
            requirements.Add(new RequirementDraft(r.Id, RequirementKind.NonFunctional, r.Description, r.Priority, r.SourceExcerpt));
        foreach (var a in output.Assumptions)
            requirements.Add(new RequirementDraft(a.Id, RequirementKind.Assumption, a.Text, "n/a", a.Rationale));
        foreach (var q in output.OpenQuestions)
            requirements.Add(new RequirementDraft(q.Id, RequirementKind.OpenQuestion, q.Text, "n/a", null));

        var allCodes = output.FunctionalRequirements.Select(r => r.Id)
            .Concat(output.NonFunctionalRequirements.Select(r => r.Id))
            .ToList();

        var specArtifact = new ArtifactDraft(
            ArtifactType.EngineeringSpecification,
            "Engineering Specification",
            JsonSerializer.Serialize(output, JsonExtractor.SerializerOptions),
            ContentPath: null,
            RequirementIds: allCodes);

        var decision = new DecisionDraft(
            "Interpreted engineering intent",
            output.IntentSummary,
            Array.Empty<AlternativeDraft>(),
            allCodes);

        var summary = $"Identified {output.FunctionalRequirements.Count} functional and " +
                      $"{output.NonFunctionalRequirements.Count} non-functional requirements, " +
                      $"{output.Ambiguities.Count(a => a.IsBlocking)} blocking ambiguities.";

        return Task.FromResult(new AgentResult(
            new[] { specArtifact },
            new[] { decision },
            Array.Empty<RiskDraft>(),
            requirements,
            Array.Empty<ProposedTask>(),
            summary));
    }
}
