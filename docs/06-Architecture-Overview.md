# Architecture Overview
## Agentic Software Engineering System

| Field | Value |
|---|---|
| Document | Architecture Overview |
| Version | 1.0 |
| Date | 2026-07-28 |
| Scope | Components · orchestration model · control flow · key decisions |
| Companion | [02-Engineering-Specification](02-Engineering-Specification.md) (full detail + requirements traceability) |

This document is the architectural narrative: how the pieces fit, how the engine runs, how a request
flows through the system, and the decisions that shaped it. It is deliberately visual; the companion
specification carries the exhaustive detail.

---

## 1. System context

```
        ┌────────────┐   natural-language requirement   ┌──────────────────────────┐
        │   Human    │ ───────────────────────────────▶ │  Agentic SDLC Platform    │
        │  Engineer  │ ◀─────────────────────────────── │  (this system)            │
        └────────────┘   approvals · review package      └───────────┬──────────────┘
                                                                      │
                              ┌───────────────────────────┬──────────┴───────────┐
                              ▼                            ▼                      ▼
                     ┌────────────────┐          ┌──────────────────┐    ┌──────────────┐
                     │  Claude API    │          │  .NET SDK        │    │  SQLite      │
                     │  or mock       │          │  (dotnet build/  │    │  (system of  │
                     │  provider      │          │   test)          │    │   record)    │
                     └────────────────┘          └──────────────────┘    └──────────────┘
```

The platform is the system under construction. It talks to a language model (real or mock), the .NET
toolchain (to genuinely build and test generated code), and SQLite (durable state).

---

## 2. Component architecture

### 2.1 Containers

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  AgenticSdlc.Web                                                               │
│    REST API (minimal APIs) · SSE event stream · vanilla-JS dashboard (wwwroot) │
│    ReadModel (entity→DTO) · ScenarioCatalog · EventBroadcaster                 │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                     │  depends only on  Core.Abstractions
                                     │  (+ read queries via the context factory)
┌───────────────────────────────────┴──────────────────────────────────────────┐
│  AgenticSdlc.Core                                                              │
│                                                                                │
│   Abstractions   IWorkflowEventBus + DTOs         ← the only public seam       │
│   Orchestration  engine, graph builder, executor, context builder,            │
│                  replan/rollback, signaler, background runner, WorkflowService  │
│   Agents         IAgent, AgentBase<T>, 7 agents, Contracts/                    │
│   Governance     GateEvaluator, ApprovalService, 5 policies                    │
│   Llm            ILlmProvider, Anthropic adapter, mock, selector, JsonExtractor │
│   Workspace      WorkspaceManager, DotnetCliRunner                             │
│   Observability  AuditLogger, WorkflowEventBus, MetricsService, TimelineService │
│   Packaging      ReviewPackageBuilder                                          │
│   Persistence    AgenticDbContext (context factory), DbInitializer             │
│   Domain         Workflow, WorkflowNode, DependencyEdge, Artifact, Decision,   │
│                  Approval, AuditEvent, AgentExecution, RiskItem, …             │
└────────────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Dependency rule

```
Domain ──────────────┐
Abstractions ────────┤ depend on nothing
                     ▼
   (every other namespace)  may depend on Domain + Abstractions
                     ▼
   Web  depends only on Abstractions (+ context factory for reads)
```

`AgenticSdlc.Core` is a single library; namespaces provide the separation at this scale. The web layer's
only structural coupling to the core is `services.AddAgenticSdlcCore(configuration)` plus the
`Abstractions` interfaces — the domain model never leaks past the web contracts.

### 2.3 Agent abstraction

Every reasoning agent extends one base and is resolved by type — adding an agent is a one-class change,
no engine modification.

```
             IAgent  (Type, ExecuteAsync(input, context) → AgentResult)
                │
      AgentBase<TOutput>  ── prompt build → LLM call → JSON extract → reparse-retry → lineage log
                │
   ┌────────────┼───────────────┬───────────────┬──────────────┬──────────────┐
Requirement  Planning   Architecture  Brownfield   Risk       Generation   (Validation is
Intelligence                                                                  hybrid: real
                                                                              dotnet + 1 call)
```

---

## 3. Orchestration model

The orchestration engine is the platform's centre of gravity. It executes an explicit **dependency
graph**, not a sequential chain.

### 3.1 The SDLC graph

Built by `GraphBuilder` from a built-in template plus **dynamic expansion** from the Planning agent's
output. Hard edges (`──▶`) block readiness; soft edges (`⋯▶`) only carry context.

```
                                          ┌──── arch ────┐
 spec ──▶ brownfield ──▶ plan ──▶─────────┤              ├──▶ gen.ready ──▶ [ gen.* … ] ──▶ gen.done ──▶ validate ──▶ package
   │       (skipped     │  ⋯▶ brownfield   └──── risk ⋯──┘      (join)      (dynamic,        (join)      (real        (review
   │        greenfield) │                                                    parallel)                    dotnet)      package)
   └── exit: no-blocking-ambiguities        exit gates:                    entry gate (brownfield):     exit gates:   entry gates:
       (→ clarification)                    plan = HUMAN                   gen.ready = HUMAN            build-succeeds  HUMAN +
                                            arch = HUMAN (high impact)     (risk acceptance)           pass-rate       change-control
                                                                                                       secret-scan
```

