# Engineering Specification
## Agentic Software Engineering System

| Field | Value |
|---|---|
| Document | Architecture & Engineering Specification |
| Version | 1.0 |
| Date | 2026-07-28 |
| Status | For review — precedes implementation |
| Predecessor | [01-Requirement-Understanding.md](01-Requirement-Understanding.md) |
| Successor | [03-Implementation-Plan.md](03-Implementation-Plan.md) |

Requirement identifiers (FR-n, NFR-n, AS-n, R-n) referenced throughout are defined in the
Requirement Understanding document.

---

## 1. System Overview

### 1.1 Purpose

A platform that converts a natural-language engineering requirement into validated, traceable
engineering artifacts by orchestrating specialized AI agents across a governed SDLC workflow.

### 1.2 Design principles

| # | Principle | Consequence |
|---|---|---|
| P-1 | **The engine is the product** | Orchestration receives the deepest engineering investment. Agents are replaceable; the engine is not. |
| P-2 | **Scenarios are data, never code** (NFR-1) | No demonstration-domain identifier appears in platform source. Scenario knowledge lives in seed files. |
| P-3 | **Persist before you act** (NFR-2, NFR-3) | Every state transition is committed before its effect is observable. Nothing important lives only in memory. |
| P-4 | **Governance is in the execution path** (R-5) | Gates are evaluated by the engine, not the UI. Removing the dashboard removes no control. |
| P-5 | **Validation is real** (A-7) | The toolchain is genuinely invoked. Simulated validation would make gate decisions meaningless. |
| P-6 | **Degrade, don't crash** (FR-25) | Missing credentials, absent toolchain, or malformed model output reduce capability and are recorded — they do not abort a workflow. |
| P-7 | **Prototype-grade clarity** (NFR-11) | Plain dependency injection and explicit control flow. No mediator, CQRS, or event-sourcing frameworks. |

### 1.3 Context

```
   ┌──────────┐   requirement    ┌───────────────────────────────┐   prompts   ┌──────────┐
   │  Human   │ ───────────────▶ │  Agentic SDLC Platform        │ ──────────▶ │  Claude  │
   │ Engineer │ ◀─────────────── │  (orchestration + agents)     │ ◀────────── │   API    │
   └──────────┘  approvals /     └───────────────────────────────┘  structured └──────────┘
                 review package      │              │                 output
                                     │ writes       │ invokes
                                     ▼              ▼
                            ┌────────────────┐  ┌──────────────┐
                            │  Workspace     │  │ .NET SDK     │
                            │  (generated    │  │ (build/test) │
                            │   artifacts)   │  └──────────────┘
                            └────────────────┘
```

### 1.4 Container decomposition

| Container | Responsibility |
|---|---|
| `AgenticSdlc.Core` | The platform: domain, persistence, agents, orchestration engine, governance, resilience, observability, packaging. Has no knowledge of HTTP. |
| `AgenticSdlc.Web` | Delivery surface: REST API, dashboard, real-time event stream. Hosts the engine's background runner. Contains no orchestration logic. |
| `AgenticSdlc.Core.Tests` | Automated verification of engine, governance, and resilience behaviour. |
| SQLite database | System of record. Workflow state, artifacts, decisions, approvals, audit, prompt lineage. |
| Workspace directory | Generated engineering artifacts as real files, one directory per workflow. |

**Rationale for a single core library** (rather than separate Agents/Orchestration/Persistence
assemblies): namespaces provide the necessary separation at this scale; additional projects would add
build ceremony without enforcing a boundary that is under pressure. Revisit if the agent set grows
beyond the SDLC domain.

---

## 2. Component Architecture

```
AgenticSdlc.Core
│
├── Domain/            Entities and enumerations. No behaviour beyond invariants.
├── Persistence/       EF Core context, factory registration, initialization.
├── Llm/               Provider abstraction, live provider, simulation provider, JSON extraction.
├── Agents/            Agent abstraction, base implementation, seven concrete agents, output contracts.
├── Orchestration/     Graph construction, scheduler, node executor, context assembly, re-planning,
│                      signalling, cancellation, background runner, service facade.
├── Governance/        Gate definitions, evaluator, policies, approval service.
├── Resilience/        Retry policy, rollback service.
├── Workspace/         Workspace file management, repository scanning, toolchain invocation.
├── Observability/     Audit logger, metrics service, timeline service, event bus.
├── Packaging/         Review package assembly.
└── Abstractions/      Interfaces and DTOs consumed by the delivery surface. The only public seam.
```

