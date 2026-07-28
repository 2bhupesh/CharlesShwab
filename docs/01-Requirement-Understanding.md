# Requirement Understanding Document
## Agentic Software Engineering System

| Field | Value |
|---|---|
| Document | Requirement Interpretation & Engineering Problem Definition |
| Version | 1.0 |
| Date | 2026-07-28 |
| Status | For review — precedes architecture and implementation |
| Source | Product Requirement: "Agentic Software Engineering System" |

> **Note on format.** This document is deliberately structured the way the platform's own
> **Requirement Intelligence Agent** structures its output — intent, functional and non-functional
> requirements, assumptions, ambiguities, risks, open questions. It is both the analysis of the
> assignment *and* a worked example of the artifact the platform produces.

---

## 1. Intent Extraction

### 1.1 What is being asked for

The requirement asks for a **reusable engineering platform**, not an application. Read literally:

> "The objective is not to build a specific software application, but to build a reusable
> engineering platform capable of solving diverse software engineering problems."

The deliverable is a system that takes a natural-language engineering requirement as *input* and
produces validated, traceable engineering artifacts as *output*, by coordinating multiple
specialized AI agents through a governed SDLC workflow.

### 1.2 The critical distinction

There are two candidate readings of this requirement, and choosing the wrong one fails the brief:

| Reading | What gets built | Verdict |
|---|---|---|
| ❌ "Build a URL shortener using AI agents" | A URL shortener; agents are scaffolding | **Wrong.** The URL shortener is explicitly labelled a *demonstration scenario*. |
| ✅ "Build a platform that can build software; prove it by having it build a URL shortener" | An orchestration platform; the URL shortener is generated output | **Correct.** Stated twice in the requirement, and reinforced in the closing paragraph. |

The closing paragraph of the requirement removes all doubt:

> "The product is the Agentic Software Engineering System, while the URL Shortener is simply one
> workload used to demonstrate the platform's capabilities. The orchestration engine is the central
> differentiator, with the engineering artifacts serving as evidence that the platform can execute
> the SDLC end to end."

**Consequence for design:** nothing about the URL shortener may be hard-coded into the platform.
No URL-shortener-specific agent, entity, prompt, or code path. The only place the URL shortener may
appear is in *scenario seed data* (prefilled requirement text and a sample brownfield codebase),
which is data, not platform logic. A useful acceptance test: *swap the requirement text for
"build a rate limiter" and the platform must still function without a code change.*

### 1.3 The central differentiator

The requirement names the orchestration engine as "the core capability" and "Primary
Differentiator", and defines it by contrast:

> "Unlike traditional workflow engines, the orchestration engine shall provide: Stateful Execution,
> Dependency Graph Execution, Parallel Execution, Synchronization Points, Dynamic Re-planning,
> Context Propagation, Decision Lineage."

This is the discriminating requirement. A sequential pipeline of LLM calls — prompt 1 feeds prompt
2 feeds prompt 3 — satisfies none of the seven properties above and would not meet the brief no
matter how good the generated code is. The engineering effort must be weighted accordingly.

### 1.4 One-sentence problem statement

> Build a stateful, graph-driven multi-agent orchestration platform that converts a natural-language
> engineering requirement into validated, fully traceable engineering artifacts, executing SDLC
> phases in parallel where dependencies permit, pausing for human approval at high-impact decision
> points, recovering from failure without losing workflow integrity, and emitting a complete audit
> trail linking every artifact back to its originating requirement.

---

## 2. Functional Requirements

Derived from the 13 numbered capabilities in the requirement. IDs are referenced by the
architecture and implementation plans.

### 2.1 Agents