- **Dynamic expansion** inserts one `gen.*` node per planner task, wired by the plan's own `dependsOn`
  declarations. Independent tasks become siblings with no edge between them — and therefore run in
  parallel. This is what makes generation *graph-driven*, not fixed by the platform.
- **Join nodes** (`gen.ready`, `gen.done`) are synchronization points: a join becomes ready only when
  every inbound hard branch has completed.

### 3.2 Node state machine

```
                       deps met + entry gates pass
        ┌──────────┐  ─────────────────────────▶  ┌─────────┐        ┌──────────┐
        │ Pending  │                               │  Ready  │───────▶│ Running  │
        └────┬─────┘                               └─────────┘        └────┬─────┘
             ▲                                                              │ exit gates
             │ retry (NextRetryAt)                     ┌────────────────────┤
             │                       entry gate=human  │                    │
             ├──────────────────────▶ AwaitingApproval ◀────────────────────┤ exit gate=human
             │       approve / answer         │                             │
             │                                ▼                             ▼
             │                          ┌──────────┐  attempts left    ┌───────────┐
             └──────────────────────────│  Failed  │◀──────────────────│ Succeeded │
                     replan             └──────────┘  exhausted         └───────────┘
   Stale ──▶ Pending   ·   Skipped (inapplicable)   ·   Cancelled (any non-terminal → cancel)
```

### 3.3 The scheduler (tick model)

`WorkflowRunnerService` (a hosted `BackgroundService`) is event-driven off a channel, with a 5-second
sweep as a safety net. Per workflow, `WorkflowEngine.TickAsync` runs serialized under a lock:

```
Tick(workflowId):
  1. load workflow + nodes + edges         (return unless Running / AwaitingApproval)
  2. readiness scan  → Pending nodes whose every HARD in-edge source is Succeeded/Skipped
  3. for each ready node:
        entry gates → Fail? Await? Pass?
        Join/Packaging  → succeed inline (Packaging assembles the review package)
        agent node      → set Running, Attempt++, dispatch NodeExecutor (throttled by MaxParallelNodes)
  4. recompute workflow status  → Running | AwaitingApproval | Completed | (Failed set by executor)
```

Node completion re-signals the channel, so the engine is event-driven with the sweep only as a backstop.

### 3.4 Parallelism, synchronization, context

- **Parallel** — ready sibling nodes are dispatched together, each on the thread pool under a global
  concurrency semaphore; short-lived DbContexts keep writes safe (WAL + busy-timeout).
- **Synchronized** — downstream of a join waits because the join's readiness requires all inbound
  branches Succeeded.
- **Context propagation** — before an agent runs, `WorkflowContextBuilder` walks its upstream edges
  (hard *and* soft) and assembles the latest non-superseded artifacts, all decisions, requirements, and
  open risks into a `WorkflowContext`. That record is the entire integration surface between agents.

### 3.5 Dynamic re-planning

`ReplanService` invalidates work when an upstream artifact changes (a rejection-with-changes, an
explicit re-plan, or a rollback):

```
replan(from node N):
  BFS downstream of N →
     each affected node: Stale → Pending (attempt reset)
     its artifacts: → Superseded         (retained for lineage, never deleted)
     its approvals: → Voided             (so the re-run re-requests; audit log keeps the history)
  reactivate a terminal workflow → Running ; signal the engine
```

This is what keeps governance honest under change: an approval granted against a superseded artifact
cannot silently authorize its replacement.

---

## 4. Control flow

### 4.1 Workflow lifecycle (happy path)

```
Client ─POST /workflows──▶ WorkflowService.Create ── build graph, (seed workspace for brownfield)
       ◀─ 201 {id}                                   │
Client ─(implicit start)─▶ WorkflowService.Start ──▶ status=Running, signal
                                                     ▼
                             ┌───────────────── Runner loop ─────────────────┐
                             │  Engine.Tick → dispatch ready nodes            │
                             │  NodeExecutor → agent → persist → exit gates   │
                             │  (repeat, driven by signals)                   │
                             └───────────────────────────────────────────────┘
                                                     ▼
                             human gate reached → status=AwaitingApproval
Client ─POST gates/{id}/decision──▶ ApprovalService.Resolve → node Succeeded, signal
                                                     ▼
                             all nodes terminal → status=Completed
                             package node → ReviewPackageBuilder → ReviewPackage artifact
```

### 4.2 A single node execution (`NodeExecutor`)

```
1. reload node (bail unless Running)
2. linked CancellationToken = workflow token + per-node timeout
3. context = WorkflowContextBuilder.Build(node)
4. result  = agent.Execute(input, context, ct)     ── AgentBase: call → JSON-retry → lineage row
5. persist (one transaction): requirements ↑upsert, artifacts ↑version+supersede, decisions, risks,
                              plan → GraphBuilder.ExpandFromPlan
6. exit gates:  Pass → Succeeded (+ artifacts Approved)
                AwaitingHuman → AwaitingApproval (artifacts stay Draft)
                Fail(policy) → retry-or-fail
   failure paths:  workflow-cancelled → revert to Pending (no attempt consumed)   [safe stop]
                   timeout/exception  → Attempt++, backoff retry, else Failed (isolated)
7. signal the engine
```