**Dependency rule:** `Abstractions` depends on nothing. `Domain` depends on nothing. Every other
namespace may depend on `Domain` and `Abstractions`. `Web` depends only on `Abstractions`.

---

## 3. Domain Model

### 3.1 Entity relationships

```
                            ┌─────────────┐
                            │  Workflow   │
                            └──────┬──────┘
        ┌──────────────┬───────────┼───────────┬──────────────┬─────────────┐
        ▼              ▼           ▼           ▼              ▼             ▼
┌──────────────┐ ┌──────────┐ ┌────────┐ ┌──────────┐ ┌────────────┐ ┌──────────┐
│ Requirement  │ │ Workflow │ │ Audit  │ │ Decision │ │  RiskItem  │ │ Metric   │
│    Item      │ │   Node   │ │ Event  │ │          │ │            │ │ Snapshot │
└──────┬───────┘ └────┬─────┘ └────────┘ └────┬─────┘ └────────────┘ └──────────┘
       │              │                       │
       │      ┌───────┼────────┬──────────┐   │
       │      ▼       ▼        ▼          ▼   │
       │  ┌────────┐ ┌──────┐ ┌────────┐ ┌────────────┐
       └─▶│Artifact│ │ Edge │ │Approval│ │AgentExecution│
          └────────┘ └──────┘ └────────┘ └────────────┘
             (traceability: Artifact ↔ RequirementItem, Decision ↔ RequirementItem)
```

### 3.2 Entities

Common to all: `Guid Id`, `DateTimeOffset` timestamps in UTC, enumerations persisted as strings,
variable-shape payloads persisted as JSON text columns (suffixed `Json`).

#### Workflow
| Field | Type | Notes |
|---|---|---|
| `Name` | string | Human label |
| `RequirementText` | string | Verbatim input — the root of all traceability |
| `ScenarioKey` | string | Seed-data selector (`greenfield`/`brownfield`/`ambiguous`). Platform behaviour is not branched on this beyond graph seeding. |
| `Status` | enum | `Draft, Running, Paused, AwaitingApproval, Completed, Failed, Cancelled` |
| `Model` | string | Model identifier used |
| `WorkspacePath` | string | Root of generated artifacts |
| `SourceWorkflowId` | Guid? | Brownfield: prior run whose output is being enhanced |
| `CreatedAt / StartedAt / CompletedAt` | DateTimeOffset? | Latency metrics |
| `FailureReason` | string? | Terminal diagnosis |

#### WorkflowNode
| Field | Type | Notes |
|---|---|---|
| `WorkflowId` | Guid | Owner |
| `Key` | string | Stable identifier within the workflow (`spec`, `arch`, `gen.api`) |
| `Name` | string | Display label |
| `AgentType` | enum | `RequirementIntelligence, Planning, Architecture, Brownfield, Generation, Validation, RiskAssessment, Join, Packaging` |
| `Phase` | enum | `Intake, Planning, Design, Generation, Validation, Release` |
| `Status` | enum | See §4.1 |
| `Attempt / MaxAttempts` | int | Retry accounting (FR-20) |
| `NextRetryAt` | DateTimeOffset? | Backoff schedule |
| `TimeoutSeconds` | int | Execution bound (FR-21) |
| `ContinueOnFailure` | bool | Failure isolation flag (FR-22) |
| `TaskInstructionsJson` | string? | Node-specific scoping; also carries reviewer feedback on re-run (FR-19) |
| `GatesJson` | string | Serialized gate definitions (FR-15) |
| `ErrorMessage` | string? | Last failure detail |
| `StartedAt / CompletedAt` | DateTimeOffset? | Timeline and parallelism evidence (FR-10) |

`(WorkflowId, Key)` is unique — this is what makes graph expansion idempotent.

#### DependencyEdge
`WorkflowId`, `FromNodeId`, `ToNodeId`, `Kind` ∈ {`Hard`, `Soft`}.

**Hard** edges block readiness. **Soft** edges contribute context without blocking — this is how a
non-critical analysis node can fail or be skipped without stalling the workflow (FR-22).

#### Artifact
| Field | Type | Notes |
|---|---|---|
| `WorkflowId`, `ProducedByNodeId` | Guid | Provenance (FR-27) |
| `Type` | enum | `EngineeringSpecification, WorkPlan, AdrSet, ComponentDiagram, ServiceContracts, BrownfieldReport, SourceCode, OpenApiSpec, DbScript, InfrastructureAsCode, TestSuite, DocSet, ReleaseNotes, ValidationReport, RiskReport, ClarificationQuestions, ClarificationAnswers, ReviewPackage` |
| `Name` | string | |
| `Version` | int | Incremented on re-execution |
| `Status` | enum | `Draft, Approved, Superseded` |
| `ContentJson` | string? | Structured payload (inline artifacts) |
| `ContentPath` | string? | Workspace-relative path (file artifacts) |
| `RequirementIdsJson` | string | Requirement codes satisfied — **the traceability link** (FR-29) |
| `SupersededByArtifactId` | Guid? | Lineage chain; predecessors retained, never deleted (FR-24) |