| ID | Requirement | Acceptance signal |
|---|---|---|
| FR-1 | A **Requirement Intelligence** agent shall extract intent, identify functional and non-functional requirements, detect ambiguity, generate assumptions, and raise clarification questions. | Produces an Engineering Specification artifact with FR/NFR/assumption/open-question items, each individually addressable by ID. |
| FR-2 | An **Engineering Planning** agent shall decompose requirements into a work breakdown structure with an explicit dependency graph, sequencing, parallelism, milestones, and agent assignment. | Output is consumed directly by the orchestration engine to create executable graph nodes — not rendered as a static task list. |
| FR-3 | An **Architecture Reasoning** agent shall select an architecture, record ADRs, decompose services, design APIs and data models, and analyze scalability, reliability, and security. | Produces ADR set, component diagram, and service contracts; each ADR becomes a queryable decision record. |
| FR-4 | A **Brownfield Reasoning** agent shall analyze an existing codebase for module/service/API dependencies, data and event flow, configuration, and change impact. | Produces impact assessment, dependency graph, risk report, and refactoring recommendations *before* planning commits to an approach. |
| FR-5 | An **Engineering Generation** agent shall produce source code, APIs, OpenAPI specifications, database scripts, IaC, unit and integration tests, documentation, and release notes. | Artifacts are real files on disk in a compilable project structure, not prose describing code. |
| FR-6 | A **Validation** agent shall perform static analysis, build validation, test execution, security validation, and architecture/API/documentation conformance checks. | Build and test validation invoke the real toolchain and parse real results; outcomes gate workflow progression. |
| FR-7 | A **Risk Assessment** agent shall continuously identify technical, architectural, security, operational, performance, and reliability risks with mitigation, validation, monitoring, and rollback recommendations. | Risk items are persisted, severity-ranked, and surfaced in the final review package. |

### 2.2 Orchestration engine

| ID | Requirement | Acceptance signal |
|---|---|---|
| FR-8 | Execution shall be driven by an **explicit dependency graph**, not a sequential task chain. | The graph is inspectable and renderable; node readiness is computed from in-edges. |
| FR-9 | Agents shall maintain **persistent execution state** surviving process restart. | Kill the host mid-workflow; on restart the workflow resumes from its persisted state. |
| FR-10 | **Independent nodes shall execute concurrently**, bounded by a configured limit. | Observable: sibling nodes report overlapping start/end timestamps. |
| FR-11 | **Synchronization points** shall join parallel branches before downstream execution. | A join node becomes ready only when every inbound branch has completed. |
| FR-12 | When an upstream artifact changes, downstream execution shall be **automatically re-evaluated and re-planned**, preserving governance. | Downstream nodes are invalidated and re-run; their approvals are re-requested rather than inherited. |
| FR-13 | Engineering **context — decisions, assumptions, artifacts — shall propagate** to all downstream agents. | Every agent invocation receives the accumulated upstream context; a decision made in architecture is visible to generation. |
| FR-14 | Every engineering decision shall be recorded with **rationale and linkage to its originating requirement**. | Given any decision, the requirement that motivated it is retrievable; given any requirement, its downstream decisions are retrievable. |

### 2.3 Governance

| ID | Requirement | Acceptance signal |
|---|---|---|
| FR-15 | The engine shall enforce **entry gates, exit gates, and quality gates** on workflow nodes. | A node cannot start or complete without its gates evaluating successfully. |
| FR-16 | **Human approval gates** shall suspend execution until an explicit human decision is recorded. | Workflow halts at the gate; approval or rejection is captured with approver identity, timestamp, and comment. |
| FR-17 | **High-impact activities** — architecture decisions, schema modifications, infrastructure changes, production release — shall require explicit human approval. | These specific node types carry mandatory human gates that cannot be auto-satisfied. |
| FR-18 | **Security, compliance, and change-control policies** shall be evaluated automatically. | Policy evaluation produces pass/fail with evidence; failure blocks progression. |
| FR-19 | Rejection shall be actionable, not merely terminal. | A reviewer's rejection comment is fed back into the agent's next execution, and downstream work is invalidated. |

### 2.4 Resilience

| ID | Requirement | Acceptance signal |
|---|---|---|
| FR-20 | **Retry policies** shall govern transient failure, with bounded attempts. | Failed node retries with backoff; attempt count is visible and capped. |
| FR-21 | **Timeouts** shall bound every agent execution. | A hung agent is cancelled and treated as a failed attempt. |
| FR-22 | **Failure isolation** — a failed node shall not corrupt workflow integrity or halt unrelated branches. | Parallel siblings continue; only genuine dependents are blocked. |
| FR-23 | **Safe stop** shall suspend a workflow without data loss or partial-state corruption. | In-flight work is cancelled cleanly; the workflow resumes from a consistent state. |
| FR-24 | **Rollback** shall revert a node's effects and re-evaluate downstream work. | Superseded artifacts are retained for lineage rather than deleted. |
| FR-25 | **Fallback strategies** shall degrade gracefully when a dependency is unavailable. | E.g. absent build toolchain or LLM provider does not crash the workflow; degradation is recorded. |

