using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Observability;

/// <summary>One entry in the workflow timeline — an audit event with its node label resolved.</summary>
public sealed record TimelineEntry(
    long Seq,
    DateTimeOffset At,
    string EventType,
    string Actor,
    string Summary,
    Guid? NodeId,
    string? NodeKey);

/// <summary>
/// The workflow timeline (FR-28): the append-only audit stream ordered by sequence, with node keys
/// joined in. The dashboard renders this directly rather than maintaining a parallel representation.
/// </summary>
public sealed class TimelineService
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    public TimelineService(IDbContextFactory<AgenticDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<TimelineEntry>> GetAsync(Guid workflowId, long afterSeq = 0, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var events = await db.AuditEvents
            .Where(e => e.WorkflowId == workflowId && e.Seq > afterSeq)
            .OrderBy(e => e.Seq)
            .ToListAsync(ct);

        var nodeKeys = await db.Nodes
            .Where(n => n.WorkflowId == workflowId)
            .ToDictionaryAsync(n => n.Id, n => n.Key, ct);

        return events.Select(e => new TimelineEntry(
            e.Seq, e.Timestamp, e.EventType.ToString(), e.Actor, e.Summary,
            e.NodeId,
            e.NodeId is { } id && nodeKeys.TryGetValue(id, out var key) ? key : null)).ToList();
    }
}
