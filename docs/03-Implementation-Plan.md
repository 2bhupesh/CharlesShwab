# Implementation Plan
## Agentic Software Engineering System

| Field | Value |
|---|---|
| Document | Engineering Execution Plan |
| Version | 1.0 |
| Date | 2026-07-28 |
| Status | For approval — precedes implementation |
| Predecessors | [01-Requirement-Understanding.md](01-Requirement-Understanding.md), [02-Engineering-Specification.md](02-Engineering-Specification.md) |

---

## 1. Approach

### 1.1 Sequencing strategy

Three constraints determine the build order:

1. **The engine is the graded differentiator** (R-1). It is built early and proven end to end before
   any effort is spent deepening agent output quality. If time runs short, the system should be one
   with an excellent engine and adequate generated code — never the reverse.

2. **The simulation provider comes before the live provider.** With deterministic model responses
   available from the second work package, the entire orchestration engine becomes testable without
   credentials, network, or non-determinism. This inverts the usual integration-last ordering and
   removes the largest source of debugging friction (R-3).

3. **The delivery surface comes after the core is proven.** Building the dashboard against a
   working engine avoids maintaining a parallel simulation of engine behaviour, and eliminates the
   drift risk that a placeholder implementation would introduce.

### 1.2 Vertical slices

Work is organized into slices that each end in a demonstrable, verified state rather than into
horizontal layers that only become meaningful when combined. After WP-5 the platform executes a
complete workflow; every subsequent package deepens rather than unblocks.

### 1.3 Scaffold reuse

The existing solution is extended, not replaced:

| Existing | Action |
|---|---|
| `AgenticSdlc.slnx` | Add the test project. No other structural change. |
| `global.json` (SDK 10.0.302) | Unchanged. |
| `src/AgenticSdlc.Core` (packages already referenced) | Remove `Class1.cs`; build the platform here. |
| `src/AgenticSdlc.Web` (minimal API referencing Core) | Replace the placeholder endpoint with the delivery surface. |

---

## 2. Work Breakdown Structure

Effort is expressed as relative size (S / M / L). Sequence and dependency matter more than duration.

### WP-0 — Design documentation *(complete)*

| | |
|---|---|
| **Deliverables** | Requirement Understanding; Engineering Specification; this plan |
| **Depends on** | — |
| **Size** | M |
| **Verification** | Documents are self-contained and reviewable; every requirement carries an acceptance signal; specification traces all requirements to components |
| **Status** | ✅ Complete |

### WP-1 — Domain and persistence

| | |
|---|---|
| **Deliverables** | All domain entities and enumerations; database context with context-factory registration, string-persisted enumerations, and indexes; initializer enabling write-ahead logging; configuration options; service-registration extension; test project added to the solution |
| **Depends on** | WP-0 |
| **Size** | M |
| **Requirements** | FR-9, NFR-2, NFR-3, NFR-7 |
| **Verification** | Round-trip test per entity against a temporary file-backed database (not in-memory — the context factory opens multiple connections); enumeration values persist as readable strings; concurrent writes from parallel contexts succeed |
| **Exit criteria** | Solution builds; persistence tests green |

### WP-2 — Model provider layer

| | |
|---|---|
| **Deliverables** | Provider abstraction; structured-output extractor; simulation provider with response catalog covering all seven agents; live provider adapting the Anthropic SDK; automatic provider selection |
| **Depends on** | WP-1 |
| **Size** | M |
| **Requirements** | NFR-5, NFR-10, R-3 |
| **Verification** | Extractor handles fenced output, prose-wrapped objects, trailing commas, and truncation; simulation provider returns identical output for identical input; provider selection resolves correctly with and without credentials present |
| **Exit criteria** | A caller can obtain a schema-valid agent response with no network access |
| **Note** | The live provider is the only file coupled to the SDK surface. Verify member names against the installed package here, while the blast radius is one file. |

### WP-3 — Agent abstraction and first agents

| | |
|---|---|
| **Deliverables** | Agent interface; base implementation covering prompt assembly, invocation, extraction, reparse retry, and prompt-lineage recording; agent registry; Requirement Intelligence and Planning agents with output contracts |
| **Depends on** | WP-2 |
| **Size** | M |
| **Requirements** | FR-1, FR-2, FR-26, NFR-9 |
| **Verification** | Scripted provider returning malformed-then-valid output proves the reparse loop and produces two prompt-lineage rows; specification output materializes addressable requirement items; plan output declares dependencies consumable by the graph builder |
| **Exit criteria** | Both agents produce valid results from simulated responses |

### WP-4 — Orchestration engine ★

