# Setup Instructions
## Agentic Software Engineering System

Everything needed to get the platform building, running, and verified from a clean machine. It runs
fully **offline** on a deterministic mock provider — no API key or external service is required to start.

---

## 1. Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| **.NET SDK** | **10.0.x** | The only hard dependency. Provides `dotnet build`/`test` used by the platform's real validation. |
| OS | Windows, macOS, or Linux | Cross-platform .NET; examples show PowerShell (Windows) and bash. |
| Browser | Any modern browser | For the dashboard. |
| Disk | ~1 GB free | SDK packages + generated workspaces. |

**Not required:** Node.js/npm, Docker, a database server, or any cloud account. An Anthropic API key is
optional (see §7).

### Check / install the SDK

```bash
dotnet --version        # expect 10.0.x
dotnet --list-sdks      # confirm a 10.0.* SDK is listed
```

If .NET 10 is missing, install it from <https://dotnet.microsoft.com/download/dotnet/10.0>, then reopen
your terminal and re-check. The repository pins the SDK band via `global.json`
(`10.0.302`, `rollForward: latestFeature`), so a 10.0.302-or-newer 10.0 SDK is used automatically.

---

## 2. Get the code

Copy or clone the repository to a location of your choice, then change into that directory. All
commands in this guide are run from the **repository root** — the folder containing `AgenticSdlc.slnx`
and `global.json`.

```bash
cd <path-to-repository>
```

You can confirm you're in the right place:

```bash
# PowerShell
Test-Path AgenticSdlc.slnx      # → True
# bash
ls AgenticSdlc.slnx            # → lists the file
```

---

## 3. Restore and build

```bash
dotnet build
```

The first build restores NuGet packages (Anthropic SDK, EF Core SQLite, ASP.NET OpenAPI, xUnit) and may
take a minute. Expect **Build succeeded, 0 errors**. A single low-severity NuGet advisory warning
(`NU1903`, a transitive SQLite dependency) is expected and harmless — see the README's *Known
limitations*.

---

## 4. Run the platform

```bash
dotnet run --project src/AgenticSdlc.Web --urls http://localhost:5134
```

Then open **http://localhost:5134**.

On first launch the app automatically:
- creates the SQLite database at `src/AgenticSdlc.Web/data/agentic.db` (with write-ahead logging),
- creates the `workspaces/` and `samples/` directories under the content root,
- serves the dashboard and the REST API on the same origin.

Leave this terminal running. Press **Ctrl-C** to stop.

> **Port already in use?** Pick another: `--urls http://localhost:5200` (and use that port below).

---

## 5. Verify the installation

With the app running, in a second terminal:

```bash
curl.exe -s http://localhost:5134/api/health
# → {"status":"ok","databaseOk":true,"anthropicKeyConfigured":false,"provider":"Mock",
#    "model":"claude-sonnet-5","activeWorkflows":0,"pendingApprovals":0}

curl.exe -s http://localhost:5134/api/scenarios      # → 3 scenarios (greenfield, brownfield, ambiguous)
```

Open in the browser:
- **http://localhost:5134** — the dashboard (home page with three scenario cards)
- **http://localhost:5134/openapi/v1.json** — the OpenAPI document (all routes)

Health showing `"databaseOk":true` and `"provider":"Mock"` confirms a working offline install. To run
an actual workflow, follow [04-Demonstration-Guide.md](04-Demonstration-Guide.md).

---

## 6. Run the tests

```bash
dotnet test
```

Expect **50 passed, 0 failed**. Most tests finish in seconds; three end-to-end tests actually generate a
.NET project and run the real `dotnet build`/`dotnet test` on it (~15–40s each), which requires the SDK
to be installed (it is, from §1).

---

## 7. Optional — use the real Claude API

By default the platform uses the deterministic mock provider. To run the agents against real Claude,
set the API key **in the same terminal** before `dotnet run`. The platform reads it from the environment
only — never from a file.

```powershell
# PowerShell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet run --project src/AgenticSdlc.Web --urls http://localhost:5134
```