#### RequirementItem
`WorkflowId`, `Code` (`FR-1`, `NFR-2`, `AS-1`, `OQ-1`), `Kind` ∈ {`Functional`, `NonFunctional`,
`Assumption`, `OpenQuestion`}, `Text`, `Priority`, `SourceExcerpt`.

Materialized from the specification artifact. This is the anchor every downstream artifact and
decision references, and the denominator of requirement coverage (FR-30).

#### Decision
`WorkflowId`, `NodeId`, `AgentType`, `Title`, `Rationale`, `AlternativesJson`,
`RequirementIdsJson`, `ArtifactIdsJson`, `CreatedAt`.

Every architecture decision record, technology selection, sequencing choice, and documented
assumption becomes a row. Satisfies decision lineage (FR-14).

#### Approval
| Field | Type | Notes |
|---|---|---|
| `WorkflowId`, `NodeId` | Guid | |
| `Stage` | enum | `Entry, Exit` |
| `Kind` | enum | `Approval, Clarification` — the clarification loop reuses the gate mechanism |
| `GateType` | enum | `HumanApproval, Policy` |
| `PolicyName` | string? | For policy gates |
| `Title`, `Description` | string | Rendered to the reviewer |
| `QuestionsJson`, `AnswersJson` | string? | Clarification payload (FR-34) |
| `Status` | enum | `Pending, Approved, Rejected, Answered, AutoPassed, AutoFailed, Voided` |
| `RequestedAt / ResolvedAt` | DateTimeOffset? | Human approval time metric (FR-30) |
| `Approver`, `Comment` | string? | Attribution (FR-16) |
| `EvaluationJson` | string? | Policy evidence (FR-18) |

Rows are **never deleted**. Invalidation sets `Voided`, preserving approval history (FR-28).

#### AuditEvent
`WorkflowId`, `NodeId?`, `Seq` (monotonic), `EventType`, `Actor` (`system` / `agent:{type}` /
`human:{name}`), `Summary`, `DetailJson`, `Timestamp`. Append-only (NFR-2).

#### AgentExecution
`WorkflowId`, `NodeId`, `AgentType`, `Attempt`, `Provider`, `Model`, `SystemPrompt`, `UserPrompt`,
`RawResponse`, `ParsedOk`, `ParseError?`, `InputTokens`, `OutputTokens`, `DurationMs`.

One row per model invocation, including reparse attempts. Satisfies prompt lineage (FR-26) and
supplies token accounting (NFR-10).

#### RiskItem
`WorkflowId`, `NodeId`, `Category`, `Severity`, `Likelihood`, `Title`, `Description`, `Mitigation`,
`Status` ∈ {`Open`, `Mitigated`, `Accepted`}, `RequirementIdsJson`.

#### MetricSnapshot
`WorkflowId?` (null = platform-wide), `CapturedAt`, `MetricsJson`. Written at workflow completion
for historical trend queries; live values are computed on demand.

### 3.3 Persistence design

- **`AddDbContextFactory`**, not scoped `AddDbContext`. Parallel node executors each create a
  short-lived context. This is load-bearing for NFR-7 — `DbContext` is not thread-safe.
- Write-ahead logging enabled at initialization; busy timeout configured. Addresses R-4.
- Indexes: `WorkflowNode(WorkflowId, Status)`, `AuditEvent(WorkflowId, Seq)`,
  `Artifact(WorkflowId, Type, Status)`, `Approval(WorkflowId, Status)`.
- Schema created via `EnsureCreated`. Migrations are deliberately deferred at prototype depth;
  schema evolution during development is handled by deleting the database file.

---

## 4. Orchestration Engine

The core of the system, and the requirement's stated differentiator.

### 4.1 Node state machine

