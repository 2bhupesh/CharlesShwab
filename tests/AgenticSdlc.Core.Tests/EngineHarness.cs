using System.Diagnostics;
using AgenticSdlc.Core;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Governance;
using AgenticSdlc.Core.Governance.Policies;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Llm.Mock;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Orchestration;
using AgenticSdlc.Core.Workspace;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// Wires up the engine and its collaborators over a <see cref="TestDb"/> with a supplied set of fake
/// agents, and drives ticks manually so tests need no hosted background service. This isolates the
/// engine mechanics from real agents (which arrive in WP-6).
/// </summary>
public sealed class EngineHarness
{
    public TestDb Db { get; }
    public CoreOptions Options { get; }
    public WorkflowService Service { get; }
    public WorkflowEngine Engine { get; }
    /// <summary>Non-null only when the harness was created with real governance.</summary>
    public ApprovalService? Approvals { get; }

    private EngineHarness(TestDb db, CoreOptions options, WorkflowService service, WorkflowEngine engine, ApprovalService? approvals)
    {
        Db = db;
        Options = options;
        Service = service;
        Engine = engine;
        Approvals = approvals;
    }

    public static async Task<EngineHarness> CreateAsync(
        IEnumerable<IAgent> agents, Action<CoreOptions>? configure = null, bool realGovernance = false)
    {
        var db = await TestDb.CreateAsync();
        var options = new CoreOptions();
        configure?.Invoke(options);

        var signaler = new WorkflowSignaler();
        var cancellation = new WorkflowCancellationRegistry();
        var audit = new AuditLogger(db.Factory);
        var contextBuilder = new WorkflowContextBuilder(db.Factory, options);
        var graphBuilder = new GraphBuilder(db.Factory, options);
        var replan = new ReplanService(audit, db.Factory, signaler);
        var rollback = new RollbackService(replan);

        IGateEvaluator gates;
        ApprovalService? approvals = null;
        if (realGovernance)
        {
            var policies = new IGatePolicy[]
            {
                new NoBlockingAmbiguitiesPolicy(db.Factory),
                new BuildMustSucceedPolicy(db.Factory),
                new ValidationPassRatePolicy(db.Factory),
                new SecretScanPolicy(db.Factory),
                new ChangeControlPolicy(db.Factory),
            };
            gates = new GateEvaluator(db.Factory, policies, audit, options);
            approvals = new ApprovalService(db.Factory, audit, signaler, replan);
        }
        else
        {
            gates = new AutoPassGateEvaluator();
        }

        var registry = new AgentRegistry(agents);
        var executor = new NodeExecutor(db.Factory, contextBuilder, registry, gates, graphBuilder, audit, cancellation, signaler, options);
        var engine = new WorkflowEngine(db.Factory, gates, executor, audit, signaler, options);
        var service = new WorkflowService(db.Factory, graphBuilder, signaler, cancellation, audit, replan, rollback, options);

        return new EngineHarness(db, options, service, engine, approvals);
    }

    /// <summary>
    /// Builds a harness wired with the real seven agents over the deterministic mock LLM and real
    /// governance — the full offline platform. Used by the end-to-end validation test that genuinely
    /// runs dotnet build/test on generated code.
    /// </summary>
    public static async Task<EngineHarness> CreateWithRealAgentsAsync(Action<CoreOptions>? configure = null)
    {
        var db = await TestDb.CreateAsync();
        var options = new CoreOptions();
        options.Workspace.Root = Path.Combine(Path.GetTempPath(), "agentic-ws-" + Guid.NewGuid().ToString("N"));
        configure?.Invoke(options);

        var signaler = new WorkflowSignaler();
        var cancellation = new WorkflowCancellationRegistry();
        var audit = new AuditLogger(db.Factory);
        var contextBuilder = new WorkflowContextBuilder(db.Factory, options);
        var graphBuilder = new GraphBuilder(db.Factory, options);
        var replan = new ReplanService(audit, db.Factory, signaler);
        var rollback = new RollbackService(replan);
        var workspace = new WorkspaceManager();
        var cli = new DotnetCliRunner();
        ILlmProvider llm = new MockLlmProvider(new MockResponseCatalog());

        var agents = new List<IAgent>
        {
            new RequirementIntelligenceAgent(llm, db.Factory, options),
            new EngineeringPlanningAgent(llm, db.Factory, options),
            new ArchitectureReasoningAgent(llm, db.Factory, options),
            new RiskAssessmentAgent(llm, db.Factory, options),
            new BrownfieldReasoningAgent(llm, db.Factory, options, workspace),
            new EngineeringGenerationAgent(llm, db.Factory, options, workspace),
            new ValidationAgent(llm, db.Factory, options, cli, workspace, audit),
        };

        var policies = new IGatePolicy[]
        {
            new NoBlockingAmbiguitiesPolicy(db.Factory),
            new BuildMustSucceedPolicy(db.Factory),
            new ValidationPassRatePolicy(db.Factory),
            new SecretScanPolicy(db.Factory),
            new ChangeControlPolicy(db.Factory),
        };
        IGateEvaluator gates = new GateEvaluator(db.Factory, policies, audit, options);
        var approvals = new ApprovalService(db.Factory, audit, signaler, replan);

        var registry = new AgentRegistry(agents);
        var executor = new NodeExecutor(db.Factory, contextBuilder, registry, gates, graphBuilder, audit, cancellation, signaler, options);
        var engine = new WorkflowEngine(db.Factory, gates, executor, audit, signaler, options);
        var service = new WorkflowService(db.Factory, graphBuilder, signaler, cancellation, audit, replan, rollback, options);

        return new EngineHarness(db, options, service, engine, approvals);
    }

    /// <summary>Drives ticks and resolves each pending approval as it appears, until the workflow settles.</summary>
    public async Task<WorkflowStatus> RunAutoApprovingAsync(Guid workflowId, string approver, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = await RunUntilSettledAsync(workflowId, timeout - sw.Elapsed);
            if (status != WorkflowStatus.AwaitingApproval) return status;
            var pending = await Approvals!.GetPendingAsync(workflowId);
            foreach (var a in pending)
                await Approvals.ApproveAsync(a.Id, approved: true, approver, "looks good", requestChanges: false);
        }
        return await StatusAsync(workflowId);
    }

    /// <summary>Drives ticks until the workflow reaches a terminal or awaiting state, or times out.</summary>
    public async Task<WorkflowStatus> RunUntilSettledAsync(Guid workflowId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            await Engine.TickAsync(workflowId);
            var status = await StatusAsync(workflowId);
            if (status is WorkflowStatus.Completed or WorkflowStatus.Failed
                or WorkflowStatus.Cancelled or WorkflowStatus.Paused or WorkflowStatus.AwaitingApproval)
                return status;
            await Task.Delay(20);
        }
        return await StatusAsync(workflowId);
    }

    public async Task<WorkflowStatus> StatusAsync(Guid workflowId)
    {
        await using var ctx = Db.NewContext();
        return (await ctx.Workflows.FirstAsync(w => w.Id == workflowId)).Status;
    }

    public async Task<List<WorkflowNode>> NodesAsync(Guid workflowId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.Nodes.Where(n => n.WorkflowId == workflowId).ToListAsync();
    }

    public async Task<WorkflowNode> NodeAsync(Guid workflowId, string key)
    {
        await using var ctx = Db.NewContext();
        return await ctx.Nodes.FirstAsync(n => n.WorkflowId == workflowId && n.Key == key);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
