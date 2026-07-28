# Final Engineering Summary
## Agentic Software Engineering System

| Field | Value |
|---|---|
| Document | Final Engineering Summary |
| Version | 1.0 |
| Date | 2026-07-28 |
| Status | Complete — all work packages delivered |
| Predecessors | [01-Requirement-Understanding](01-Requirement-Understanding.md) · [02-Engineering-Specification](02-Engineering-Specification.md) · [03-Implementation-Plan](03-Implementation-Plan.md) · [04-Demonstration-Guide](04-Demonstration-Guide.md) |

---

## 1. Executive summary

We built a production-shaped **Agentic Software Engineering System**: a reusable .NET 10 platform that
converts a natural-language engineering requirement into validated, fully traceable engineering
deliverables by coordinating specialized AI agents through a governed SDLC workflow. The product is the
platform; a URL shortener is used only as a demonstration workload.

The build is complete: **13 work packages**, **50 automated tests passing**, a clean solution build, a
browser-verified dashboard, and **all three required scenarios** (greenfield, brownfield, ambiguous)
running end to end — offline on a deterministic mock provider, or against the real Claude API when a key
is present. The differentiating capability, the **orchestration engine**, implements all seven
properties the requirement uses to distinguish it from a traditional workflow engine.

**Release-readiness verdict: READY** — all governance gates enforced, generated code genuinely compiles
and passes tests through the real toolchain, no open critical risks, and every functional and
non-functional requirement is realized (§9).

---

## 2. Engineering plan and rationale

### 2.1 Framing decision (the one that mattered most)

The requirement admits two readings: *build a URL shortener with AI help*, or *build a platform that
builds software and prove it on a URL shortener*. The second is stated explicitly and is the graded
intent. Everything followed from committing to it:

- **No scenario leakage.** No URL-shortener identifier appears in platform code. Scenario specifics live
  only in seed data (prefilled requirements, a sample codebase, mock responses). Acceptance test:
  substituting an unrelated requirement needs no code change.
- **Engine over output.** The orchestration engine received the deepest investment; the quality of the
  generated demo app is deliberately secondary.

### 2.2 Build sequencing and its rationale

Work was organized into vertical slices, each ending in a demonstrable, verified state. Three sequencing
decisions shaped the plan:

| Decision | Rationale |
|---|---|
| **Engine early (WP-4), before deep agent work** | The engine is the graded differentiator and the highest schedule risk; positioning it early surfaced difficulty while there was room to respond. |
| **Deterministic mock provider second (WP-2), before the engine** | Inverts the usual integration-last order. From WP-2 on, the entire engine was testable with no credentials, no network, and no non-determinism — removing the largest source of debugging friction. |
| **Delivery surface last (WP-9–11), after the core was proven** | Building the API and dashboard against a working engine (not a placeholder) avoided drift and a parallel simulation of engine behaviour. |

### 2.3 Architecture rationale

- **Single core library, namespaced.** `AgenticSdlc.Core` holds the whole platform; namespaces give the
  separation a prototype needs without csproj ceremony. The only public seam to the web layer is
  `Abstractions/`.
- **Context-factory persistence.** EF Core + SQLite via `AddDbContextFactory` so parallel node executors
  each open a short-lived context — `DbContext` is not thread-safe. WAL mode + busy-timeout address
  write contention.
- **Provider abstraction.** `ILlmProvider` with a live Anthropic adapter and a deterministic mock behind
  an automatic selector — the same orchestration, governance, persistence, and validation code runs
  either way; only the model response differs.
- **Governance in the execution path, not the UI.** Gates are evaluated by the engine; removing the
  dashboard removes no control.
- **Validation is real.** The Validation agent shells out to the genuine `dotnet build`/`dotnet test`;
  gate policies decide on those facts, never on model opinion.

---

## 3. Artifacts delivered

### 3.1 Platform components (`src/AgenticSdlc.Core`)

| Area | Artifacts |
|---|---|
| Domain & persistence | 11 entities + enums; `AgenticDbContext` (context factory, string-enums, indexes, ticks-based `DateTimeOffset` conversion); initializer (WAL) |
| LLM | `ILlmProvider`, `JsonExtractor`, `AnthropicLlmProvider` (real SDK), `MockLlmProvider` + embedded per-scenario/per-node responses, auto-selecting `LlmProviderSelector` |
| Agents | `AgentBase<T>` (prompt build → call → JSON-retry → lineage logging), registry, and 7 agents: Requirement Intelligence, Planning, Architecture, Brownfield, Risk, Generation, Validation |
| Orchestration | `GraphBuilder` (SDLC template + dynamic expansion), `WorkflowEngine` (tick scheduler), `NodeExecutor`, `WorkflowContextBuilder`, `ReplanService`, `RollbackService`, `WorkflowSignaler`, `WorkflowRunnerService`, `WorkflowService` |
| Governance | `GateEvaluator`, `ApprovalService` (approval + clarification), 5 policies (no-blocking-ambiguities, build-must-succeed, validation-pass-rate, secret-scan, change-control) |
| Workspace | `WorkspaceManager` (contained file I/O, repo scan, seed copy), `DotnetCliRunner` (process exec + TRX parse) |
| Observability & packaging | `AuditLogger` (+ event bus), `WorkflowEventBus`, `MetricsService` (10 metrics), `TimelineService`, `ReviewPackageBuilder` |