```
                       deps satisfied
                       + entry gates pass
        ┌──────────┐  ─────────────────▶  ┌─────────┐        ┌──────────┐
        │ Pending  │                      │  Ready  │───────▶│ Running  │
        └────┬─────┘                      └─────────┘        └────┬─────┘
             ▲                                                    │
             │ entry gate = human                                 │ exit gates
             │      ┌──────────────────┐                          │
             ├──────│ AwaitingApproval │◀─────────────────────────┤
             │      └────────┬─────────┘   exit gate = human      │
             │  approved     │ rejected                           │
             │               ▼                                    ▼
             │          ┌────────┐                          ┌───────────┐
             └──────────│ Failed │◀─────────────────────────│ Succeeded │
               retry    └────────┘  attempts exhausted      └───────────┘
                                                                  │
   ┌─────────┐   upstream artifact changed                        │
   │  Stale  │◀───────────────────────────────────────────────────┘
   └────┬────┘
        └──────▶ Pending (Attempt reset)

   Any non-terminal ──cancel──▶ Cancelled
   Inapplicable to scenario ──▶ Skipped
```

`Ready` is transient — computed and persisted within a single scheduler tick before dispatch.
`Stale` is transient and audit-visible before immediate reset to `Pending`.

### 4.2 Graph construction

`GraphBuilder` composes the execution graph from two sources: a **built-in SDLC template**
expressing phase ordering that is true of all software engineering, and **dynamic expansion** from
the Planning agent's output expressing decomposition specific to this requirement.

**Template** (satisfies FR-8):

| Node key | Agent | Hard depends on | Soft depends on | Gates |
|---|---|---|---|---|
| `spec` | RequirementIntelligence | — | — | Exit: no-blocking-ambiguities policy |
| `brownfield` | Brownfield | `spec` | — | — (skipped when no existing codebase) |
| `plan` | Planning | `spec` | `brownfield` | Exit: **human approval** |
| `arch` | Architecture | `plan` | `brownfield` | Exit: **human approval** (high impact, FR-17) |
| `risk` | RiskAssessment | `plan` | `arch` | — (`ContinueOnFailure`) |
| `gen.ready` | Join | `arch` | `risk`, `brownfield` | Entry: human approval when brownfield (risk acceptance) |
| `gen.*` | Generation | *expanded from plan* | | `gen.db` exit: **human approval** (schema, FR-17) |
| `gen.done` | Join | all `gen.*` | — | — |
| `validate` | Validation | `gen.done` | `arch` | Exit: build-succeeds + validation-pass-rate + secret-scan policies |
| `package` | Packaging | `validate` | `risk` | Entry: **human approval** (release, FR-17) + change-control policy |

**Dynamic expansion** — on `plan` success, each work-breakdown task assigned to the Generation agent
becomes a node inserted between `gen.ready` and `gen.done`, wired according to the **plan's own
dependency declarations**. Tasks the plan declares independent become sibling nodes with no edge
between them and therefore execute concurrently (FR-10).

This is precisely the property that distinguishes the system from a sequential pipeline: the shape
of the generation stage is decided by an agent at run time, not by the platform at compile time.

### 4.3 Scheduler

`WorkflowRunnerService` (a `BackgroundService`) consumes workflow identifiers from an unbounded
channel, with a periodic sweep as a safety net for time-based transitions:

```
loop until shutdown:
    id ← channel.read(timeout: 5s)
    if id present:  engine.Tick(id)
    else:           engine.TickAllActive()      // retry schedules, restart recovery
```

`WorkflowEngine.Tick(workflowId)` — serialized per workflow by a semaphore so concurrent signals
cannot race:

1. Load workflow with nodes and edges. Return unless status is `Running` or `AwaitingApproval`.
2. **Readiness scan** — `Pending` nodes whose every inbound *hard* edge originates from a node in
   `Succeeded` or `Skipped`, and whose retry schedule (if any) is due.
3. **Entry gates** — evaluate per §5. Policy failure fails the node; human gate creates a pending
   approval and moves the node to `AwaitingApproval` without dispatching.
4. **Dispatch** — mark `Running`, persist, then execute on the thread pool under a global
   concurrency semaphore. An in-flight registry prevents double dispatch.
5. **Join nodes** short-circuit to `Succeeded` on readiness. A join with N inbound hard edges
   becomes ready only when all N branches have completed — this *is* the synchronization
   mechanism (FR-11).
6. **Terminal evaluation** — no non-terminal nodes remain → `Completed` and capture a metric
   snapshot. A failed node with no remaining attempts and no `ContinueOnFailure` → `Failed`. Only
   `AwaitingApproval` nodes remain → workflow `AwaitingApproval`.
7. Every transition writes an audit event and publishes to the event bus.

Node completion re-signals the channel, making the engine event-driven with the sweep as a fallback
rather than a poll loop.

### 4.4 Node execution

`NodeExecutor.Execute(nodeId)`:

