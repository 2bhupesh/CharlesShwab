namespace AgenticSdlc.Core.Domain;

/// <summary>
/// An append-only audit record. Ordered by <see cref="Seq"/> this stream IS the workflow timeline
/// the dashboard renders (spec §9.1). <see cref="Actor"/> is <c>system</c>, <c>agent:{type}</c>, or
/// <c>human:{name}</c>.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Monotonic per-workflow sequence, assigned at write time. Enables afterSeq paging.</summary>
    public long Seq { get; set; }

    public Guid WorkflowId { get; set; }

    public Guid? NodeId { get; set; }

    public AuditEventType EventType { get; set; }

    public string Actor { get; set; } = "system";

    public string Summary { get; set; } = "";

    /// <summary>Structured detail payload (JSON); optional.</summary>
    public string? DetailJson { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
