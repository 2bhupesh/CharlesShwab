using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// Assembles the <see cref="WorkflowContext"/> for one node by transitively walking its upstream
/// edges — both Hard and Soft, since Soft edges exist precisely to deliver context without imposing
/// ordering (spec §4.5). This record is the entire integration surface between agents.
/// </summary>
public sealed class WorkflowContextBuilder
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly CoreOptions _options;

    public WorkflowContextBuilder(IDbContextFactory<AgenticDbContext> dbFactory, CoreOptions options)
    {
        _dbFactory = dbFactory;
        _options = options;
    }

    public async Task<WorkflowContext> BuildAsync(Guid workflowId, Guid nodeId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var wf = await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == workflowId, ct);
        var edges = await db.Edges.AsNoTracking().Where(e => e.WorkflowId == workflowId).ToListAsync(ct);
        var nodes = await db.Nodes.AsNoTracking().Where(n => n.WorkflowId == workflowId).ToListAsync(ct);

        // Breadth-first walk backwards over incoming edges to collect all ancestor node ids.
        var ancestors = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(nodeId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var e in edges.Where(e => e.ToNodeId == current))
            {
                if (ancestors.Add(e.FromNodeId))
                    queue.Enqueue(e.FromNodeId);
            }
        }

        var succeededAncestors = nodes
            .Where(n => ancestors.Contains(n.Id) && n.Status == NodeStatus.Succeeded)
            .Select(n => n.Id)
            .ToHashSet();

        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.WorkflowId == workflowId && a.Status != ArtifactStatus.Superseded)
            .ToListAsync(ct);

        var upstreamArtifacts = artifacts
            .Where(a => succeededAncestors.Contains(a.ProducedByNodeId))
            .Select(a => new ArtifactRef(a.Id, a.Type, a.Name, a.Version, Snippet(a), a.ContentPath))
            .ToList();

        var requirements = await db.Requirements.AsNoTracking()
            .Where(r => r.WorkflowId == workflowId).ToListAsync(ct);
        var decisions = await db.Decisions.AsNoTracking()
            .Where(d => d.WorkflowId == workflowId).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var openRisks = await db.Risks.AsNoTracking()
            .Where(r => r.WorkflowId == workflowId && r.Status == RiskStatus.Open).ToListAsync(ct);

        return new WorkflowContext(
            workflowId, wf.RequirementText, wf.ScenarioKey,
            requirements, decisions, openRisks, upstreamArtifacts, wf.WorkspacePath);
    }

    /// <summary>Truncates artifact content to the configured budget so prompts stay bounded (NFR-10).</summary>
    private string Snippet(Artifact a)
    {
        var content = a.ContentJson ?? (a.ContentPath is null ? "" : $"(file at {a.ContentPath})");
        var max = _options.Llm.MaxContextCharsPerArtifact;
        return content.Length <= max
            ? content
            : content[..max] + $"\n...(truncated; full content at {a.ContentPath ?? "artifact " + a.Id})";
    }
}
