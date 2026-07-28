using System.Text.Json;
using AgenticSdlc.Core.Agents;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Governance;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Resilience;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Orchestration;

/// <summary>
/// Executes a single agent node: assembles context, invokes the agent under a timeout, persists the
/// result as one logical unit (artifacts versioned, drafts recorded, plan expanded), evaluates exit
/// gates, and applies the resilience paths — safe-stop revert, timeout/error retry with backoff, and
/// failure isolation (spec §4.4, §6).
/// </summary>
public sealed class NodeExecutor
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly WorkflowContextBuilder _contextBuilder;
    private readonly AgentRegistry _registry;
    private readonly IGateEvaluator _gates;
    private readonly GraphBuilder _graphBuilder;
    private readonly AuditLogger _audit;
    private readonly WorkflowCancellationRegistry _cancellation;
    private readonly WorkflowSignaler _signaler;
    private readonly RetryPolicy _retry;

    public NodeExecutor(
        IDbContextFactory<AgenticDbContext> dbFactory,
        WorkflowContextBuilder contextBuilder,
        AgentRegistry registry,
        IGateEvaluator gates,
        GraphBuilder graphBuilder,
        AuditLogger audit,
        WorkflowCancellationRegistry cancellation,
        WorkflowSignaler signaler,
        CoreOptions options)
    {
        _dbFactory = dbFactory;
        _contextBuilder = contextBuilder;
        _registry = registry;
        _gates = gates;
        _graphBuilder = graphBuilder;
        _audit = audit;
        _cancellation = cancellation;
        _signaler = signaler;
        _retry = new RetryPolicy(options.Orchestration.RetryBaseDelaySeconds);
    }

    public async Task ExecuteAsync(Guid workflowId, Guid nodeId)
    {
        var workflowToken = _cancellation.GetToken(workflowId);
        await using var db = await _dbFactory.CreateDbContextAsync();

        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId);
        if (node is null || node.Status != NodeStatus.Running)
            return; // cancellation raced dispatch, or already handled

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(workflowToken);
        linked.CancelAfter(TimeSpan.FromSeconds(node.TimeoutSeconds));

        try
        {
            var context = await _contextBuilder.BuildAsync(workflowId, nodeId, linked.Token);
            var agent = _registry.Resolve(node.AgentType);
            var input = new AgentTaskInput(workflowId, nodeId, node.Key, node.Name, node.TaskInstructionsJson, node.Attempt);

            var result = await agent.ExecuteAsync(input, context, linked.Token);

            await PersistResultAsync(db, workflowId, node, result, linked.Token);

            var outcome = await _gates.EvaluateAsync(node, GateStage.Exit, workflowToken);
            switch (outcome.Decision)
            {
                case GateDecision.Passed:
                    await CompleteAsync(db, node, result.SummaryMarkdown);
                    break;
                case GateDecision.AwaitingHuman:
                    node.Status = NodeStatus.AwaitingApproval;
                    await db.SaveChangesAsync();
                    await _audit.LogAsync(workflowId, nodeId, AuditEventType.ApprovalRequested, $"agent:{node.AgentType}",
                        $"Node '{node.Key}' awaiting approval at exit.");
                    break;
                default:
                    node.ErrorMessage = outcome.Reason;
                    await FailOrRetryAsync(db, node, workflowId, outcome.Reason ?? "exit gate failed");
                    break;
            }
        }
        catch (OperationCanceledException) when (workflowToken.IsCancellationRequested)
        {
            await HandleWorkflowCancellationAsync(db, node, workflowId);
        }
        catch (OperationCanceledException)
        {
            // Timeout: the node's own deadline fired, not a workflow-level stop.
            await _audit.LogAsync(workflowId, nodeId, AuditEventType.NodeTimedOut, "system",
                $"Node '{node.Key}' timed out after {node.TimeoutSeconds}s.");
            await FailOrRetryAsync(db, node, workflowId, "timeout");
        }
        catch (Exception ex)
        {
            node.ErrorMessage = ex.Message;
            await FailOrRetryAsync(db, node, workflowId, ex.Message);
        }
        finally
        {
            _signaler.Signal(workflowId); // re-tick the scheduler regardless of outcome
        }
    }

    private async Task CompleteAsync(AgenticDbContext db, WorkflowNode node, string summary)
    {
        node.Status = NodeStatus.Succeeded;
        node.CompletedAt = DateTimeOffset.UtcNow;

        // Draft artifacts produced by this node become Approved once the node succeeds cleanly.
        var drafts = await db.Artifacts
            .Where(a => a.ProducedByNodeId == node.Id && a.Status == ArtifactStatus.Draft)
            .ToListAsync();
        foreach (var a in drafts)
            a.Status = ArtifactStatus.Approved;

        await db.SaveChangesAsync();
        await _audit.LogAsync(node.WorkflowId, node.Id, AuditEventType.NodeSucceeded, $"agent:{node.AgentType}",
            $"Node '{node.Key}' succeeded. {summary}");
    }

    private async Task FailOrRetryAsync(AgenticDbContext db, WorkflowNode node, Guid workflowId, string reason)
    {
        if (node.Attempt < node.MaxAttempts)
        {
            node.Status = NodeStatus.Pending;
            node.NextRetryAt = DateTimeOffset.UtcNow + _retry.Delay(node.Attempt, Random.Shared.NextDouble());
            await db.SaveChangesAsync();
            await _audit.LogAsync(workflowId, node.Id, AuditEventType.NodeRetryScheduled, "system",
                $"Node '{node.Key}' attempt {node.Attempt}/{node.MaxAttempts} failed: {reason}. Retry scheduled.");
            return;
        }

        node.Status = NodeStatus.Failed;
        node.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await _audit.LogAsync(workflowId, node.Id, AuditEventType.NodeFailed, "system",
            $"Node '{node.Key}' failed permanently after {node.Attempt} attempts: {reason}.");

        // Failure isolation: only a critical (non-ContinueOnFailure) node fails the whole workflow.
        if (!node.ContinueOnFailure)
        {
            var wf = await db.Workflows.FirstAsync(w => w.Id == workflowId);
            if (wf.Status is WorkflowStatus.Running or WorkflowStatus.AwaitingApproval)
            {
                wf.Status = WorkflowStatus.Failed;
                wf.FailureReason = $"Node '{node.Key}' failed: {reason}";
                wf.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                await _audit.LogAsync(workflowId, null, AuditEventType.WorkflowFailed, "system", wf.FailureReason);
            }
        }
    }

    private async Task HandleWorkflowCancellationAsync(AgenticDbContext db, WorkflowNode node, Guid workflowId)
    {
        var wf = await db.Workflows.FirstAsync(w => w.Id == workflowId);
        if (wf.Status == WorkflowStatus.Cancelled)
        {
            node.Status = NodeStatus.Cancelled;
            node.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return;
        }

        // Safe stop (pause): revert to Pending WITHOUT consuming a retry attempt (spec §6, FR-23).
        node.Status = NodeStatus.Pending;
        node.Attempt = Math.Max(0, node.Attempt - 1);
        node.StartedAt = null;
        await db.SaveChangesAsync();
        await _audit.LogAsync(workflowId, node.Id, AuditEventType.WorkflowPaused, "system",
            $"Node '{node.Key}' reverted to Pending on safe stop (no attempt consumed).");
    }

    /// <summary>
    /// Persists an agent result as one unit: requirements upserted, artifacts versioned with prior
    /// versions superseded, decisions and risks recorded, and planner follow-ups expanded into nodes.
    /// </summary>
    private async Task PersistResultAsync(AgenticDbContext db, Guid workflowId, WorkflowNode node, AgentResult result, CancellationToken ct)
    {
        // Requirements (spec node) — upsert by code so a clarification re-run does not duplicate.
        if (result.Requirements.Count > 0)
        {
            var codes = result.Requirements.Select(r => r.Code).ToList();
            var existing = await db.Requirements
                .Where(r => r.WorkflowId == workflowId && codes.Contains(r.Code)).ToListAsync(ct);
            db.Requirements.RemoveRange(existing);
            foreach (var r in result.Requirements)
                db.Requirements.Add(new RequirementItem
                {
                    WorkflowId = workflowId, Code = r.Code, Kind = r.Kind,
                    Text = r.Text, Priority = r.Priority, SourceExcerpt = r.SourceExcerpt
                });
        }

        foreach (var draft in result.Artifacts)
        {
            var priors = await db.Artifacts
                .Where(a => a.WorkflowId == workflowId && a.ProducedByNodeId == node.Id
                            && a.Type == draft.Type && a.Status != ArtifactStatus.Superseded)
                .ToListAsync(ct);
            var version = priors.Count == 0 ? 1 : priors.Max(a => a.Version) + 1;

            var artifact = new Artifact
            {
                WorkflowId = workflowId,
                ProducedByNodeId = node.Id,
                Type = draft.Type,
                Name = draft.Name,
                Version = version,
                Status = ArtifactStatus.Draft,
                ContentJson = draft.ContentJson,
                ContentPath = draft.ContentPath,
                RequirementIdsJson = JsonSerializer.Serialize(draft.RequirementIds)
            };
            db.Artifacts.Add(artifact);
            foreach (var p in priors)
            {
                p.Status = ArtifactStatus.Superseded;
                p.SupersededByArtifactId = artifact.Id;
            }
        }

        foreach (var d in result.Decisions)
            db.Decisions.Add(new Decision
            {
                WorkflowId = workflowId, NodeId = node.Id, AgentType = node.AgentType,
                Title = d.Title, Rationale = d.Rationale,
                AlternativesJson = JsonSerializer.Serialize(d.Alternatives),
                RequirementIdsJson = JsonSerializer.Serialize(d.RequirementIds)
            });

        foreach (var r in result.Risks)
            db.Risks.Add(new RiskItem
            {
                WorkflowId = workflowId, NodeId = node.Id, Category = r.Category,
                Severity = r.Severity, Likelihood = r.Likelihood, Title = r.Title,
                Description = r.Description, Mitigation = r.Mitigation,
                RequirementIdsJson = JsonSerializer.Serialize(r.RequirementIds)
            });

        await db.SaveChangesAsync(ct);

        // Planner follow-ups expand the graph (a separate context inside the builder).
        if (result.FollowUpTasks.Count > 0)
            await _graphBuilder.ExpandFromPlanAsync(workflowId, result.FollowUpTasks, ct);
    }
}