```bash
# bash
export ANTHROPIC_API_KEY="sk-ant-..."
dotnet run --project src/AgenticSdlc.Web --urls http://localhost:5134
```

`GET /api/health` will then report `"provider":"Anthropic"` and `"anthropicKeyConfigured":true`.
Provider selection is automatic: key present → Claude, absent → mock. Everything else — orchestration,
governance, persistence, and real build/test validation — is identical between the two.

---

## 8. Configuration reference

Settings live in [src/AgenticSdlc.Web/appsettings.json](../src/AgenticSdlc.Web/appsettings.json) under the
`AgenticSdlc` section. Defaults work out of the box; override only if needed.

| Key | Default | Meaning |
|---|---|---|
| `Llm:Provider` | `Auto` | `Auto` (key→Claude else mock), `Anthropic`, or `Mock`. |
| `Llm:Model` | `claude-sonnet-5` | Model id. Switch to `claude-opus-5` for deeper reasoning. |
| `Llm:MaxTokens` | `8000` | Max output tokens per agent call. |
| `Llm:MaxJsonRetries` | `2` | In-conversation reparse attempts before a node-level retry. |
| `Orchestration:MaxParallelNodes` | `3` | Global concurrency limit for node execution. |
| `Orchestration:DefaultNodeTimeoutSeconds` | `300` | Per-node execution timeout. |
| `Orchestration:MaxAttempts` | `3` | Node retry budget. |
| `Orchestration:RetryBaseDelaySeconds` | `5` | Base for exponential backoff. |
| `Orchestration:ClarificationMaxRounds` | `2` | Bound on ambiguity clarification rounds. |
| `Persistence:DbPath` | `data/agentic.db` | SQLite file (relative to content root). |
| `Workspace:Root` | `workspaces` | Where generated projects are written. |
| `Workspace:SamplesRoot` | `samples` | Where the brownfield sample codebase lives. |

The API key is supplied by the `ANTHROPIC_API_KEY` **environment variable only** and never appears in
configuration files.

---

## 9. Reset to a clean state

To wipe all runtime data (workflows, generated projects) and start fresh, stop the app and delete:

```bash
# PowerShell
Remove-Item -Recurse -Force src/AgenticSdlc.Web/data, src/AgenticSdlc.Web/workspaces
```

These directories are recreated automatically on the next launch. They are git-ignored, so this never
touches source. The `samples/UrlShortener.Sample` project is source (the brownfield seed) — do not delete it.

---

## 10. Project layout

```
AgenticSdlc.slnx                 solution
global.json                      pins the .NET 10 SDK band
src/AgenticSdlc.Core/            the platform (domain, orchestration, agents, governance, …)
src/AgenticSdlc.Web/             REST API, SSE, dashboard (wwwroot); host
tests/AgenticSdlc.Core.Tests/    50 xUnit tests
samples/UrlShortener.Sample/     the brownfield "existing codebase"
docs/                            requirement → spec → plan → demo → summary → architecture → setup
```

---

## 11. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `dotnet: command not found` | .NET SDK not installed or not on `PATH`. Install .NET 10 (§1), reopen the terminal. |
| Build error about SDK version | Installed 10.0 SDK is older than `global.json`. Install 10.0.302+ or update `global.json`. |
| Port 5134 already in use | Another process holds the port. Use `--urls http://localhost:5200`. |
| `provider` shows `Mock` after setting a key | The env var wasn't set in the **same** shell as `dotnet run`. Set it, then run in that shell. |
| Validation reports `"skipped"` | The `dotnet` CLI wasn't found on `PATH` during a run; build/test degrade gracefully rather than failing. |
| A generated build fails on the live provider | Real-model output occasionally needs a retry; the node's retry policy handles it, and the mock always compiles. |
| Want to inspect the API | Browse `http://localhost:5134/openapi/v1.json`, or use the curl reference in the Demonstration Guide §10. |

---

## 12. Next steps

- **[04-Demonstration-Guide.md](04-Demonstration-Guide.md)** — run each of the three scenarios end to end.
- **[06-Architecture-Overview.md](06-Architecture-Overview.md)** — how the platform is built and how it runs.