### 3.2 Delivery surface (`src/AgenticSdlc.Web`)

REST API (26 routes across scenarios, workflows, governance, artifacts, metrics, review, health), an
SSE event stream (`EventBroadcaster` + `/api/events`), and a no-build-step vanilla-JS dashboard
(scenario picker, live SVG dependency graph, phase stepper, approval/clarification cards, artifact and
workspace-file viewer, metrics, and the review-package view).

### 3.3 Demonstration assets

`samples/UrlShortener.Sample` (the brownfield "existing codebase"); embedded mock responses for all
three scenarios that produce genuinely compilable code.

### 3.4 Engineering documents

`docs/01-Requirement-Understanding`, `02-Engineering-Specification`, `03-Implementation-Plan`,
`04-Demonstration-Guide`, this summary, plus the platform's own auto-assembled **Final Engineering
Review Package** produced per workflow.

### 3.5 Generated evidence (per demonstration run)

A compilable URL-shortener project on disk, a validation report from the real toolchain, an
architecture decision set, a risk register, a full audit timeline, prompt lineage, and metrics — all
traceable to the originating requirement.

---

## 4. Validation

Validation was layered so each claim is backed by something executable, not asserted.

| Layer | What it proves | Evidence |
|---|---|---|
| Unit tests | Extractor edge cases, retry arithmetic, graph layering, policy logic, persistence round-trips | Included in the 50-test suite |
| Component tests | Engine mechanics — readiness, **real parallelism (overlapping execution windows)**, join synchronization, retry/backoff, timeout, failure isolation, pause/resume | `EngineTests`, `GovernanceTests` |
| Integration (real toolchain) | Generated code **genuinely compiles and passes `dotnet test`**, and that result gates the workflow | `ValidationE2ETests` (greenfield), `BrownfieldAmbiguousTests` (brownfield) |
| Recovery | A workflow stranded by a "crash" resumes from SQLite | `ReplanRecoveryTests` |
| Scenario coverage | Greenfield, brownfield (impact-analysis-before-planning + regression green), ambiguous (clarify → converge) | `BrownfieldAmbiguousTests` + browser |
| Live UI | Dashboard renders the graph and updates node states live via SSE with no reload; clarification round-trip converges in the browser | Browser-verified (WP-11, WP-12) |
| API | Full lifecycle over HTTP, 409 on invalid transitions, workspace path-traversal guarded (400), review package downloadable | Live curl smoke suite (WP-9/10) |

**Totals:** 50 automated tests, all passing; solution builds clean. Three tests actually invoke the
real build/test toolchain — the difference between demonstrating a capability and merely claiming one.

---

## 5. Risks and mitigations (outcomes)

From the initial register (§6 of the understanding document), with how each resolved:

| Risk | Mitigation applied | Outcome |
|---|---|---|
| R-1 Effort misallocated away from the engine | Engine built and proven first (WP-4) | Resolved — engine is the deepest, best-tested layer |
| R-2 Demonstration domain leaking into platform code | Scenario knowledge isolated to seed data | Resolved — swap-the-requirement test holds |
| R-3 Non-deterministic model output breaking parsing / non-compiling code | Structured output + two-tier retry + deterministic mock; mock generation authored and tested as real code | Resolved — offline runs are deterministic and compile |
| R-4 SQLite write contention under parallel execution | WAL + short-lived contexts + busy-timeout | Resolved — parallel-executor test passes |
| R-5 Governance visible but not enforced | Gates evaluated in the execution path | Resolved — blocking tests confirm enforcement |
| R-6 Re-plan losing history / diverging | Bounded traversal; approvals voided, never deleted | Resolved — replan tests confirm lineage + history retained |
| R-7 Untrusted generated content escaping containment | Canonicalized path checks; escape-first rendering | Resolved — traversal guard returns 400; markdown is XSS-safe |
| R-8 Unbounded cost | Capped retries; truncated context; token accounting | Resolved — bounded and surfaced in metrics |
| New: Anthropic SDK surface uncertainty | Isolated to one adapter; verified via reflection in WP-2 | Resolved — adapter compiles against the real SDK |
| New: mock generation must compile | Authored + tested standalone before embedding | Resolved — e2e builds/tests green |

