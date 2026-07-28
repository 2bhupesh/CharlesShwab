using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// Lifecycle control for workflows. Every control method mutates persisted state and signals the
/// runner — HTTP handlers return immediately while the background engine does the work, so start,
/// pause, resume, cancel, and inspect are fully concurrent (spec §8). The richer query facade
/// (<c>IWorkflowService</c> in Abstractions) is layered on in WP-8/9.
/// </summary>
public sealed class WorkflowService
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly GraphBuilder _graphBuilder;
    private readonly WorkflowSignaler _signaler;
    private readonly WorkflowCancellationRegistry _cancellation;
    private readonly AuditLogger _audit;
    private readonly CoreOptions _options;

    public WorkflowService(
        IDbContextFactory<AgenticDbContext> dbFactory,
        GraphBuilder graphBuilder,
        WorkflowSignaler signaler,
        WorkflowCancellationRegistry cancellation,
        AuditLogger audit,
        CoreOptions options)
    {
        _dbFactory = dbFactory;
        _graphBuilder = graphBuilder;
        _signaler = signaler;
        _cancellation = cancellation;
        _audit = audit;
        _options = options;
    }

    /// <summary>Creates a workflow in Draft, builds its initial SDLC graph, and returns its id.</summary>
    public async Task<Guid> CreateAsync(string name, string requirementText, string scenarioKey,
        Guid? sourceWorkflowId = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var workspaceRoot = CoreServiceCollectionExtensions.ResolvePath(_options.Workspace.Root);
        var workspacePath = Path.Combine(workspaceRoot, id.ToString());

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.Workflows.Add(new Workflow
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? $"{scenarioKey} workflow" : name,
                RequirementText = requirementText,
                ScenarioKey = scenarioKey,
                Status = WorkflowStatus.Draft,
                Model = _options.Llm.Model,
                WorkspacePath = workspacePath,
                SourceWorkflowId = sourceWorkflowId
            });
            await db.SaveChangesAsync(ct);
        }

        await _graphBuilder.BuildInitialGraphAsync(id, scenarioKey, ct);
        await _audit.LogAsync(id, null, AuditEventType.WorkflowCreated, "system",
            $"Workflow created for scenario '{scenarioKey}'.");
        return id;
    }

    public async Task StartAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, ct)
                 ?? throw new InvalidOperationException($"Workflow {id} not found.");
        if (wf.Status is not (WorkflowStatus.Draft or WorkflowStatus.Paused))
            throw new InvalidOperationException($"Workflow {id} cannot start from status {wf.Status}.");

        wf.Status = WorkflowStatus.Running;
        wf.StartedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync(id, null, AuditEventType.WorkflowStarted, "system", "Workflow started.");
        _signaler.Signal(id);
    }

    public async Task PauseAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, ct)
                 ?? throw new InvalidOperationException($"Workflow {id} not found.");
        if (wf.Status is not (WorkflowStatus.Running or WorkflowStatus.AwaitingApproval))
            throw new InvalidOperationException($"Workflow {id} is not running.");

        wf.Status = WorkflowStatus.Paused;
        await db.SaveChangesAsync(ct);
        _cancellation.Cancel(id); // in-flight nodes revert to Pending without consuming an attempt
        await _audit.LogAsync(id, null, AuditEventType.WorkflowPaused, "system", "Workflow paused (safe stop).");
    }

    public async Task ResumeAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, ct)
                 ?? throw new InvalidOperationException($"Workflow {id} not found.");
        if (wf.Status != WorkflowStatus.Paused)
            throw new InvalidOperationException($"Workflow {id} is not paused.");

        wf.Status = WorkflowStatus.Running;
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync(id, null, AuditEventType.WorkflowResumed, "system", "Workflow resumed.");
        _signaler.Signal(id); // a fresh cancellation token is created lazily on next dispatch
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, ct)
                 ?? throw new InvalidOperationException($"Workflow {id} not found.");
        if (wf.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled)
            throw new InvalidOperationException($"Workflow {id} is already terminal ({wf.Status}).");

        wf.Status = WorkflowStatus.Cancelled;
        wf.CompletedAt = DateTimeOffset.UtcNow;

        var nodes = await db.Nodes.Where(n => n.WorkflowId == id).ToListAsync(ct);
        foreach (var n in nodes.Where(n => n.Status is NodeStatus.Pending or NodeStatus.Ready or NodeStatus.Running or NodeStatus.AwaitingApproval))
            n.Status = NodeStatus.Cancelled;
        await db.SaveChangesAsync(ct);

        _cancellation.Cancel(id);
        await _audit.LogAsync(id, null, AuditEventType.WorkflowCancelled, "system", "Workflow cancelled.");
    }
}
