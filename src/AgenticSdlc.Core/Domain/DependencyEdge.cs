namespace AgenticSdlc.Core.Domain;

/// <summary>
/// A dependency between two nodes. <see cref="EdgeKind.Hard"/> edges block readiness;
/// <see cref="EdgeKind.Soft"/> edges only contribute context without imposing ordering.
/// </summary>
public class DependencyEdge
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    public Guid FromNodeId { get; set; }

    public Guid ToNodeId { get; set; }

    public EdgeKind Kind { get; set; } = EdgeKind.Hard;
}