### 4.3 Human approval (pause → resume)

```
GateEvaluator (exit, human)  → create Approval(Pending), node → AwaitingApproval, audit
   … other branches keep running; workflow reports AwaitingApproval only when nothing else runs …
ApprovalService.Resolve(approve)  → Approval=Approved, node=Succeeded, artifacts=Approved, signal
ApprovalService.Resolve(reject+changes) → feedback into node instructions, node→Pending,
                                          downstream invalidated, signal
```

### 4.4 Real-time updates (SSE)

```
AuditLogger.Log(...)  ──publish──▶ IWorkflowEventBus (bounded, drop-oldest)
                                        │
EventBroadcaster (hosted)  ──reads──────┘  sequences · ring-buffer replay · per-client channels · heartbeat
        │
        └── GET /api/events (SSE) ──▶ browser EventSource ──▶ dashboard: refetch state, patch one graph node
```

The client refetches full state on connect and patches on each event, so correctness does not depend on
replay-buffer adequacy.

### 4.5 Restart recovery

```
process crash mid-run → nodes left "Running" in SQLite
        │
new process → WorkflowRunnerService.StartAsync → Engine.Recover:
        Running nodes → Pending (their in-flight tasks died)
        active workflows → signalled → resume from persisted state
```

---

## 5. Data model (traceability spine)

```
                         Workflow
   ┌──────────┬─────────────┼──────────────┬───────────────┐
RequirementItem  WorkflowNode ──▶ DependencyEdge   Decision   RiskItem
   │  (FR/NFR/AS/OQ)   │                              │
   │                   ├── Artifact (versioned, superseded-chain)
   │                   ├── Approval (Pending→Approved/Rejected/Voided/Answered)
   │                   └── AgentExecution (prompt lineage: prompts, response, tokens)
   └────────────────── AuditEvent (append-only, seq-ordered = the timeline)

  Traceability: Artifact.RequirementIds ↔ RequirementItem.Code ; Decision.RequirementIds ↔ same.
  Every artifact traces back to the requirement that caused it, and forward from any requirement.
```

Persistence is EF Core + SQLite via a **context factory** (short-lived contexts for parallel executors);
enums stored as strings; `DateTimeOffset` stored as UTC ticks so ordering translates to SQL.

---

## 6. Key architectural decisions

| # | Decision | Rationale |
|---|---|---|
| AD-1 | **Explicit dependency-graph engine**, not a sequential pipeline | The requirement's differentiator; enables parallelism, joins, and dynamic re-planning. |
| AD-2 | **Graph built from an SDLC template + runtime expansion from the plan** | Phase ordering is universal; decomposition is requirement-specific and decided by an agent at run time. |
| AD-3 | **`ILlmProvider` with auto-selecting mock/live** | Same orchestration code runs offline (deterministic) or live; a demo can't fail on connectivity or quota. |
| AD-4 | **Governance evaluated in the execution path via `IGateEvaluator`** | Gates enforce regardless of the UI; the engine depends on a seam, keeping it testable in isolation. |
| AD-5 | **Real validation (`DotnetCliRunner`) with policies deciding on facts** | "Validation results determine progression" is meaningful only if validation is genuine. |
| AD-6 | **`AddDbContextFactory`, short-lived contexts, WAL** | `DbContext` is not thread-safe; parallel executors each own a context. |
| AD-7 | **Compensating re-plan/rollback; approvals voided not deleted** | Preserves lineage and governance history under change. |
| AD-8 | **Event bus → SSE; refetch-on-event client** | Server→client only; native `EventSource` gives reconnection/replay free; correctness independent of buffers. |
| AD-9 | **Single core library + `Abstractions` seam** | Namespaces suffice at this scale; the web layer couples only to interfaces + DTOs. |
| AD-10 | **No demonstration-domain knowledge in platform code** | Scenarios are seed data; the platform stays reusable across problems. |

---

## 7. Cross-cutting concerns

| Concern | Mechanism |
|---|---|
| **Concurrency** | Per-workflow tick lock; global node-concurrency semaphore; in-flight guard; short-lived DbContexts; WAL + busy-timeout. |
| **Resilience** | Exponential-backoff retries; per-node timeout via linked token; failure isolation (soft edges + `ContinueOnFailure`); safe stop; rollback; restart recovery. |
| **Observability** | Append-only audit timeline; prompt lineage per model call; artifact versioning/supersession; ten on-demand metrics; auto-assembled review package. |
| **Security** | Workspace path-traversal containment (canonicalize + prefix check); escape-first, no-raw-HTML markdown; credentials only from environment, never config or logs. |
| **Extensibility** | New agent = implement `IAgent` + register; new policy = implement `IGatePolicy` + register; the engine is untouched. |