### 2.5 Observability, metrics, and reporting

| ID | Requirement | Acceptance signal |
|---|---|---|
| FR-26 | The platform shall record **agent execution history and prompt lineage** (prompts and responses). | Every LLM invocation is retrievable with its input, output, token usage, and duration. |
| FR-27 | The platform shall record **artifact lineage**, including versions and supersession. | Given an artifact, its producing node, source requirements, and superseded predecessors are retrievable. |
| FR-28 | The platform shall expose a **workflow timeline, state transitions, audit log, and approval history**. | Append-only audit event stream covering every state change and human action. |
| FR-29 | **Every artifact shall be traceable to its originating requirement.** | Bidirectional traversal: requirement → artifacts, artifact → requirement. |
| FR-30 | The platform shall compute **engineering intelligence metrics**: workflow and agent success rate, retry frequency, rollback frequency, MTTR, workflow latency, human approval time, validation pass rate, requirement coverage, test coverage. | All ten metrics are queryable per workflow and platform-wide. |
| FR-31 | The platform shall automatically assemble a **Final Engineering Review Package** containing requirement interpretation, plan, architecture rationale, artifacts, test and validation results, risk assessment, trade-offs, assumptions, limitations, approval history, audit trail, and release readiness. | Single generated document assembled without manual authoring. |

### 2.6 Demonstration scenarios

| ID | Requirement | Acceptance signal |
|---|---|---|
| FR-32 | **Greenfield** — build a new system from requirements. | Full SDLC executes; generated project compiles and its tests pass. |
| FR-33 | **Brownfield** — enhance an existing codebase with impact analysis. | Impact analysis precedes planning; existing tests continue to pass alongside new ones. |
| FR-34 | **Ambiguous requirement** — interpret an incomplete requirement and converge on a solution. | Platform detects insufficiency, asks clarifying questions, incorporates human answers, and converges — or proceeds on explicitly documented assumptions. |

---

## 3. Non-Functional Requirements

| ID | Category | Requirement |
|---|---|---|
| NFR-1 | Reusability | No demonstration-scenario knowledge in platform code. Scenarios are data. |
| NFR-2 | Auditability | Every state transition, decision, approval, and LLM call is persisted append-only. Nothing that matters exists only in memory. |
| NFR-3 | Durability | Workflow state survives process restart. SQLite is the system of record. |
| NFR-4 | Observability | A human can watch a workflow progress live and understand what each agent is doing and why. |
| NFR-5 | Determinism of demonstration | The full platform must be demonstrable without network access or API credentials, so a demo cannot fail on connectivity or quota. |
| NFR-6 | Operational simplicity | `dotnet run` starts everything. No npm, no build chain, no external services, no container orchestration. |
| NFR-7 | Concurrency safety | Parallel agent execution must not corrupt shared state; database access must be safe under concurrent writers. |
| NFR-8 | Security | Generated content is untrusted: no path traversal out of workspaces, no HTML injection in rendered artifacts, no secrets in configuration files. |
| NFR-9 | Extensibility | Adding a new agent requires implementing one interface and registering it — no changes to the engine. |
| NFR-10 | Cost control | Bounded LLM retries and context size; token usage tracked and reported. |
| NFR-11 | Maintainability | Prototype-grade clarity: plain dependency injection, no framework ceremony, readable over clever. |

---

## 4. Ambiguity Analysis

Ambiguities detected in the requirement, and the interpretation adopted. Each is a decision that
should be confirmed rather than silently assumed.

