using System.Collections.Concurrent;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Observability;

/// <summary>
/// Append-only audit writer (spec §9.1). Assigns a monotonic per-workflow sequence; a per-workflow
/// lock serializes sequence assignment so parallel executors never collide. WP-8 extends this to
/// also publish to the event bus for the dashboard.
/// </summary>
public class AuditLogger
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public AuditLogger(IDbContextFactory<AgenticDbContext> dbFactory) => _dbFactory = dbFactory;

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
    }
}
