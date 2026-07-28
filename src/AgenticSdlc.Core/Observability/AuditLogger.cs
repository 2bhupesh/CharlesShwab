using System.Collections.Concurrent;
using AgenticSdlc.Core.Abstractions;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Observability;

/// <summary>
/// Append-only audit writer (spec §9.1). Assigns a monotonic per-workflow sequence; a per-workflow
/// lock serializes sequence assignment so parallel executors never collide. Each row is also
/// published to the event bus so the dashboard can react in real time.
/// </summary>
public class AuditLogger
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly IWorkflowEventBus? _bus;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public AuditLogger(IDbContextFactory<AgenticDbContext> dbFactory, IWorkflowEventBus? bus = null)
    {
        _dbFactory = dbFactory;
        _bus = bus;
    }

    public virtual async Task LogAsync(
        Guid workflowId, Guid? nodeId, AuditEventType type, string actor, string summary,
        string? detailJson = null, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(workflowId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var lastSeq = await db.AuditEvents
                .Where(e => e.WorkflowId == workflowId)
                .MaxAsync(e => (long?)e.Seq, ct) ?? 0;

            db.AuditEvents.Add(new AuditEvent
            {
                WorkflowId = workflowId,
                NodeId = nodeId,
                Seq = lastSeq + 1,
                EventType = type,
                Actor = actor,
                Summary = summary,
                DetailJson = detailJson
            });
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Release();
        }

        _bus?.Publish(new WorkflowEvent(type.ToString(), workflowId, nodeId, summary, DateTimeOffset.UtcNow));
    }
}
