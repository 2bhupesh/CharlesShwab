# Agentic Software Engineering System

A production-shaped **platform** that coordinates specialized AI agents to execute the end-to-end
software development lifecycle — from a natural-language requirement through planning, architecture,
code generation, testing, validation, and release readiness — under **governed autonomy** with
human-in-the-loop approval gates, full traceability, and resilience.

> The product is the **platform**, not any one application. A URL shortener is used only as a
> demonstration workload. The **orchestration engine** — stateful, dependency-graph-driven, parallel,
> dynamically re-planning — is the differentiator.

Built on **.NET 10**. Runs fully **offline** on a deterministic mock provider (no API key needed), or
against the real **Claude API** when a key is present.

---

## Quick start

```bash
dotnet run --project src/AgenticSdlc.Web --urls http://localhost:5134
```

Open **http://localhost:5134**, pick the **Greenfield** scenario (the requirement is prefilled), and
click **Start workflow**. Watch the dependency graph light up, approve the plan and architecture at the
approval gates, and — once validation passes — browse the generated code and the review package.

No database setup, no npm, no external services. On first launch the app creates the SQLite database
and workspace folders automatically. See **[docs/04-Demonstration-Guide.md](docs/04-Demonstration-Guide.md)**
for a full walkthrough of all three scenarios.

### Using the real Claude API

Provider selection is automatic: set the key and the agents call Claude; leave it unset and they use
the deterministic mock.

```bash
# PowerShell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet run --project src/AgenticSdlc.Web --urls http://localhost:5134
```

`GET /api/health` reports which provider is active. The model is configurable in
[appsettings.json](src/AgenticSdlc.Web/appsettings.json) (`AgenticSdlc:Llm:Model`, default
`claude-sonnet-5`).

---

## The three demonstration scenarios

| Scenario | What it shows |
|---|---|
| **Greenfield** | Build a new system from requirements → full SDLC → generated C# URL shortener whose code genuinely compiles and passes `dotnet test`. |
| **Brownfield** | Enhance an existing codebase (bundled sample) → impact analysis **before** planning → regression validation keeps existing tests green. |
| **Ambiguous** | Interpret a vague request → the platform asks clarifying questions, pauses, incorporates your answers, and converges on a spec. |

---

## What makes the engine different

The requirement defines the orchestration engine by contrast with a traditional workflow engine. All
seven properties are implemented:

- **Stateful execution** — all state in SQLite; workflows resume after a process restart.
- **Dependency-graph execution** — node readiness is computed from an explicit graph, not a sequential chain.
- **Parallel execution** — independent nodes run concurrently under a concurrency limit.
- **Synchronization points** — join nodes become ready only when every inbound branch completes.
- **Dynamic re-planning** — changing an upstream artifact invalidates and re-runs downstream work while preserving governance.
- **Context propagation** — decisions, assumptions, and artifacts flow to every downstream agent.
- **Decision lineage** — every decision is recorded with rationale and linked to its originating requirement.

Plus **controlled autonomy** (entry/exit/quality/human gates; high-impact steps require approval),
**resilience** (retry, timeout, failure isolation, safe stop, rollback, restart recovery), full
**observability** (audit timeline, prompt lineage, artifact lineage, ten engineering metrics), and an
auto-assembled **Final Engineering Review Package**.

---

## Architecture at a glance

```
┌─────────────────────────────────────────────────────────────────────┐
│  AgenticSdlc.Web   REST API · SSE event stream · vanilla-JS dashboard │
└───────────────────────────────┬─────────────────────────────────────┘
                                 │ Abstractions (interfaces + DTOs)
┌───────────────────────────────┴─────────────────────────────────────┐
│  AgenticSdlc.Core                                                     │
│                                                                       │
│   Orchestration  engine (tick scheduler), graph builder, executor,   │
│                  context builder, re-plan, rollback, background runner│
│   Agents         7 specialized agents over a common base + contracts  │
│   Governance     gate evaluator, 5 policies, approval service         │
│   Llm            provider abstraction · Anthropic adapter · mock       │
│   Workspace      file I/O · repo scan · real `dotnet build`/`test`    │
│   Observability  audit logger + event bus, metrics, timeline          │
│   Packaging      review-package builder                               │
│   Persistence    EF Core + SQLite (context factory)                   │
│   Domain         workflow, node, edge, artifact, decision, approval…  │
└───────────────────────────────┬─────────────────────────────────────┘
                    ┌────────────┴────────────┐
              Claude API                 .NET SDK
           (or deterministic         (real build + test
            mock provider)            of generated code)
```

The SDLC graph the engine executes:

```
spec ─▶ brownfield ─▶ plan ─▶ arch ─▶ risk ─▶ gen.ready ─▶ [gen.* …] ─▶ gen.done ─▶ validate ─▶ package
        (skipped for            (parallel)     (join /       (dynamic,      (join /      (real       (review
         greenfield)                            sync)         parallel)      sync)        dotnet)     package)
   ▲ human gates: plan, architecture, release · policy gates: ambiguities, build, pass-rate, secrets, change-control
```

---

## Project structure

```
src/AgenticSdlc.Core     the platform (domain, orchestration, agents, governance, …)
src/AgenticSdlc.Web      REST API, SSE, and the dashboard (wwwroot)
tests/AgenticSdlc.Core.Tests   50 xUnit tests, including real build/test e2e runs
samples/UrlShortener.Sample    the "existing codebase" for the brownfield scenario
docs/                    requirement understanding, specification, plan, demo guide
```

---

## Testing

```bash
dotnet test
```

50 tests, all green. Most run in seconds; three end-to-end tests actually generate a project and run
the real `dotnet build`/`dotnet test` on it (~15–40s each), which is how the platform proves that
"validation results determine workflow progression" is real, not simulated.

---

## Documentation

| Document | Contents |
|---|---|
| [docs/07-Setup-Instructions.md](docs/07-Setup-Instructions.md) | Prerequisites, build, run, configuration, verification, troubleshooting. |
| [docs/01-Requirement-Understanding.md](docs/01-Requirement-Understanding.md) | Interpretation of the assignment: 34 FRs, 11 NFRs, ambiguities, assumptions, risks. |
| [docs/02-Engineering-Specification.md](docs/02-Engineering-Specification.md) | Architecture, domain model, engine algorithm, governance, traceability matrix. |
| [docs/03-Implementation-Plan.md](docs/03-Implementation-Plan.md) | Work-package build plan and verification. |
| [docs/04-Demonstration-Guide.md](docs/04-Demonstration-Guide.md) | Step-by-step walkthrough of all three scenarios + resilience demos. |
| [docs/05-Final-Engineering-Summary.md](docs/05-Final-Engineering-Summary.md) | Capstone: plan/rationale, artifacts, validation, risks, trade-offs, assumptions, limitations. |
| [docs/06-Architecture-Overview.md](docs/06-Architecture-Overview.md) | Components, orchestration model, control flow, and key architectural decisions (diagram-driven). |

---

## Known limitations

- Single-process execution; no authentication (the approver identity is asserted, not verified).
- Real build/test validation targets the generated **.NET** project; other stacks would generate
  artifacts but receive only static and conformance validation.
- In-memory generated apps are not durable; schema evolution recreates the SQLite database (no migrations).
- The `SQLitePCLRaw` transitive dependency carries a low-relevance advisory (embedded single-user store,
  no untrusted SQL); left at EF Core's pinned version rather than forcing a major-version override.
