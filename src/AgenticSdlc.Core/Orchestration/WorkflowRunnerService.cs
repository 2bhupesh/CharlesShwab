using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// The background loop that drives the engine. Event-driven off the signaler with a 5-second sweep as
/// a safety net for time-based transitions (due retries). On startup it recovers nodes stranded in
/// <see cref="NodeStatus.Running"/> by a previous process (their in-flight tasks died) and re-signals
/// active workflows, so execution resumes after a restart (spec §6, FR-9).
/// </summary>
public sealed class WorkflowRunnerService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly WorkflowSignaler _signaler;
    private readonly WorkflowEngine _engine;
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly AuditLogger _audit;

    public WorkflowRunnerService(
        WorkflowSignaler signaler,
        WorkflowEngine engine,
        IDbContextFactory<AgenticDbContext> dbFactory,
        AuditLogger audit)
    {
        _signaler = signaler;
        _engine = engine;
        _dbFactory = dbFactory;
        _audit = audit;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var signalled = await _signaler.WaitAsync(SweepInterval, stoppingToken);
            try
            {
                if (signalled is { } workflowId)
                    await _engine.TickAsync(workflowId, stoppingToken);
                else
                    await _engine.TickAllActiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A tick failure must not kill the runner; the sweep will retry.
            }
        }
    }

    /// <summary>Resets nodes left Running by a crashed process and re-signals active workflows.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stranded = await db.Nodes.Where(n => n.Status == NodeStatus.Running).ToListAsync(ct);
        foreach (var n in stranded)
        {
            n.Status = NodeStatus.Pending;
            n.StartedAt = null;
        }
        if (stranded.Count > 0)
            await db.SaveChangesAsync(ct);

        var active = await db.Workflows
            .Where(w => w.Status == WorkflowStatus.Running || w.Status == WorkflowStatus.AwaitingApproval)
            .Select(w => w.Id)
            .ToListAsync(ct);

        foreach (var n in stranded)
            await _audit.LogAsync(n.WorkflowId, n.Id, AuditEventType.NodeRecoveredAfterRestart, "system",
                $"Node '{n.Key}' reset to Pending after restart.");

        foreach (var id in active)
            _signaler.Signal(id);
    }
}