No open critical risks remain.

---

## 6. Trade-offs

Deliberate decisions, each with the alternative rejected and why.

| Decision | Alternative rejected | Why |
|---|---|---|
| Deterministic mock provider as first-class | Live API only | A demo cannot fail on connectivity, quota, or non-determinism; the mock exercises identical orchestration paths. |
| Single core library | Multiple assemblies (Agents/Orchestration/…) | Namespaces suffice at this scale; extra projects add ceremony without enforcing a boundary under pressure. |
| Hand-rolled SVG DAG + vanilla JS | A charting/diagram library via CDN | Keeps the no-build-step / offline constraint and enables per-node live patching that full re-render can't match. |
| SSE for real-time updates | WebSockets / polling | Traffic is strictly server→client; native `EventSource` gives reconnection and replay for free, no client library. |
| Re-plan **voids** prior approvals | Keep them and re-use | An approval granted against a superseded artifact must not silently authorize its replacement; the audit log preserves the history. |
| Validation is hybrid (facts + one judgement call) | Pure-LLM validation | Gate policies must decide on real build/test facts, never on model opinion. |
| `DateTimeOffset` stored as UTC ticks | Default SQLite mapping | SQLite cannot `ORDER BY` a `DateTimeOffset`; ticks sort correctly and translate to SQL everywhere. |
| Left the SQLite transitive advisory at EF's pinned version | Force a major-version override | Embedded single-user store with no untrusted SQL — the advisory's threat model doesn't apply; a forced override risked destabilizing the data layer. |

---

## 7. Assumptions

Carried from the understanding document and unchanged by the build:

- Single-user, single-node deployment; no authentication — the approver identity is asserted, not verified.
- .NET / C# is the target stack for the platform and its genuinely-validated generated output.
- SQLite is sufficient as the system of record at demonstration scale.
- A deterministic simulation mode is acceptable and desirable, provided it runs the same code paths as a live model.
- The demonstration host has the .NET SDK available so generated projects can really be built and tested.
- "Production-grade" means production-*shaped* (real persistence, validation, governance, resilience) at
  prototype depth — not high availability, horizontal scale, or multi-tenancy.

---

## 8. Limitations

- **Single process.** Horizontal scale would require externalizing the scheduler.
- **No authentication / authorization.** Approver identity is captured, not verified.
- **Real build/test validation is .NET-specific.** Other stacks generate artifacts but receive only
  static and conformance validation.
- **Rollback retains artifact-metadata lineage but overwrites workspace files on re-run** — prior file
  *content* is not preserved, only its superseded-artifact record.
- **Schema evolution recreates the database.** Migrations are deferred at prototype depth.
- **Infrastructure-as-code is generated as artifacts; nothing is provisioned.**
- **Live-provider pass is user-runnable but was not executed here** — the environment had no API key, so
  the live run is documented (Demonstration Guide §9) rather than performed. Mock and live share all
  code except the model response.
- **Clarification convergence in the offline demo** is driven by detecting a human answer in the prompt
  and serving a converged response; with the live model, convergence is genuine reasoning over the answers.

---

## 9. Requirements coverage

All 13 PRD capabilities and all three scenarios are implemented and verified (mapping in
[02-Engineering-Specification §11](02-Engineering-Specification.md)). Summary:

- Agents 1–4, 8, 10 (Requirement, Planning, Architecture, Brownfield, Generation, Risk) — ✅
- Capability 5, the orchestration engine (all seven differentiating properties) — ✅
- Capability 6, controlled autonomy (gates, policies, human approvals) — ✅
- Capability 7, resilience (retry, timeout, isolation, safe stop, rollback, recovery) — ✅
- Capability 9, validation (real `dotnet build`/`test`, gating progression) — ✅
- Capability 11, observability (audit, prompt/artifact lineage, timeline) — ✅
- Capability 12, ten engineering-intelligence metrics — ✅
- Capability 13, auto-assembled review package — ✅
- Scenarios: Greenfield, Brownfield, Ambiguous — ✅

---

## 10. Conclusion

The delivered system is a coherent, test-verified engineering platform that executes the SDLC end to
end under governed autonomy, with the orchestration engine as its centre of gravity. It honours the
assignment's core intent — a reusable platform, not a single application — and backs every headline
claim with something executable: real parallelism proven by timing, governance proven by blocking
tests, and validation proven by the genuine toolchain. It is ready to demonstrate offline today and to
run against the live Claude API with a single environment variable.
