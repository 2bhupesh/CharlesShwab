using System.Diagnostics;
using AgenticSdlc.Core;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Governance;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Orchestration;
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

    private EngineHarness(TestDb db, CoreOptions options, WorkflowService service, WorkflowEngine engine)
    {
        Db = db;
        Options = options;
        Service = service;
        Engine = engine;
    }

    public static async Task<EngineHarness> CreateAsync(IEnumerable<IAgent> agents, Action<CoreOptions>? configure = null)
    {
        var db = await TestDb.CreateAsync();
        var options = new CoreOptions();
        configure?.Invoke(options);

        var signaler = new WorkflowSignaler();
        var cancellation = new WorkflowCancellationRegistry();
        var audit = new AuditLogger(db.Factory);
        var contextBuilder = new WorkflowContextBuilder(db.Factory, options);
        var graphBuilder = new GraphBuilder(db.Factory, options);
        IGateEvaluator gates = new AutoPassGateEvaluator();
        var registry = new AgentRegistry(agents);
        var executor = new NodeExecutor(db.Factory, contextBuilder, registry, gates, graphBuilder, audit, cancellation, signaler, options);
        var engine = new WorkflowEngine(db.Factory, gates, executor, audit, signaler, options);
        var service = new WorkflowService(db.Factory, graphBuilder, signaler, cancellation, audit, options);

        return new EngineHarness(db, options, service, engine);
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