1. Fresh database context; reload and confirm the node is still `Running` (guards against
   cancellation racing dispatch).
2. Cancellation token linking the workflow-level token with a timeout of `TimeoutSeconds` (FR-21).
3. Assemble context (§4.5); resolve the agent; invoke.
4. **Persist results in one transaction**: artifacts (version incremented, predecessors marked
   `Superseded`), decisions, risks, and — for the planning node — graph expansion.
5. Transition to `Succeeded`; evaluate exit gates. A human exit gate moves to `AwaitingApproval`
   with artifacts held at `Draft` until approval.
6. **Failure handling**:
   - Cancelled by workflow pause → return to `Pending`, **attempt not incremented** (a safe stop
     must not consume the retry budget — FR-23).
   - Timeout or exception → increment attempt; if budget remains, `Pending` with
     `NextRetryAt = now + base × 2^(attempt−1) ± jitter`; otherwise `Failed`.
   - **Isolation** (FR-22): no other node is touched. Siblings continue. Dependents simply never
     become ready.
7. Signal the scheduler.

### 4.5 Context propagation (FR-13)

```
WorkflowContext
├── RequirementText           the original ask, verbatim
├── Requirements[]            structured FR/NFR/assumption items
├── Decisions[]               every decision recorded so far, with rationale
├── OpenRisks[]               unmitigated risks
├── UpstreamArtifacts[]       latest non-superseded artifact per upstream node,
│                             content truncated to a configured budget
└── WorkspacePath             for agents that read or write files
```

Assembled by transitively walking inbound edges — **both hard and soft** — from the executing node.
Soft edges exist precisely to deliver context without imposing ordering.

The consequence: a technology decision recorded by the Architecture agent is visible verbatim to
every Generation agent, and to the Validation agent that later checks conformance against it.
Agents do not communicate directly; the context record is the entire integration surface.

### 4.6 Dynamic re-planning (FR-12)

Triggered by: rejection with requested changes; explicit node re-run; or any node producing a new
artifact version.

`ReplanService.InvalidateDownstream(nodeId, reason)`:

1. Breadth-first traversal of outbound edges (hard and soft) collecting the affected set.
2. Each affected non-`Pending` node → `Stale` (audited with reason) → `Pending`, attempt reset.
3. Their artifacts → `Superseded`. Rows retained; lineage shows v1 superseded by v2 (FR-27).
4. Their **pending** approvals → `Voided`. **Resolved approvals are retained as history**, and
   because the nodes will re-run, they will request approval afresh.

Point 4 is what preserves governance under re-planning, as the requirement demands. An approval
granted against a superseded artifact cannot silently authorize its replacement.

Re-executed nodes observe the new upstream state through normal context assembly. No special-case
merge logic is required — this falls out of the design rather than being bolted on.

---

## 5. Governance Model

### 5.1 Gate mechanics

A gate is `(Stage, Type, PolicyName?, Parameters?, Description)` serialized onto its node. The
evaluator runs gates in declaration order at the appropriate stage.

| Gate type | Evaluation | Outcome |
|---|---|---|
| **Policy** | Synchronous check against workflow state | `AutoPassed` / `AutoFailed` with evidence recorded |
| **HumanApproval** | Creates a pending approval and suspends the node | Resolved out-of-band by a human decision |
| **Clarification** | Creates a pending approval carrying questions | Resolved by submitted answers |

### 5.2 Policies (FR-18)

| Policy | Applied at | Rule |
|---|---|---|
| `NoBlockingAmbiguities` | `spec` exit | Fails when the specification contains ambiguities marked blocking. Failure raises a **clarification gate** rather than failing the node — this is the ambiguous-requirement scenario (FR-34). |
| `BuildMustSucceed` | `validate` exit | Fails when the build did not succeed. |
| `ValidationPassRate` | `validate` exit | Fails when the test pass rate is below a configured threshold. |
| `SecretScan` | `validate` exit | Fails when generated files match credential patterns (NFR-8). |
| `ChangeControl` | `package` entry | Asserts every high-impact artifact type has a granted human approval on its producing node. Catches governance bypass structurally. |

### 5.3 Approval resolution

`ApprovalService.Resolve(approvalId, decision, approver, comment, requestChanges)`:

| Decision | Effect |
|---|---|
| Approve, entry gate | Node becomes eligible for dispatch |
| Approve, exit gate | Node → `Succeeded`; its artifacts → `Approved` |
| Reject with requested changes | Reviewer comment appended to node instructions; node → `Pending` for re-execution; downstream invalidated (FR-19) |
| Reject outright | Node → `Failed` |
| Clarification answered | Answers persisted as an artifact and appended to node instructions; node re-executes |

