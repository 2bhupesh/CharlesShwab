using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Engineering Planning agent (FR-2): decomposes the specification into a work breakdown with an
/// explicit dependency graph. Its <see cref="ProposedTask"/>s drive graph expansion — the shape of
/// the generation stage is decided here at run time, not fixed by the platform (spec §4.2).
/// </summary>
public sealed class EngineeringPlanningAgent : AgentBase<PlanOutput>
{
    public EngineeringPlanningAgent(ILlmProvider llm, IDbContextFactory<AgenticDbContext> db, CoreOptions options)
        : base(llm, db, options) { }

    public override AgentType Type => AgentType.Planning;

    protected override string SystemPrompt => """
        You are a lead engineer producing an executable delivery plan from an engineering
        specification. Break the work into tasks with explicit dependencies so independent tasks can
        run in parallel. Assign each task an agent (use "Generation" for implementation tasks). Give
        each task a stable lowercase id. Declare dependsOn using those ids. Identify synchronization
        points and the critical path.
        Respond with ONLY a JSON object of this exact shape:
        {
          "milestones": [{"id":"MS-1","name":"...","exitCriteria":"..."}],
          "tasks": [{"id":"domain","name":"...","description":"...","agent":"Generation","phase":"Generation","dependsOn":[],"parallelizable":true,"estimateHours":2,"requirementIds":["FR-1"]}],
          "syncPoints": ["taskId"],
          "criticalPath": ["taskId","taskId"]
        }
        """;

    protected override string BuildUserPrompt(AgentTaskInput input, WorkflowContext ctx) =>
        "Produce an executable engineering plan for the following specification and context.\n\n" +
        RenderContext(ctx);

    protected override AgentResult MapOutput(PlanOutput output, AgentTaskInput input, WorkflowContext ctx)
    {
        var planArtifact = new ArtifactDraft(
            ArtifactType.WorkPlan,
            "Engineering Plan",
            JsonSerializer.Serialize(output, JsonExtractor.SerializerOptions),
            ContentPath: null,
            RequirementIds: output.Tasks.SelectMany(t => t.RequirementIds).Distinct().ToList());

        var followUps = output.Tasks
            .Select(t => new ProposedTask(
                t.Id, t.Name, t.Description,
                ParseAgent(t.Agent),
                ParsePhase(t.Phase),
                t.DependsOn ?? new List<string>(),
                t.RequirementIds ?? new List<string>()))
            .ToList();

        var decision = new DecisionDraft(
            "Sequenced engineering work",
            $"Decomposed into {output.Tasks.Count} tasks with {output.CriticalPath.Count} on the critical path; " +
            $"sync points: {string.Join(", ", output.SyncPoints)}.",
            Array.Empty<AlternativeDraft>(),
            output.Tasks.SelectMany(t => t.RequirementIds).Distinct().ToList());

        return new AgentResult(
            new[] { planArtifact },
            new[] { decision },
            Array.Empty<RiskDraft>(),
            Array.Empty<RequirementDraft>(),
            followUps,
            $"Planned {output.Tasks.Count} tasks across {output.Milestones.Count} milestones.");
    }

    private static AgentType ParseAgent(string value) =>
        Enum.TryParse<AgentType>(value, ignoreCase: true, out var t) ? t : AgentType.Generation;

    private static WorkflowPhase ParsePhase(string value) =>
        Enum.TryParse<WorkflowPhase>(value, ignoreCase: true, out var p) ? p : WorkflowPhase.Generation;
}
