namespace AgenticSdlc.Core.Domain;

/// <summary>
/// A point-in-time capture of computed metrics, persisted once at workflow completion for historical
/// trend queries. Live values are computed on demand (spec §9.2); this is the durable record.
/// <see cref="WorkflowId"/> null means a platform-wide snapshot.
/// </summary>
public class MetricSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? WorkflowId { get; set; }

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Serialized metric bundle (JSON).</summary>
    public string MetricsJson { get; set; } = "{}";
}