Every resolution is audited with identity and timestamp, and feeds human-approval-time metrics.

**Important property:** while a node awaits approval, unrelated branches continue executing. The
workflow reports `AwaitingApproval` only when nothing else is runnable. Governance suspends *work
requiring authorization*, not the entire system.

### 5.4 Clarification convergence

Bounded by configuration (default 2 rounds). On exhaustion the platform proceeds using explicitly
documented assumptions recorded as `Decision` rows and surfaced in the review package — satisfying
"converging on an engineering solution" without unbounded interrogation.

---

## 6. Resilience Model

| Concern | Mechanism | Requirement |
|---|---|---|
| Transient failure | Exponential backoff with jitter, bounded attempts, scheduled via `NextRetryAt` and dispatched by the sweep tick | FR-20 |
| Malformed model output | Two-tier: in-conversation reparse request (cheap), then full node retry (expensive) | R-3 |
| Hung execution | Per-node timeout on a linked cancellation token | FR-21 |
| Blast radius | Failure mutates only the failed node; `ContinueOnFailure` plus soft edges allow non-critical nodes to fail without blocking | FR-22 |
| Safe stop | Workflow-level cancellation; in-flight nodes return to `Pending` **without consuming an attempt** | FR-23 |
| Process restart | All state in SQLite. Startup recovery marks nodes stranded in `Running` as `Pending` and re-signals active workflows | FR-9 |
| Rollback | Compensating rather than destructive: supersede artifacts, reset node, invalidate downstream. Full lineage retained | FR-24 |
| Provider unavailable | Simulation provider fallback; absent toolchain degrades validation to static analysis. Both recorded as decisions | FR-25 |

---

## 7. Agent Specifications

### 7.1 Abstraction

```
IAgent
  AgentType Type
  AgentResult Execute(AgentTaskInput input, WorkflowContext context, CancellationToken ct)

AgentResult
  Artifacts[]      what was produced
  Decisions[]      what was decided, and why
  Risks[]          what could go wrong
  FollowUpTasks[]  proposed graph expansion
  SummaryMarkdown  human-readable account
```

`AgentBase<TOutput>` is a template method owning all model plumbing: prompt assembly from context,
invocation, structured-output extraction, reparse retry, and prompt-lineage recording. A concrete
agent supplies only a system prompt (including its output schema), a user-prompt builder, and a
mapping from parsed output to `AgentResult`.

**Extensibility (NFR-9):** adding an agent means implementing one class and registering it. The
engine resolves agents by type from the container and requires no modification.

### 7.2 Structured output handling (R-3)

Model responses are requested as JSON conforming to a documented schema. Extraction: strip code
fences → locate the first balanced object → deserialize permissively (case-insensitive, trailing
commas tolerated) → validate required fields. On failure the conversation continues with the
malformed response and a correction instruction, bounded by configuration. Persistent failure
escalates to node-level retry.

Both tiers are recorded as `AgentExecution` rows, so parse reliability is measurable per agent.

### 7.3 Agent contracts

| Agent | Consumes | Produces | Requirement |
|---|---|---|---|
| **RequirementIntelligence** | Requirement text | Intent summary; functional and non-functional requirements with identifiers; ambiguities with clarifying questions and severity; assumptions; open questions → specification artifact + requirement rows | FR-1 |
| **Planning** | Specification, brownfield report | Milestones with exit criteria; tasks with agent assignment, dependency declarations, parallelism flags, requirement links; synchronization points; critical path → work plan artifact + graph expansion | FR-2 |
| **Architecture** | Specification, plan, brownfield report | Selected style with alternatives and trade-offs; architecture decision records; component decomposition; service contracts; technology selections with rationale; component diagram → ADR set, diagram, contracts + one decision per record | FR-3 |
| **Brownfield** | Existing codebase scan, specification | Repository summary; module inventory; dependency findings; change impact per proposed change; refactoring recommendations; risks → brownfield report + risk rows | FR-4 |
| **Generation** | Architecture contracts, assigned plan task, existing code | Files with path, kind, and content → written to workspace; one artifact per kind, referencing satisfied requirements | FR-5 |
| **Validation** | Workspace, ADRs, contracts | Build result; test counts; static findings; security findings; architecture, API, and documentation conformance; overall verdict → validation report | FR-6 |
| **RiskAssessment** | Specification, plan, architecture | Categorized risks with severity, likelihood, mitigation, and requirement links → risk report + risk rows | FR-7 |

### 7.4 Validation agent — hybrid design