| # | Ambiguity | Interpretation adopted | Rationale |
|---|---|---|---|
| A-1 | "Production-grade" and "production-ready" applied to a prototype deliverable. | Read as *production-shaped*: real persistence, real validation, real governance, real error handling — at prototype depth and scale. Not: high availability, horizontal scale, authentication, multi-tenancy. | The requirement also calls the deliverable a "prototype" and asks it to "demonstrate" capabilities. Depth of *coverage* is prioritized over depth of *hardening*. |
| A-2 | Scope of "Infrastructure as Code" generation. | The Generation agent can emit IaC artifacts (e.g. Dockerfile, compose, workflow YAML) as text artifacts; the platform does not provision infrastructure. | Provisioning is out of scope and carries irreversible side effects that governance would need to gate for real. |
| A-3 | "Continuous validation" — how continuous? | Validation runs as graph nodes triggered by dependency completion, and re-runs automatically when re-planning invalidates upstream work. Not a background polling loop. | Matches the graph-driven execution model; "continuous" is satisfied by automatic re-validation on change. |
| A-4 | MTTR (mean time to recovery) is listed as a metric, but "recovery" is undefined. | Measured as elapsed time from a node entering a failed state to the same node reaching success via retry, rollback, or re-plan. | The only recovery event the platform can observe directly. |
| A-5 | "Compliance Review" phase with no compliance regime named. | Implemented as a pluggable policy evaluation step (secret scanning, change-control assertion, architecture conformance) rather than a specific regulatory standard. | No regime specified; a policy plug-in point is the honest generalization. |
| A-6 | Brownfield source — which existing codebase? | Two supported inputs: a sample codebase included with the platform, or the output workspace of a previous greenfield run by the platform itself. | The second is the stronger demonstration: the platform enhancing software it previously wrote, with impact analysis. |
| A-7 | Whether generated code must actually compile and pass tests. | Yes. Validation invokes the real build and test toolchain. | "Validation results determine workflow progression" is meaningless if validation is simulated. This is the difference between a demo and a claim. |
| A-8 | "Human-in-the-loop" — what interface? | Web dashboard with approval controls plus equivalent REST endpoints. | Approval must be observable and demonstrable; a console prompt would not evidence "governance" convincingly. |

---

## 5. Assumptions

Assumptions made in the absence of explicit direction. Each is falsifiable and cheap to revise.

| # | Assumption | Impact if wrong |
|---|---|---|
| AS-1 | Single-user, single-node deployment. No authentication; approver identity is a supplied name, not a verified principal. | Adding auth is additive; the approval record already carries an identity field. |
| AS-2 | .NET / C# is the target stack for both the platform and its generated output, given the existing scaffold. | The Generation agent is language-agnostic by design; only validation tooling is stack-specific. |
| AS-3 | SQLite is sufficient as the system of record at demonstration scale. | Swapping providers is an EF Core configuration change. |
| AS-4 | Evaluation will focus on the orchestration engine, governance, and traceability rather than the sophistication of the generated URL shortener. | Directly follows the requirement's own emphasis; effort is allocated accordingly. |
| AS-5 | A deterministic simulation mode is acceptable and desirable for demonstration, provided the same code paths execute as with a live model. | Without it, any demo depends on network, credentials, quota, and model non-determinism. |
| AS-6 | The demonstration environment has the .NET SDK available so generated projects can genuinely be built and tested. | Absence degrades validation to static checks only — handled as a documented fallback (FR-25). |
| AS-7 | Human approval is expected within a session; workflows do not need multi-day suspension semantics or notification delivery. | State is persisted, so long suspension already works; only proactive notification is absent. |

---

## 6. Risk Register (initial)

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R-1 | **Effort misallocation** — over-investing in generated output quality at the expense of the orchestration engine, which is the graded differentiator. | High | Build the engine first and prove it end-to-end before deepening agent output. |
| R-2 | **Scenario leakage** — URL-shortener specifics contaminating platform code, breaking the reusability claim. | High | Scenarios are data files. Acceptance test: substitute a different requirement and verify no code change is needed. |
| R-3 | **Non-deterministic LLM output** breaking parsing or producing non-compiling code mid-demo. | High | Structured output with schema validation, bounded reparse retries, node-level retry, plus a deterministic simulation mode. |
| R-4 | **Concurrent write contention** on SQLite under parallel node execution. | Medium | Write-ahead logging, short-lived database contexts per executor, busy-timeout, bounded concurrency. |
| R-5 | **Governance theatre** — approval gates that are visible but not actually enforced. | Medium | Gates are evaluated in the execution path, not the presentation layer; rejection genuinely invalidates downstream work. |
| R-6 | **Re-planning divergence** — invalidation cascading endlessly or losing approval history. | Medium | Bounded invalidation traversal; approvals are voided, never deleted; re-run nodes request fresh approval. |
| R-7 | **Untrusted generated content** — model-authored file paths or markdown escaping the workspace or injecting into the dashboard. | Medium | Canonicalized path containment checks; escape-first rendering with no raw HTML passthrough. |
| R-8 | **Unbounded cost** from retries and large context windows. | Low | Capped retries, truncated context per artifact, token accounting surfaced in metrics. |

