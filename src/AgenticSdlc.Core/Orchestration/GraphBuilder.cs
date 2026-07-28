using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Governance;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// Composes the execution graph from a built-in SDLC template (phase ordering true of all software
/// engineering) plus dynamic expansion from the Planning agent's output (decomposition specific to
/// this requirement). The dynamic part is what makes the generation stage graph-driven rather than
/// sequential (spec §4.2). No demonstration-domain knowledge lives here (NFR-1).
/// </summary>
public sealed class GraphBuilder
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly CoreOptions _options;

    public GraphBuilder(IDbContextFactory<AgenticDbContext> dbFactory, CoreOptions options)
    {
        _dbFactory = dbFactory;
        _options = options;
    }

    public async Task BuildInitialGraphAsync(Guid workflowId, string scenarioKey, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var isBrownfield = scenarioKey.Equals("brownfield", StringComparison.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, WorkflowNode>();

        WorkflowNode Add(string key, string name, AgentType agent, WorkflowPhase phase,
            IEnumerable<GateDefinition>? gates = null, bool continueOnFailure = false,
            NodeStatus status = NodeStatus.Pending)
        {
            var node = new WorkflowNode
            {
                WorkflowId = workflowId,
                Key = key,
                Name = name,
                AgentType = agent,
                Phase = phase,
                Status = status,
                MaxAttempts = _options.Orchestration.MaxAttempts,
                TimeoutSeconds = _options.Orchestration.DefaultNodeTimeoutSeconds,
                ContinueOnFailure = continueOnFailure,
                GatesJson = GateDefinition.Serialize(gates ?? Array.Empty<GateDefinition>())
            };
            nodes[key] = node;
            return node;
        }

        Add("spec", "Requirement Analysis", AgentType.RequirementIntelligence, WorkflowPhase.Intake,
            gates: new[] { GateDefinition.Policy(GateStage.Exit, "NoBlockingAmbiguities", "Specification must have no blocking ambiguities") });

        // Brownfield runs after spec and before plan; skipped for greenfield/ambiguous scenarios.
        Add("brownfield", "Brownfield Impact Analysis", AgentType.Brownfield, WorkflowPhase.Design,
            continueOnFailure: true,
            status: isBrownfield ? NodeStatus.Pending : NodeStatus.Skipped);

        Add("plan", "Engineering Planning", AgentType.Planning, WorkflowPhase.Planning,
            gates: new[] { GateDefinition.Human(GateStage.Exit, "Approve the engineering plan before design") });

        Add("arch", "Architecture Reasoning", AgentType.Architecture, WorkflowPhase.Design,
            gates: new[] { GateDefinition.Human(GateStage.Exit, "Approve the architecture (high impact)") });

        Add("risk", "Risk Assessment", AgentType.RiskAssessment, WorkflowPhase.Design, continueOnFailure: true);

        Add("gen.ready", "Generation Ready", AgentType.Join, WorkflowPhase.Generation,
            gates: isBrownfield
                ? new[] { GateDefinition.Human(GateStage.Entry, "Accept the assessed risk before modifying the codebase") }
                : null);

        Add("gen.done", "Generation Complete", AgentType.Join, WorkflowPhase.Generation);

        Add("validate", "Validation", AgentType.Validation, WorkflowPhase.Validation,
            gates: new[]
            {
                GateDefinition.Policy(GateStage.Exit, "BuildMustSucceed", "The generated project must build"),
                GateDefinition.Policy(GateStage.Exit, "ValidationPassRate", "Test pass rate must meet threshold", "{\"minPassRate\":0.9}"),
                GateDefinition.Policy(GateStage.Exit, "SecretScan", "No secrets in generated files")
            });

        Add("package", "Release Readiness", AgentType.Packaging, WorkflowPhase.Release,
            gates: new[]
            {
                GateDefinition.Human(GateStage.Entry, "Approve release readiness"),
                GateDefinition.Policy(GateStage.Entry, "ChangeControl", "High-impact artifacts must be approved")
            });

        db.Nodes.AddRange(nodes.Values);

        var edges = new List<DependencyEdge>();
        void Edge(string from, string to, EdgeKind kind) =>
            edges.Add(new DependencyEdge { WorkflowId = workflowId, FromNodeId = nodes[from].Id, ToNodeId = nodes[to].Id, Kind = kind });

        Edge("spec", "brownfield", EdgeKind.Hard);
        Edge("spec", "plan", EdgeKind.Hard);
        Edge("brownfield", "plan", EdgeKind.Soft);
        Edge("plan", "arch", EdgeKind.Hard);
        Edge("brownfield", "arch", EdgeKind.Soft);
        Edge("plan", "risk", EdgeKind.Hard);
        Edge("arch", "risk", EdgeKind.Soft);
        Edge("arch", "gen.ready", EdgeKind.Hard);
        Edge("risk", "gen.ready", EdgeKind.Soft);
        Edge("brownfield", "gen.ready", EdgeKind.Soft);
        // Direct ready->done so an empty plan still flows; removed when gen tasks are inserted.
        Edge("gen.ready", "gen.done", EdgeKind.Hard);
        Edge("gen.done", "validate", EdgeKind.Hard);
        Edge("arch", "validate", EdgeKind.Soft);
        Edge("validate", "package", EdgeKind.Hard);
        Edge("risk", "package", EdgeKind.Soft);

        db.Edges.AddRange(edges);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Inserts one generation node per planner-proposed <see cref="AgentType.Generation"/> task
    /// between <c>gen.ready</c> and <c>gen.done</c>, wired by the plan's own dependency declarations.
    /// Independent tasks become sibling nodes with no edge between them and therefore run in parallel.
    /// Idempotent via node-key uniqueness. Returns the number of nodes inserted.
    /// </summary>
    public async Task<int> ExpandFromPlanAsync(Guid workflowId, IReadOnlyList<ProposedTask> tasks, CancellationToken ct)
    {
        var genTasks = tasks.Where(t => t.Agent == AgentType.Generation).ToList();
        if (genTasks.Count == 0) return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Nodes.Where(n => n.WorkflowId == workflowId).ToListAsync(ct);
        var byKey = existing.ToDictionary(n => n.Key);
        if (!byKey.TryGetValue("gen.ready", out var genReady) || !byKey.TryGetValue("gen.done", out var genDone))
            return 0;

        string NodeKey(string taskId) => $"gen.{taskId}";

        // Skip tasks already inserted (idempotency).
        var toInsert = genTasks.Where(t => !byKey.ContainsKey(NodeKey(t.Id))).ToList();
        if (toInsert.Count == 0) return 0;

        // Remove the placeholder ready->done edge so gen.done waits for the real gen nodes.
        var placeholder = await db.Edges.FirstOrDefaultAsync(
            e => e.WorkflowId == workflowId && e.FromNodeId == genReady.Id && e.ToNodeId == genDone.Id, ct);
        if (placeholder is not null) db.Edges.Remove(placeholder);

        var created = new Dictionary<string, WorkflowNode>();
        foreach (var t in toInsert)
        {
            var node = new WorkflowNode
            {
                WorkflowId = workflowId,
                Key = NodeKey(t.Id),
                Name = t.Name,
                AgentType = AgentType.Generation,
                Phase = t.Phase,
                Status = NodeStatus.Pending,
                MaxAttempts = _options.Orchestration.MaxAttempts,
                TimeoutSeconds = _options.Orchestration.DefaultNodeTimeoutSeconds,
                TaskInstructionsJson = System.Text.Json.JsonSerializer.Serialize(t),
                GatesJson = GateDefinition.Serialize(GatesForGenTask(t))
            };
            created[t.Id] = node;
            db.Nodes.Add(node);
        }

        var edges = new List<DependencyEdge>();
        foreach (var t in toInsert)
        {
            var node = created[t.Id];
            var genDeps = t.DependsOn.Where(d => created.ContainsKey(d) || byKey.ContainsKey(NodeKey(d))).ToList();
            if (genDeps.Count == 0)
            {
                edges.Add(new DependencyEdge { WorkflowId = workflowId, FromNodeId = genReady.Id, ToNodeId = node.Id, Kind = EdgeKind.Hard });
            }
            else
            {
                foreach (var dep in genDeps)
                {
                    var fromId = created.TryGetValue(dep, out var cn) ? cn.Id : byKey[NodeKey(dep)].Id;
                    edges.Add(new DependencyEdge { WorkflowId = workflowId, FromNodeId = fromId, ToNodeId = node.Id, Kind = EdgeKind.Hard });
                }
            }
            edges.Add(new DependencyEdge { WorkflowId = workflowId, FromNodeId = node.Id, ToNodeId = genDone.Id, Kind = EdgeKind.Hard });
        }

        db.Edges.AddRange(edges);
        await db.SaveChangesAsync(ct);
        return toInsert.Count;
    }

    /// <summary>
    /// Schema/database generation tasks carry a human exit gate (high impact, FR-17). Detection is by
    /// task intent, not domain — keeps the platform scenario-agnostic.
    /// </summary>
    private static IEnumerable<GateDefinition> GatesForGenTask(ProposedTask t)
    {
        var text = (t.Name + " " + t.Description).ToLowerInvariant();
        if (text.Contains("schema") || text.Contains("database") || text.Contains("migration"))
            yield return GateDefinition.Human(GateStage.Exit, "Approve schema/database change (high impact)");
    }
}