Validation is deliberately **not** primarily a model task (P-5). It executes:

1. Real build invocation against the workspace; output captured and parsed.
2. Real test invocation with structured result output; result file parsed for counts.
3. Deterministic static checks — credential patterns, forbidden constructs.
4. **One** model call, scoped to judgements that require reading intent: does the implementation
   conform to the recorded architecture decisions, do the endpoints match the declared contracts,
   is the documentation adequate.

Steps 1–3 produce facts. Step 4 produces judgement. Gate policies evaluate the facts, so
progression cannot be authorized by model opinion alone.

Absent toolchain: build and test sections marked skipped, recorded as a decision, static and
conformance checks still run (FR-25).

---

## 8. Delivery Surface

### 8.1 REST API

All endpoints under `/api`, camelCase JSON, enumerations as strings. Asynchronous control
operations return `202 Accepted`. Errors use problem-details with `400` (validation), `404`
(unknown), `409` (invalid state transition).

| Group | Endpoints |
|---|---|
| Scenarios | `GET /scenarios` — descriptors with prefilled requirement text |
| Workflows | `POST /workflows`; `GET /workflows`; `GET /workflows/{id}`; `POST /workflows/{id}/{pause\|resume\|stop\|cancel}` |
| Governance | `GET /approvals`; `GET /workflows/{id}/approvals`; `POST /workflows/{id}/gates/{gateId}/decision`; `POST /workflows/{id}/gates/{gateId}/clarifications` |
| Traceability | `GET /workflows/{id}/{decisions\|risks\|audit\|logs\|prompts}` |
| Artifacts | `GET /workflows/{id}/artifacts`; `GET /artifacts/{id}`; `GET /artifacts/{id}/download`; `GET /workflows/{id}/workspace/tree`; `GET /workflows/{id}/workspace/file` |
| Metrics | `GET /workflows/{id}/metrics`; `GET /metrics` |
| Reporting | `GET /workflows/{id}/review-package`; `GET /workflows/{id}/review-package.md` |
| Operations | `GET /health`; `GET /events` (stream) |

OpenAPI document published from the framework's built-in generator.

**Path containment (NFR-8):** workspace file access canonicalizes the requested path and rejects any
result outside the workflow's workspace root. Generated content is untrusted input.

### 8.2 Real-time updates

Server-sent events, chosen over WebSockets or polling because the traffic is strictly
server-to-client, the browser provides automatic reconnection and last-event replay natively, and it
requires no client library — preserving the no-build-step constraint (NFR-6).

Event types: `workflow.updated`, `node.updated`, `gate.raised`, `gate.resolved`,
`artifact.created`, `log`, `metrics.updated`, `heartbeat`.

The broadcaster assigns sequence numbers, retains a bounded replay buffer, and fans out through
per-client bounded channels that drop oldest rather than block — **a stalled browser cannot apply
backpressure to the engine**.

Client correctness strategy: refetch full state on every connection open, patch incrementally on
events. Convergence therefore does not depend on replay-buffer adequacy.

### 8.3 Dashboard

Static assets, no package manager, no content-delivery dependencies — runs offline (NFR-5, NFR-6).

| View | Content |
|---|---|
| Home | Scenario picker with prefilled requirements; workflow list with live status |
| Workflow | **Dependency graph** — layered directed-acyclic layout rendered as SVG, node fill driven by state attribute so a single event patches one node; phase progression; lifecycle controls; pending approval and clarification cards with decision forms; tabs for activity log, artifacts with content viewer and lineage links, workspace file browser, decisions, risks, metrics |
| Review | Assembled engineering package, printable, downloadable as markdown |

Graph layout: topological ranking for layer assignment, barycenter ordering to reduce crossings,
bezier edges. Approximately 150 lines — chosen over a diagramming library because per-node patching
on live events is a first-class requirement and full re-render on each update is visually poor.

Markdown rendering escapes first and permits no raw HTML passthrough — generated content is
untrusted (NFR-8).

---

## 9. Observability and Metrics

### 9.1 Audit taxonomy (FR-28)

Workflow lifecycle: created, started, paused, resumed, cancelled, completed, failed.
Node lifecycle: ready, started, succeeded, failed, retry scheduled, timed out, stale, skipped,
recovered after restart.
Governance: gate evaluated, approval requested, granted, rejected, voided.
Artifacts: created, approved, superseded.
Reasoning: decision recorded, risk recorded, re-plan triggered.
Model: call completed, reparse attempted, provider fallback.
Process: validation run, review package assembled.

The audit stream ordered by sequence *is* the workflow timeline; the dashboard renders it directly
rather than maintaining a parallel representation.

