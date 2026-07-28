namespace AgenticSdlc.Web.Contracts;

// ---- Requests ----

public sealed record StartWorkflowRequest(string Scenario, string Requirement, string? Name = null, string? SourceWorkflowId = null);
public sealed record GateDecisionRequest(string Decision, string Approver, string? Comment = null); // Decision: approve|reject
public sealed record ClarificationAnswersRequest(string Respondent, List<ClarificationAnswerDto> Answers);
public sealed record ClarificationAnswerDto(string QuestionId, string Answer);
public sealed record ReplanRequest(string NodeId, string Reason);

// ---- Responses ----

public sealed record ScenarioDescriptor(string Id, string Title, string Description, string SampleRequirement, bool RequiresExistingCodebase);

public sealed record WorkflowSummary(
    string Id, string Name, string Scenario, string Status, string CurrentPhase,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    int NodesTotal, int NodesSucceeded, int NodesFailed, int PendingApprovals);

public sealed record WorkflowDetail(
    string Id, string Name, string Scenario, string Status, string Requirement, string CurrentPhase,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? FailureReason, string WorkspacePath,
    IReadOnlyList<NodeDto> Nodes, IReadOnlyList<EdgeDto> Edges, IReadOnlyList<PhaseDto> Phases,
    IReadOnlyList<GateDto> PendingGates);

public sealed record NodeDto(
    string Id, string Key, string Label, string AgentType, string Phase, string State,
    int Attempt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error);

public sealed record EdgeDto(string FromNodeId, string ToNodeId, string Kind);
public sealed record PhaseDto(string Name, string Status);

public sealed record GateDto(
    string GateId, string WorkflowId, string NodeId, string Kind, string Title, string Description,
    string Status, DateTimeOffset RaisedAt, DateTimeOffset? ResolvedAt, string? Approver, string? Comment,
    IReadOnlyList<ClarificationQuestionDto>? Questions);

public sealed record ClarificationQuestionDto(string QuestionId, string Question, string? Rationale, IReadOnlyList<string>? SuggestedOptions);

public sealed record ArtifactSummary(
    string Id, string WorkflowId, string ProducedByNodeId, string Name, string Type, string ContentType,
    int Version, string Status, DateTimeOffset CreatedAt, IReadOnlyList<string> RequirementIds, bool HasFile);

public sealed record ArtifactDetail(ArtifactSummary Summary, string? Content, bool ContentTruncated, ArtifactLineage Lineage);
public sealed record ArtifactLineage(string ProducedByNodeId, string? SupersededByArtifactId, IReadOnlyList<string> RequirementIds);

public sealed record DecisionDto(string Id, string NodeId, string AgentType, string Title, string Rationale, IReadOnlyList<string> RequirementIds, DateTimeOffset At);
public sealed record RiskDto(string Id, string Category, string Severity, string Likelihood, string Title, string Description, string Mitigation, string Status);
public sealed record TimelineEntryDto(long Seq, DateTimeOffset At, string EventType, string Actor, string Summary, string? NodeId, string? NodeKey);
public sealed record PromptDto(string Id, string NodeId, string AgentType, int Attempt, string Provider, string Model, bool ParsedOk, int InputTokens, int OutputTokens, int DurationMs, DateTimeOffset At);

public sealed record HealthDto(string Status, bool DatabaseOk, bool AnthropicKeyConfigured, string Provider, string Model, int ActiveWorkflows, int PendingApprovals);