**The critical work package.** Everything before it is preparation; everything after deepens it.

| | |
|---|---|
| **Deliverables** | Graph builder with SDLC template and plan-driven expansion; scheduler tick; node executor; context assembler; signalling channel; cancellation registry; background runner; workflow service facade |
| **Depends on** | WP-3 |
| **Size** | L |
| **Requirements** | FR-8, FR-9, FR-10, FR-11, FR-13, FR-22, FR-23 |
| **Verification** | Template graph has the specified shape; independent nodes report overlapping execution windows (parallelism proven by timestamp, not asserted); a join node remains pending until every inbound branch completes; retry honours backoff; timeout cancels and counts as an attempt; pause returns in-flight nodes to pending **without consuming an attempt**; **end-to-end: create → start → completed using simulated responses** |
| **Exit criteria** | A workflow runs start to finish unattended and offline |

### WP-5 — Governance

| | |
|---|---|
| **Deliverables** | Gate evaluator; approval service including the clarification variant; five policies |
| **Depends on** | WP-4 |
| **Size** | M |
| **Requirements** | FR-15 … FR-19, FR-34 |
| **Verification** | Human gate suspends its node and creates a pending approval; approval resumes execution; **unrelated branches continue while a node awaits approval**; rejection with requested changes re-executes the node with reviewer feedback present in its instructions and invalidates downstream work; policy gates resolve automatically with recorded evidence; clarification gate round-trips questions and answers |
| **Exit criteria** | Governance demonstrably blocks progression, not merely displays state |

> **Milestone M1 — Governed autonomy.** The platform executes a complete workflow with real approval
> gates, offline and unattended. This is the earliest point at which the core claim is demonstrable.

### WP-6 — Remaining agents, workspace, and real validation

| | |
|---|---|
| **Deliverables** | Architecture, Risk Assessment, Brownfield, and Generation agents; workspace manager for file operations, repository scanning, and seed copying; toolchain runner; Validation agent |
| **Depends on** | WP-5 |
| **Size** | L |
| **Requirements** | FR-3 … FR-7, FR-25 |
| **Verification** | Generated files land in the workspace in a compilable structure; **the real build succeeds and the real test run passes against generated output**; result parsing extracts accurate counts; absent toolchain degrades to static analysis without aborting the workflow; simulation catalog output is treated as tested source — it must genuinely compile |
| **Exit criteria** | Validation results are facts obtained from the toolchain, not assertions |

> **Milestone M2 — Verified generation.** The platform produces code that genuinely builds and
> passes tests, and gates progression on that outcome.

### WP-7 — Re-planning, rollback, and recovery

| | |
|---|---|
| **Deliverables** | Re-planning service; rollback service; startup recovery pass |
| **Depends on** | WP-6 |
| **Size** | M |
| **Requirements** | FR-12, FR-24, FR-9 |
| **Verification** | Downstream nodes are invalidated on upstream change; artifacts are superseded with lineage intact rather than deleted; pending approvals are voided while **resolved approvals are retained as history**; re-executed nodes request approval afresh; **the host is terminated mid-workflow and execution resumes to completion after restart** |
| **Exit criteria** | Workflow integrity survives failure, change, and process termination |

### WP-8 — Observability, metrics, and packaging

| | |
|---|---|
| **Deliverables** | Audit logger with event-bus publication; metrics service; timeline service; review package builder; abstraction DTOs finalized |
| **Depends on** | WP-7 |
| **Size** | M |
| **Requirements** | FR-14, FR-26 … FR-31 |
| **Verification** | All ten required metrics computed against a completed workflow; requirement coverage reconciles against artifact linkage; every artifact traces bidirectionally to its requirement; review package contains all thirteen required sections |
| **Exit criteria** | Traceability is queryable in both directions |

> **Milestone M3 — Core complete.** The entire platform is functional and verifiable through tests.
> No user interface exists yet.

### WP-9 — REST API

| | |
|---|---|
| **Deliverables** | Application wiring; all endpoint groups; request and response contracts with mapping; scenario catalog; review package markdown renderer; OpenAPI document |
| **Depends on** | WP-8 |
| **Size** | M |
| **Requirements** | FR-16, FR-31, NFR-8 |
| **Verification** | Smoke suite per §4.2; invalid state transitions return conflict; **workspace path traversal is rejected**; OpenAPI document lists every route |
| **Exit criteria** | Every platform capability is reachable over HTTP |

### WP-10 — Event stream