### 9.2 Metrics (FR-30)

Computed on demand by query rather than accumulated in counters — there is no second source of
truth to drift.

| Metric | Derivation |
|---|---|
| Workflow success rate | Completed ÷ terminal workflows |
| Agent success rate | Per agent type, succeeded ÷ attempted node executions |
| Retry frequency | Σ(attempts − 1) ÷ node count |
| Rollback frequency | Count of artifact-superseded events |
| MTTR | Mean elapsed time from node failure to subsequent success of that node (A-4) |
| Workflow latency | Completion minus start; per phase from node timestamps |
| Human approval time | Mean of resolution minus request across human gates |
| Validation pass rate | Tests passed ÷ tests total from the latest validation report |
| Requirement coverage | Requirements referenced by ≥1 current artifact ÷ total requirements |
| Test coverage | Tests passed ÷ total, plus tests per functional requirement |

Token consumption and estimated cost aggregate from prompt-lineage rows.

### 9.3 Review package (FR-31)

Assembled by the `package` node into markdown and JSON: requirement interpretation and any
clarification exchange; engineering plan; architecture rationale with decision records; artifact
index with versions and requirement linkage; validation results; risk register; trade-off analysis
from decision alternatives; assumptions; limitations; complete approval history; audit summary;
metrics; and a computed release-readiness verdict — all gates passed, validation succeeded, no open
critical risks.

---

## 10. Configuration

```
AgenticSdlc:
  Llm:            Provider (Auto|Live|Simulation), Model, MaxTokens,
                  MaxReparseAttempts, MaxContextCharsPerArtifact
  Orchestration:  MaxParallelNodes, DefaultNodeTimeoutSeconds, MaxAttempts,
                  RetryBaseDelaySeconds, ClarificationMaxRounds
  Persistence:    DbPath
  Workspace:      Root, SamplesRoot
  Events:         RingBufferSize
```

Credentials are supplied by environment variable only and never appear in configuration files
(NFR-8). Provider selection defaults to automatic: live when credentials are present, deterministic
simulation otherwise (NFR-5, AS-5). **Both paths execute identical orchestration, governance,
persistence, and validation code** — simulation substitutes the model response, nothing else.

---

## 11. Requirements Traceability

| Requirement | Realized by |
|---|---|
| FR-1 … FR-7 | §7.3 agent contracts |
| FR-8 | §4.2 graph construction |
| FR-9 | §3.3 persistence; §6 restart recovery |
| FR-10 | §4.3 dispatch under concurrency limit; §4.2 dynamic expansion |
| FR-11 | §4.3 join nodes |
| FR-12 | §4.6 re-planning |
| FR-13 | §4.5 context propagation |
| FR-14 | §3.2 Decision entity |
| FR-15 … FR-19 | §5 governance model |
| FR-20 … FR-25 | §6 resilience model |
| FR-26 | §3.2 AgentExecution |
| FR-27 | §3.2 Artifact versioning and supersession |
| FR-28 | §9.1 audit taxonomy |
| FR-29 | §3.2 requirement linkage on artifacts and decisions |
| FR-30 | §9.2 metrics |
| FR-31 | §9.3 review package |
| FR-32 … FR-34 | §4.2 scenario-aware seeding; §5.4 clarification convergence |
| NFR-1 | P-2; scenario data isolated from platform code |
| NFR-2, NFR-3 | §3.3 persistence; append-only audit |
| NFR-4 | §8.2 event stream; §8.3 dashboard |
| NFR-5 | §10 simulation provider |
| NFR-6 | §8.3 no build chain |
| NFR-7 | §3.3 context factory; write-ahead logging |
| NFR-8 | §8.1 path containment; §8.3 escaped rendering; §10 credential handling |
| NFR-9 | §7.1 agent abstraction |
| NFR-10 | §7.2 bounded retries; §4.5 context truncation |
| NFR-11 | P-7 |

---

## 12. Known Limitations

Stated explicitly so they read as decisions rather than defects:

1. Single-process execution. Horizontal scale would require externalizing the scheduler.
2. No authentication; approver identity is asserted, not verified (AS-1).
3. Real build and test validation is .NET-specific. Other stacks generate artifacts but receive
   static and conformance validation only (A-2, OQ-4).
4. Rollback preserves artifact metadata lineage but overwrites workspace files on re-execution;
   prior file *content* is not retained.
5. Schema evolution requires recreating the database — no migrations at prototype depth.
6. Infrastructure-as-code is generated as artifacts; nothing is provisioned (A-2).
