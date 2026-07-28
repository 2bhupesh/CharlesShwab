using AgenticSdlc.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgenticSdlc.Core.Persistence;

/// <summary>
/// The system of record. Registered via <c>AddDbContextFactory</c> so parallel node executors each
/// create a short-lived context — <see cref="DbContext"/> is not thread-safe (spec §3.3, NFR-7).
/// Enums are persisted as readable strings; variable-shape payloads live in JSON <c>string</c>
/// columns. Schema is created via <c>EnsureCreated</c>; migrations are deferred at prototype depth.
/// </summary>
public class AgenticDbContext : DbContext
{
    public AgenticDbContext(DbContextOptions<AgenticDbContext> options) : base(options) { }

    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowNode> Nodes => Set<WorkflowNode>();
    public DbSet<DependencyEdge> Edges => Set<DependencyEdge>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<RequirementItem> Requirements => Set<RequirementItem>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AgentExecution> AgentExecutions => Set<AgentExecution>();
    public DbSet<RiskItem> Risks => Set<RiskItem>();
    public DbSet<MetricSnapshot> MetricSnapshots => Set<MetricSnapshot>();

    // SQLite has no native DateTimeOffset and cannot ORDER BY it. Store as UTC ticks (long) so
    // ordering and comparisons translate to SQL. All timestamps are UTC, so no offset is lost.
    private static readonly ValueConverter<DateTimeOffset, long> DtoToTicks =
        new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
    private static readonly ValueConverter<DateTimeOffset?, long?> NullableDtoToTicks =
        new(v => v.HasValue ? v.Value.UtcTicks : null, v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Enums are persisted as readable strings via explicit HasConversion<string>() per property.
        // DateTimeOffset columns are stored as ticks across every entity (see converters above).
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTimeOffset))
                    prop.SetValueConverter(DtoToTicks);
                else if (prop.ClrType == typeof(DateTimeOffset?))
                    prop.SetValueConverter(NullableDtoToTicks);
            }
        }

        b.Entity<Workflow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<WorkflowNode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentType).HasConversion<string>();
            e.Property(x => x.Phase).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.WorkflowId, x.Status });
            e.HasIndex(x => new { x.WorkflowId, x.Key }).IsUnique();
        });

        b.Entity<DependencyEdge>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => x.WorkflowId);
            e.HasIndex(x => x.FromNodeId);
            e.HasIndex(x => x.ToNodeId);
        });

        b.Entity<Artifact>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.WorkflowId, x.Type, x.Status });
            e.HasIndex(x => x.ProducedByNodeId);
        });

        b.Entity<RequirementItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => new { x.WorkflowId, x.Code });
        });

        b.Entity<Decision>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentType).HasConversion<string>();
            e.HasIndex(x => x.WorkflowId);
        });

        b.Entity<Approval>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Stage).HasConversion<string>();
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.GateType).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.WorkflowId, x.Status });
            e.HasIndex(x => x.NodeId);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasConversion<string>();
            e.HasIndex(x => new { x.WorkflowId, x.Seq });
        });

        b.Entity<AgentExecution>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentType).HasConversion<string>();
            e.Property(x => x.Provider).HasConversion<string>();
            e.HasIndex(x => new { x.WorkflowId, x.NodeId });
        });

        b.Entity<RiskItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Category).HasConversion<string>();
            e.Property(x => x.Severity).HasConversion<string>();
            e.Property(x => x.Likelihood).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => x.WorkflowId);
        });

        b.Entity<MetricSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkflowId);
        });
    }
}
