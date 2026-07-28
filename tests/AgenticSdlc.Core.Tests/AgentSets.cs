using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Orchestration;

namespace AgenticSdlc.Core.Tests;

/// <summary>Reusable fake agent sets that produce governance-valid artifacts.</summary>
public static class AgentSets
{
    public const string PassingValidation =
        "{\"buildSucceeded\":true,\"testsTotal\":3,\"testsPassed\":3,\"testsFailed\":0,\"overall\":\"pass\"}";
    public const string FailingValidation =
        "{\"buildSucceeded\":false,\"buildErrors\":[\"CS1002\"],\"testsTotal\":0,\"testsPassed\":0,\"testsFailed\":0,\"overall\":\"fail\"}";

    public static FakeAgent Content(AgentType type, ArtifactType artifact, string content) =>
        new(type, (_, _, _) => Task.FromResult(new AgentResult(
            new[] { new ArtifactDraft(artifact, type.ToString(), content, null, Array.Empty<string>()) },
            Array.Empty<DecisionDraft>(), Array.Empty<RiskDraft>(), Array.Empty<RequirementDraft>(),
            Array.Empty<ProposedTask>(), $"{type} done")));

    /// <summary>Full set for governance tests: valid spec, one generation task, and a passing validation report.</summary>
    public static List<IAgent> Governed(
        Func<AgentTaskInput, WorkflowContext, CancellationToken, Task<AgentResult>>? spec = null,
        string validationContent = PassingValidation)
    {
        var specAgent = spec is not null
            ? new FakeAgent(AgentType.RequirementIntelligence, spec)
            : Content(AgentType.RequirementIntelligence, ArtifactType.EngineeringSpecification, "{}");

        var plan = new FakeAgent(AgentType.Planning, (_, _, _) => Task.FromResult(new AgentResult(
            new[] { new ArtifactDraft(ArtifactType.WorkPlan, "plan", "{}", null, Array.Empty<string>()) },
            Array.Empty<DecisionDraft>(), Array.Empty<RiskDraft>(), Array.Empty<RequirementDraft>(),
            new[] { new ProposedTask("impl", "Implement", "build it", AgentType.Generation, WorkflowPhase.Generation, Array.Empty<string>(), Array.Empty<string>()) },
            "planned")));

        return new List<IAgent>
        {
            specAgent,
            plan,
            Content(AgentType.Architecture, ArtifactType.AdrSet, "{}"),
            FakeAgent.Ok(AgentType.Brownfield, ArtifactType.BrownfieldReport),
            FakeAgent.Ok(AgentType.RiskAssessment, ArtifactType.RiskReport),
            FakeAgent.Ok(AgentType.Generation, ArtifactType.SourceCode),
            Content(AgentType.Validation, ArtifactType.ValidationReport, validationContent),
        };
    }
}
