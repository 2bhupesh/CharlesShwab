namespace AgenticSdlc.Core.Domain;

/// <summary>Lifecycle status of an entire workflow.</summary>
public enum WorkflowStatus
{
    Draft,
    Running,
    Paused,
    AwaitingApproval,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Execution state of a single node in the dependency graph (spec §4.1).</summary>
public enum NodeStatus
{
    Pending,
    Ready,
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    Stale
}

/// <summary>
/// Which specialized agent (or system role) executes a node. <see cref="Join"/> and
/// <see cref="Packaging"/> are handled by the engine itself, not by an <c>IAgent</c>.
/// </summary>
public enum AgentType
{
    RequirementIntelligence,
    Planning,
    Architecture,
    Brownfield,
    Generation,
    Validation,
    RiskAssessment,
    Join,
    Packaging
}

/// <summary>SDLC phase a node belongs to; drives the dashboard phase stepper.</summary>
public enum WorkflowPhase
{
    Intake,
    Planning,
    Design,
    Generation,
    Validation,
    Release
}

/// <summary>Whether a dependency edge blocks readiness (Hard) or only feeds context (Soft).</summary>
public enum EdgeKind
{
    Hard,
    Soft
}

/// <summary>Kind of engineering artifact. Values are stack-agnostic — no scenario leakage.</summary>
public enum ArtifactType
{
    EngineeringSpecification,
    WorkPlan,
    AdrSet,
    ComponentDiagram,
    ServiceContracts,
    BrownfieldReport,
    SourceCode,
    OpenApiSpec,
    DbScript,
    InfrastructureAsCode,
    TestSuite,
    DocSet,
    ReleaseNotes,
    ValidationReport,
    RiskReport,
    ClarificationQuestions,
    ClarificationAnswers,
    ReviewPackage
}

/// <summary>Lifecycle of an artifact version. Superseded rows are retained for lineage.</summary>
public enum ArtifactStatus
{
    Draft,
    Approved,
    Superseded
}

/// <summary>Category of a materialized requirement item; the anchor of all traceability.</summary>
public enum RequirementKind
{
    Functional,
    NonFunctional,
    Assumption,
    OpenQuestion
}

/// <summary>Whether a gate is evaluated on node entry or on node exit.</summary>
public enum GateStage
{
    Entry,
    Exit
}

/// <summary>How a gate is resolved: automated policy, human approval, or human clarification.</summary>
public enum GateType
{
    Policy,
    HumanApproval
}

/// <summary>Distinguishes a plain approval gate from an interactive clarification gate.</summary>
public enum ApprovalKind
{
    Approval,
    Clarification
}

/// <summary>Resolution state of an approval/clarification. Rows are voided, never deleted.</summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Answered,
    AutoPassed,
    AutoFailed,
    Voided
}

/// <summary>Category of a risk item.</summary>
public enum RiskCategory
{
    Technical,
    Architectural,
    Security,
    Operational,
    Performance,
    Reliability
}

/// <summary>Severity/likelihood level shared by risks.</summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>Lifecycle of a risk item.</summary>
public enum RiskStatus
{
    Open,
    Mitigated,
    Accepted
}

/// <summary>Which provider served a model call — surfaced in prompt lineage.</summary>
public enum LlmProviderKind
{
    Anthropic,
    Mock
}

/// <summary>
/// Append-only audit taxonomy (spec §9.1). Every state transition and human action maps to one
/// of these; the ordered stream is the workflow timeline.
/// </summary>
public enum AuditEventType
{
    WorkflowCreated,
    WorkflowStarted,
    WorkflowPaused,
    WorkflowResumed,
    WorkflowCancelled,
    WorkflowCompleted,
    WorkflowFailed,
    NodeReady,
    NodeStarted,
    NodeSucceeded,
    NodeFailed,
    NodeRetryScheduled,
    NodeTimedOut,
    NodeStale,
    NodeSkipped,
    NodeRecoveredAfterRestart,
    GateEvaluated,
    ApprovalRequested,
    ApprovalGranted,
    ApprovalRejected,
    ApprovalVoided,
    ClarificationRequested,
    ClarificationAnswered,
    ArtifactCreated,
    ArtifactApproved,
    ArtifactSuperseded,
    DecisionRecorded,
    RiskRecorded,
    ReplanTriggered,
    RollbackTriggered,
    LlmCallCompleted,
    LlmJsonRetry,
    LlmFallback,
    ValidationRun,
    ReviewPackageAssembled
}