---

## 7. Explicitly Out of Scope

Stated so that absence reads as a decision rather than an omission:

- Authentication, authorization, and multi-tenancy
- Horizontal scale, high availability, distributed execution
- Actual infrastructure provisioning or deployment to any environment
- Version control integration (commits, branches, pull requests)
- Support for generating non-.NET stacks with real build validation (artifacts can be generated; only .NET output is genuinely compiled and tested)
- Long-lived approval notification delivery (email, chat)
- Fine-tuning, model training, or model evaluation

---

## 8. Open Questions

Questions that would change the design if answered differently. Proceeding on the stated
assumption; each is cheap to revisit.

| # | Question | Proceeding assumption |
|---|---|---|
| OQ-1 | Is the evaluation primarily a live demonstration, or a code and document review? | Both are served: the dashboard covers live demonstration, the review package and this document cover written review. |
| OQ-2 | Should the platform support resuming a workflow whose requirement text is edited mid-flight? | Not in v1. Re-planning is triggered by artifact change and rejection; requirement editing would use the same mechanism and can be added later. |
| OQ-3 | Is a specific compliance regime expected (SOC 2, PCI, internal standard)? | No. Compliance is a pluggable policy point. |
| OQ-4 | Is generated output expected for stacks beyond .NET? | No. Generation is stack-agnostic; only .NET validation is real. |

---

## 9. Success Criteria

The build is complete when the following are demonstrable end to end:

1. A natural-language requirement entered in the browser produces a persisted workflow with a
   visible dependency graph.
2. Independent nodes are observed executing **concurrently**, and a join node visibly waits for all
   inbound branches.
3. The workflow **halts** at a human approval gate and only proceeds after an explicit decision is
   recorded with identity, timestamp, and comment.
4. A rejection with feedback causes the node to re-execute with that feedback and **invalidates
   downstream work**, which then re-requests approval.
5. Generated code exists as real files and **genuinely compiles and passes its tests** via the real
   toolchain, with results gating progression.
6. Killing and restarting the host mid-workflow **resumes** execution from persisted state.
7. Any artifact can be traced back to the requirement that caused it, and any requirement forward to
   the artifacts satisfying it.
8. All ten engineering intelligence metrics are reported.
9. A Final Engineering Review Package is assembled automatically.
10. All three scenarios — greenfield, brownfield, ambiguous — execute end to end.
11. The platform runs **offline with no API credentials** in simulation mode, exercising the same
    execution paths.

---

## 10. Glossary

| Term | Meaning in this system |
|---|---|
| **Workflow** | One end-to-end execution of the SDLC for one requirement. |
| **Node** | A unit of work in the dependency graph, executed by one agent (or a system join). |
| **Edge** | A dependency between nodes. *Hard* edges block readiness; *soft* edges only contribute context. |
| **Artifact** | A versioned engineering output — specification, plan, ADR set, source file, validation report, review package. |
| **Decision** | A recorded engineering choice with rationale, linked to the requirements that motivated it and the artifacts it produced. |
| **Gate** | A governance checkpoint on a node — entry or exit, automated policy or human approval. |
| **Stale** | State of a node whose upstream inputs changed, requiring re-execution. |
| **Supersession** | Replacement of an artifact by a newer version; the predecessor is retained for lineage. |
| **Context propagation** | Assembling upstream artifacts, decisions, and assumptions into the input of a downstream agent. |
| **Greenfield / Brownfield** | Building new software / modifying software that already exists. |

---

## 11. Next Deliverables

1. **Architecture & Design Document** — component decomposition, domain model, execution algorithm,
   governance model, persistence design. *(see [02-Engineering-Specification.md](02-Engineering-Specification.md)
   and [06-Architecture-Overview.md](06-Architecture-Overview.md))*
2. **Implementation Plan** — build sequence, verification per slice.
   *(see [03-Implementation-Plan.md](03-Implementation-Plan.md))*
3. **Implementation** — the platform itself.
4. **Demonstration Guide** — how to run each of the three scenarios.
   *(see [04-Demonstration-Guide.md](04-Demonstration-Guide.md))*