| | |
|---|---|
| **Deliverables** | Event broadcaster with sequencing, bounded replay buffer, per-client channels, and heartbeat; stream endpoint |
| **Depends on** | WP-9 |
| **Size** | S |
| **Requirements** | NFR-4 |
| **Verification** | Stream opens and delivers a heartbeat within the configured interval; node transitions appear live; a client that stops reading is dropped rather than applying backpressure to the engine; reconnection replays from the last received sequence |
| **Exit criteria** | Live observation without polling |

### WP-11 — Dashboard

| | |
|---|---|
| **Deliverables** | Stylesheet; utility, API, stream, markdown, and graph modules; home, workflow, and review views |
| **Depends on** | WP-10 |
| **Size** | L |
| **Requirements** | NFR-4, NFR-6, NFR-8 |
| **Verification** | Full walkthrough in a browser with no network access; graph updates patch individual nodes rather than re-rendering; approval and clarification forms resolve gates; artifact viewer renders markdown and code with lineage navigation; workspace browser displays generated files |
| **Exit criteria** | The platform is demonstrable to a non-technical observer |

> **Milestone M4 — Demonstrable platform.** Full capability observable and controllable in a browser.

### WP-12 — Brownfield seed and scenario completion

| | |
|---|---|
| **Deliverables** | Sample existing codebase that builds and tests independently; seed-copy path including reuse of a prior run's output; clarification round-trip refinement |
| **Depends on** | WP-11 |
| **Size** | M |
| **Requirements** | FR-32, FR-33, FR-34 |
| **Verification** | All three scenarios execute end to end; brownfield impact analysis precedes planning; existing tests continue passing alongside newly generated ones; ambiguous requirement converges through clarification |
| **Exit criteria** | All three required scenarios demonstrated |

### WP-13 — Live provider validation and documentation

| | |
|---|---|
| **Deliverables** | Demonstration guide; repository README; live-provider verification pass |
| **Depends on** | WP-12 |
| **Size** | S |
| **Requirements** | FR-32 … FR-34 |
| **Verification** | All three scenarios repeated against the live model with credentials present; a reader unfamiliar with the repository can run every scenario from the guide alone |
| **Exit criteria** | Reproducible by someone other than the author |

> **Milestone M5 — Delivery complete.**

---

## 3. Dependency Graph and Critical Path

```
WP-0 ─▶ WP-1 ─▶ WP-2 ─▶ WP-3 ─▶ WP-4★ ─▶ WP-5 ─▶ WP-6 ─▶ WP-7 ─▶ WP-8 ─▶ WP-9 ─▶ WP-10 ─▶ WP-11 ─▶ WP-12 ─▶ WP-13
 docs    domain    llm    agents   ENGINE   gates   agents   replan  observ    api    events    ui    scenarios  live
                                     │                +val                                                        
                                     │                                                                            
                                   M1 ──────── M2 ──────────────── M3 ──────────────────────── M4 ──────────── M5
```

The chain is largely linear because each package consumes the previous package's abstractions. Two
observations:

- **WP-4 is the critical package.** Schedule risk concentrates here. It is deliberately positioned
  early enough that difficulty surfaces while there is room to respond.
- **Parallelizable work**, if capacity allows: the simulation response catalog (WP-2) can be authored
  alongside WP-1; the dashboard stylesheet and graph module (WP-11) depend only on the contract
  shapes fixed in WP-9; the sample codebase (WP-12) is independent of everything and can be built at
  any point.

---

## 4. Verification Strategy

### 4.1 Automated

| Level | Scope | Applied at |
|---|---|---|
| Unit | Extractor edge cases, retry policy arithmetic, graph layering, policy evaluation | WP-1 … WP-8 |
| Component | Engine behaviour with a scripted provider: readiness, parallelism, joins, retries, timeouts, gates, invalidation | WP-4, WP-5, WP-7 |
| Integration | Full workflow against the simulation provider, ending in a genuine build and test of generated output | WP-6 onward |
| Recovery | Host terminated mid-workflow; new instance resumes to completion | WP-7 |

Executed continuously: `dotnet build` and `dotnet test` at the solution root after each package.

### 4.2 API smoke sequence

Executed after WP-9, no credentials required:

| Check | Expected |
|---|---|
| Health | Database reachable; provider mode reported |
| Scenario list | Three descriptors with prefilled requirement text |
| Create workflow | `201` with location header |
| Detail | Nodes, edges, and phases present |
| Pause, then pause again | `202`, then `409` |
| Resume | `202`; execution continues |
| Pending approvals | Gate appears when reached |
| Resolve gate | `200`; workflow proceeds |
| Artifact retrieval | Content and lineage returned |
| Workspace traversal attempt | `400` — containment enforced |
| Metrics | All ten metrics present |
| Review package | Markdown downloads |
| Event stream | Heartbeat within interval; live transitions observed |

