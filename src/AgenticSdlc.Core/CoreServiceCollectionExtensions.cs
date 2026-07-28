using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Governance;
using AgenticSdlc.Core.Governance.Policies;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Llm.Mock;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgenticSdlc.Core;

/// <summary>
/// Single entry point that wires the entire platform into a host's service collection. The Web
/// project's only coupling to Core is <c>services.AddAgenticSdlcCore(configuration)</c> plus the
/// <c>Abstractions</c> interfaces. Later work packages extend this method to register agents,
/// providers, policies, and the background runner.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddAgenticSdlcCore(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new CoreOptions();
        configuration.GetSection(CoreOptions.SectionName).Bind(options);

        // Resolve the db path to absolute and ensure its directory exists before EF touches it.
        var dbPath = ResolvePath(options.Persistence.DbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        services.Configure<CoreOptions>(configuration.GetSection(CoreOptions.SectionName));
        services.AddSingleton(options);

        // Context factory (not scoped AddDbContext): parallel executors each open a short-lived
        // context, which DbContext thread-safety requires (NFR-7).
        services.AddDbContextFactory<AgenticDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

        // LLM layer: both concrete providers plus the selector the platform depends on (spec §5).
        services.AddSingleton<MockResponseCatalog>();
        services.AddSingleton<MockLlmProvider>();
        services.AddSingleton<AnthropicLlmProvider>();
        services.AddSingleton<ILlmProvider, LlmProviderSelector>();

        // Workspace + toolchain (real dotnet build/test).
        services.AddSingleton<Workspace.WorkspaceManager>();
        services.AddSingleton<Workspace.DotnetCliRunner>();

        // Agents: each registered under IAgent so the registry can resolve by type (NFR-9).
        services.AddSingleton<IAgent, RequirementIntelligenceAgent>();
        services.AddSingleton<IAgent, EngineeringPlanningAgent>();
        services.AddSingleton<IAgent, ArchitectureReasoningAgent>();
        services.AddSingleton<IAgent, RiskAssessmentAgent>();
        services.AddSingleton<IAgent, BrownfieldReasoningAgent>();
        services.AddSingleton<IAgent, EngineeringGenerationAgent>();
        services.AddSingleton<IAgent, ValidationAgent>();
        services.AddSingleton<AgentRegistry>();

        // Governance: real gate evaluator plus the five policies, approval, and re-plan services.
        services.AddSingleton<IGatePolicy, NoBlockingAmbiguitiesPolicy>();
        services.AddSingleton<IGatePolicy, BuildMustSucceedPolicy>();
        services.AddSingleton<IGatePolicy, ValidationPassRatePolicy>();
        services.AddSingleton<IGatePolicy, SecretScanPolicy>();
        services.AddSingleton<IGatePolicy, ChangeControlPolicy>();
        services.AddSingleton<IGateEvaluator, GateEvaluator>();
        services.AddSingleton<ReplanService>();
        services.AddSingleton<RollbackService>();
        services.AddSingleton<ApprovalService>();

        // Observability + packaging.
        services.AddSingleton<WorkflowEventBus>();
        services.AddSingleton<Abstractions.IWorkflowEventBus>(sp => sp.GetRequiredService<WorkflowEventBus>());
        services.AddSingleton<AuditLogger>(sp => new AuditLogger(
            sp.GetRequiredService<IDbContextFactory<Persistence.AgenticDbContext>>(),
            sp.GetRequiredService<Abstractions.IWorkflowEventBus>()));
        services.AddSingleton<MetricsService>();
        services.AddSingleton<TimelineService>();
        services.AddSingleton<Packaging.ReviewPackageBuilder>();

        // Orchestration engine and its background runner.
        services.AddSingleton<WorkflowSignaler>();
        services.AddSingleton<WorkflowCancellationRegistry>();
        services.AddSingleton<WorkflowContextBuilder>();
        services.AddSingleton<GraphBuilder>();
        services.AddSingleton<NodeExecutor>();
        services.AddSingleton<WorkflowEngine>();
        services.AddSingleton<WorkflowService>();
        services.AddHostedService<WorkflowRunnerService>();

        return services;
    }

    /// <summary>
    /// Creates the SQLite database (with WAL) and ensures the workspace/samples directories exist.
    /// Call once at host startup before serving requests.
    /// </summary>
    public static async Task InitializeAgenticSdlcAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AgenticDbContext>>();
        await DbInitializer.InitAsync(factory, ct);

        var options = services.GetRequiredService<CoreOptions>();
        Directory.CreateDirectory(ResolvePath(options.Workspace.Root));
        Directory.CreateDirectory(ResolvePath(options.Workspace.SamplesRoot));
    }

    /// <summary>Resolves a possibly-relative configured path against the current working directory.</summary>
    internal static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
}
