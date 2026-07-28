# Demonstration Guide
## Agentic Software Engineering System

This guide walks through running the platform and demonstrating each of the three required scenarios,
plus the resilience, governance, and observability capabilities. Everything here runs **offline** on
the deterministic mock provider — no API key required.

---

## 1. Prerequisites

- **.NET 10 SDK** (`dotnet --version` → 10.0.x)
- A modern browser
- No database, no npm, no external services

## 2. Start the platform

```bash
dotnet run --project src/AgenticSdlc.Web --urls http://localhost:5134
```

Open **http://localhost:5134**. On first launch the app creates `data/agentic.db` (SQLite) and the
`workspaces/` folder automatically.

Confirm it is up and which provider is active:

```bash
curl.exe -s http://localhost:5134/api/health
# → {"status":"ok","databaseOk":true,"anthropicKeyConfigured":false,"provider":"Mock", ... }
```

---

## 3. Scenario 1 — Greenfield (build from requirements)

**Goal:** turn a natural-language requirement into a validated, running URL shortener.

1. On the home page, click the **Greenfield** card. The requirement textarea is prefilled.
2. Click **Start workflow**. You are taken to the live workflow view.
3. **Watch the dependency graph.** Nodes transition Pending → Running → Succeeded. `spec` runs first,
   then `plan`. The `brownfield` node is dashed/**Skipped** (greenfield has no existing code).
4. **Approve the plan.** When `plan` finishes, it turns amber (**AwaitingApproval**) and an approval
   card appears below the graph. Read the plan (Artifacts tab → *Engineering Plan*) and click **Approve**.
5. **Approve the architecture** the same way when `arch` awaits approval.
6. **Generation runs in parallel** — several `gen.*` nodes execute concurrently (the plan decided the
   shape). Then `validate` runs a **real `dotnet build` and `dotnet test`** on the generated code.
7. **Approve the release** at the `package` gate. The workflow reaches **Completed**.

**What to inspect:**
- **Artifacts tab** → *Generated workspace* → open `src/UrlShortener.Api/Program.cs` — the actual
  generated code, viewable in the browser.
- **Metrics tab** → validation pass rate 100%, requirement coverage 100%.
- **Review package** button → the auto-assembled 13-section engineering report; **Download Markdown**.

**Independent cross-check** (proves validation is real, not simulated):

```bash
# find the workflow's workspace id from the URL, then run its tests yourself:
dotnet test src/AgenticSdlc.Web/workspaces/<workflow-id>/generated/tests/UrlShortener.Tests
# → all tests pass, outside the platform
```

---

## 4. Scenario 2 — Brownfield (enhance existing code)

**Goal:** add features to an existing codebase with impact analysis first, without breaking it.

1. On the home page, click the **Brownfield** card (requirement prefilled: add expiring links + click
   analytics). Optionally pick a previous greenfield run under *Seed from a previous greenfield run*;
   otherwise the bundled `samples/UrlShortener.Sample` is used.
2. Click **Start workflow**.
3. **Impact analysis runs before planning.** The `brownfield` node executes (it is **not** skipped
   here) and produces a *Brownfield Impact Assessment* artifact — open it from the Artifacts tab to see
   the module inventory, change-impact analysis, and refactoring recommendations.
4. A **risk-acceptance** approval gate pauses before implementation — approve it.
5. Generation modifies the existing files (adds expiry + analytics + a `/links/{code}/stats` endpoint).
6. **Validation runs the full test suite** — the existing tests *plus* the new ones. The Metrics tab
   shows all passing (regression preserved).

**What to inspect:** compare the seeded base (Risk Report / Impact Assessment) against the enhanced
`Program.cs` in the Workspace browser — the `/links/{code}/stats` endpoint and 410-on-expired logic
are new.

---

## 5. Scenario 3 — Ambiguous requirement (clarify and converge)

**Goal:** interpret an incomplete request and converge on a solution.

1. On the home page, click the **Ambiguous requirement** card (prefilled: *"We need something to share
   links better…"*).
2. Click **Start workflow**.
3. The `spec` node runs, detects the request is too vague, and the workflow **pauses at a clarification
   gate** — a card appears with specific questions ("What should the tool do?", "Who are the users?").
4. **Answer the questions** in the card and click **Submit answers**.
5. The spec **re-runs with your answers**, converges on a concrete specification (no more blocking
   ambiguities), and the workflow proceeds through the normal pipeline.
6. The **review package** shows the clarification Q&A trail — traceability from a vague ask to a spec.

---

## 6. Resilience demonstrations

Start any workflow, then:

- **Safe stop / resume** — click **Safe stop** while nodes are running. In-flight work reverts to
  Pending (no retry attempt is consumed) and the workflow shows **Paused**. Click **Resume**; it
  continues from where it left off.
- **Restart recovery** — stop the server (Ctrl-C) while a workflow is mid-flight, then start it again.
  On startup the engine recovers nodes that were stranded `Running` and resumes the workflow from
  SQLite. (Automated in `ReplanRecoveryTests.Restart_recovery_resumes_a_stranded_workflow`.)
- **Re-plan / rollback** — `POST /api/workflows/{id}/replan` with a node id re-runs that node and
  invalidates everything downstream; the prior artifacts are retained as *Superseded* (lineage), and
  the re-run requests fresh approvals.

---

## 7. Governance demonstrations

- **Reject with changes** — at any human gate, add a comment and click **Request changes**. The node
  re-runs with your feedback, downstream work is invalidated, and a fresh approval is requested. The
  rejection is preserved in the audit timeline.
- **Policy enforcement** — the `validate` gate will not pass unless the real build succeeds and the
  test pass-rate meets the threshold; the `package` gate asserts high-impact artifacts were approved.

---

## 8. Observability

- **Activity tab** — the live audit timeline (every state transition, decision, approval).
- **Decisions tab** — every recorded decision with rationale and requirement links (decision lineage).
- **Prompt lineage** — `GET /api/workflows/{id}/prompts` returns every model call with token usage.
- **Metrics** — per-workflow and platform-wide (`GET /api/metrics`): success rates, retry/rollback
  frequency, MTTR, latency, approval time, validation pass rate, requirement/test coverage.

---

## 9. Running against the real Claude API

The mock demonstrates the full platform deterministically. To run the agents against real Claude:

```bash
# PowerShell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet run --project src/AgenticSdlc.Web --urls http://localhost:5134
```

`GET /api/health` will report `"provider":"Anthropic"` and `"anthropicKeyConfigured":true`. Run the
same three scenarios from the dashboard. The agents now reason live; the orchestration, governance,
persistence, and **real build/test validation are identical** to the offline run — only the model
responses differ. If a live call fails, the platform degrades to the mock for that call (recorded in
the audit log) rather than aborting the workflow.

The model is set in [appsettings.json](../src/AgenticSdlc.Web/appsettings.json)
(`AgenticSdlc:Llm:Model`, default `claude-sonnet-5`; switch to `claude-opus-5` for deeper reasoning).

---

## 10. API smoke reference (optional)

Every capability is reachable over HTTP; the dashboard is a thin client over these.

```bash
curl.exe -s  http://localhost:5134/api/scenarios
curl.exe -s -X POST http://localhost:5134/api/workflows -H "Content-Type: application/json" \
     -d "{\"scenario\":\"greenfield\",\"requirement\":\"Build a URL shortener.\"}"
curl.exe -s  http://localhost:5134/api/workflows/<id>
curl.exe -s  http://localhost:5134/api/workflows/<id>/approvals
curl.exe -s -X POST http://localhost:5134/api/workflows/<id>/gates/<gateId>/decision \
     -H "Content-Type: application/json" -d "{\"decision\":\"approve\",\"approver\":\"demo\"}"
curl.exe -s  http://localhost:5134/api/workflows/<id>/artifacts
curl.exe -s  http://localhost:5134/api/workflows/<id>/metrics
curl.exe -s  http://localhost:5134/api/workflows/<id>/review-package.md
curl.exe -N  http://localhost:5134/api/events?workflowId=<id>     # live SSE stream
```

Full OpenAPI document: **http://localhost:5134/openapi/v1.json**.

---

## 11. Troubleshooting

| Symptom | Fix |
|---|---|
| Port already in use | Use another: `--urls http://localhost:5200` |
| `provider` shows Mock but you set a key | Ensure `ANTHROPIC_API_KEY` is set in the **same** shell before `dotnet run`. |
| Validation shows "skipped" | The `dotnet` CLI wasn't found on `PATH`; build/test degrade gracefully. |
| Want a clean slate | Stop the app and delete `src/AgenticSdlc.Web/data` and `src/AgenticSdlc.Web/workspaces`. |
