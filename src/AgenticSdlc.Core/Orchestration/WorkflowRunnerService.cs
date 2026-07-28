using Microsoft.Extensions.Hosting;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// The background loop that drives the engine. Event-driven off the signaler with a 5-second sweep as
/// a safety net for time-based transitions (due retries). On startup it recovers nodes stranded in
/// <see cref="NodeStatus.Running"/> by a previous process (their in-flight tasks died) and re-signals
/// active workflows, so execution resumes after a restart (spec §6, FR-9).
/// </summary>
public sealed class WorkflowRunnerService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly WorkflowSignaler _signaler;
    private readonly WorkflowEngine _engine;

    public WorkflowRunnerService(WorkflowSignaler signaler, WorkflowEngine engine)
    {
        _signaler = signaler;
        _engine = engine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _engine.RecoverAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var signalled = await _signaler.WaitAsync(SweepInterval, stoppingToken);
            try
            {
                if (signalled is { } workflowId)
                    await _engine.TickAsync(workflowId, stoppingToken);
                else
                    await _engine.TickAllActiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A tick failure must not kill the runner; the sweep will retry.
            }
        }
    }
}