### 4.3 Scenario walkthroughs

Executed against the simulation provider first, then repeated against the live model.

**Greenfield.** Submit the requirement; observe the graph populate and nodes execute concurrently;
approve the plan gate; approve the architecture gate; observe generation nodes running in parallel;
observe validation invoke the real toolchain; inspect generated source in the workspace browser;
confirm the review package assembles. *Independent cross-check: run the test suite manually inside
the generated workspace — it must pass outside the platform.*

**Brownfield.** Seed from the sample codebase; confirm impact analysis, dependency findings, and
risk report are produced **before** planning; approve the risk-acceptance gate; confirm pre-existing
tests still pass alongside new ones. Repeat once seeding from the greenfield run's output — the
platform enhancing software it previously wrote.

**Ambiguous.** Submit the vague requirement; confirm the workflow suspends at a clarification gate
with substantive questions; answer through the dashboard; confirm convergence to a specification and
normal progression; confirm the exchange and any documented assumptions appear in the review package.

### 4.4 Resilience walkthroughs

| Exercise | Expected |
|---|---|
| Safe stop mid-execution, then resume | In-flight nodes return to pending with retry budget intact; execution resumes |
| Browser closed and reopened mid-run | State fully reconstructed from the API; stream reattaches |
| **Host terminated and restarted mid-run** | Stranded nodes recovered; workflow resumes to completion |
| Cancel | Workflow terminal; controls disabled |
| Credentials removed | Simulation provider engages; workflow completes; fallback recorded |

---

## 5. Risk Management

Carried forward from the Requirement Understanding register, with the mitigating package identified.

| Risk | Mitigation | Package | Residual |
|---|---|---|---|
| R-1 Effort misallocated away from the engine | Engine built and proven at WP-4/M1, before agent depth | WP-4 | Low |
| R-2 Demonstration domain leaking into platform code | Scenario data isolated in seed files; substitution test at WP-13 | WP-12 | Low |
| R-3 Non-deterministic model output breaking parsing | Two-tier retry; schema validation; deterministic simulation mode | WP-2, WP-3 | Low |
| R-4 Database write contention under parallel execution | Write-ahead logging; short-lived contexts; busy timeout; bounded concurrency | WP-1 | Medium — verify under load at WP-4 |
| R-5 Governance visible but not enforced | Gates evaluated in the execution path; verified by blocking tests | WP-5 | Low |
| R-6 Invalidation cascading endlessly or losing history | Bounded traversal; approvals voided rather than deleted | WP-7 | Low |
| R-7 Untrusted generated content escaping containment | Canonicalized path checks; escape-first rendering | WP-9, WP-11 | Low |
| R-8 Unbounded cost | Capped retries; truncated context; token accounting surfaced | WP-2, WP-8 | Low |
| **New** — SDK surface differs from expectation | Isolated to the single live-provider adapter; verified at WP-2 while cheap to change | WP-2 | Low |
| **New** — Simulated generation output fails to compile | Catalog files treated as source code and covered by the WP-6 integration test | WP-6 | Medium |

---

## 6. Definition of Done

The implementation is complete when every statement below is demonstrable:

- [ ] A natural-language requirement produces a persisted workflow with an inspectable dependency graph
- [ ] Independent nodes execute concurrently, evidenced by overlapping execution windows
- [ ] A join node demonstrably waits for every inbound branch
- [ ] Execution suspends at human approval gates and resumes only on explicit decision, recorded with identity, timestamp, and comment
- [ ] Rejection with feedback re-executes the node with that feedback and invalidates downstream work, which re-requests approval
- [ ] Generated code genuinely compiles and passes tests via the real toolchain, and that result gates progression
- [ ] The host can be terminated mid-workflow and resumes from persisted state on restart
- [ ] Any artifact traces back to its originating requirement, and any requirement forward to its artifacts
- [ ] All ten engineering intelligence metrics are reported
- [ ] The final engineering review package assembles automatically
- [ ] All three scenarios — greenfield, brownfield, ambiguous — execute end to end
- [ ] The platform runs offline without credentials, exercising identical orchestration paths
- [ ] Substituting an unrelated requirement requires no platform code change
- [ ] Solution builds clean; all automated tests pass
- [ ] Documentation set complete: understanding, specification, plan, demonstration guide, README

---

## 7. Immediate Next Action

Begin **WP-1**: remove the placeholder class, create the test project and register it in the
solution, implement the domain entities and enumerations, the database context with factory
registration, the initializer, configuration options, and the service-registration extension —
then prove it with per-entity round-trip tests against a temporary file-backed database.
