namespace AgenticSdlc.Core.Domain;

/// <summary>
/// A unit of work in the dependency graph, executed by one agent (or a system Join/Packaging role).
/// The state machine is documented in spec §4.1. <c>(WorkflowId, Key)</c> is unique, which makes
/// graph expansion idempotent.
/// </summary>
public class WorkflowNode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    /// <summary>Stable identifier within the workflow (e.g. <c>spec</c>, <c>arch</c>, <c>gen.api</c>).</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public AgentType AgentType { get; set; }

    public WorkflowPhase Phase { get; set; }

    public NodeStatus Status { get; set; } = NodeStatus.Pending;

    /// <summary>Retry accounting (spec §6). <see cref="Attempt"/> counts executions consumed.</summary>
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;

    /// <summary>When a retry becomes eligible for dispatch; null when not scheduled.</summary>
    public DateTimeOffset? NextRetryAt { get; set; }

    /// <summary>Per-node execution bound, enforced via a linked cancellation token.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Failure-isolation flag: when true, this node failing does not fail the workflow, and its
    /// dependents treat it like <see cref="NodeStatus.Skipped"/> across Soft edges.
    /// </summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>
    /// Node-specific scoping for the agent (JSON). Also carries reviewer feedback appended on a
    /// reject-with-changes re-run, and clarification answers on the spec node.
    /// </summary>
    public string? TaskInstructionsJson { get; set; }

    /// <summary>Serialized <c>List&lt;GateDefinition&gt;</c> attached to this node.</summary>
    public string GatesJson { get; set; } = "[]";

    public string? ErrorMessage { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
