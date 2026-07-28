namespace AgenticSdlc.Core.Agents.Contracts;

/// <summary>Structured output of the Engineering Planning agent (FR-2) — an executable plan.</summary>
public sealed record PlanOutput
{
    public List<PlanMilestone> Milestones { get; init; } = new();
    public List<PlanTask> Tasks { get; init; } = new();
    public List<string> SyncPoints { get; init; } = new();
    public List<string> CriticalPath { get; init; } = new();
}

public sealed record PlanMilestone(string Id, string Name, string ExitCriteria);

public sealed record PlanTask(
    string Id,
    string Name,
    string Description,
    string Agent,
    string Phase,
    List<string> DependsOn,
    bool Parallelizable,
    double EstimateHours,
    List<string> RequirementIds);
