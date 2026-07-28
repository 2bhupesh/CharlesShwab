using System.Text;
using System.Text.Json;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Core.Workspace;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Packaging;

/// <summary>
/// Assembles the Final Engineering Review Package (FR-31): a single markdown + JSON document gathering
/// requirement interpretation, plan, architecture rationale, artifact index with lineage, validation
/// results, risks, trade-offs, assumptions, approval history, audit summary, metrics, and a computed
/// release-readiness verdict — produced automatically by the packaging node, no manual authoring.
/// </summary>
public sealed class ReviewPackageBuilder
{
    private readonly IDbContextFactory<AgenticDbContext> _dbFactory;
    private readonly WorkspaceManager _workspace;
    private readonly MetricsService _metrics;
    private readonly AuditLogger _audit;

    public ReviewPackageBuilder(
        IDbContextFactory<AgenticDbContext> dbFactory,
        WorkspaceManager workspace,
        MetricsService metrics,
        AuditLogger audit)
    {
        _dbFactory = dbFactory;
        _workspace = workspace;
        _metrics = metrics;
        _audit = audit;
    }

    /// <summary>Builds the package, writes it to the workspace, and records a ReviewPackage artifact.</summary>
    public async Task BuildAsync(Guid workflowId, Guid nodeId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wf = await db.Workflows.FirstAsync(w => w.Id == workflowId, ct);
        var requirements = await db.Requirements.Where(r => r.WorkflowId == workflowId).ToListAsync(ct);
        var decisions = (await db.Decisions.Where(d => d.WorkflowId == workflowId).ToListAsync(ct))
            .OrderBy(d => d.CreatedAt).ToList();
        var risks = await db.Risks.Where(r => r.WorkflowId == workflowId).ToListAsync(ct);
        var approvals = await db.Approvals.Where(a => a.WorkflowId == workflowId).ToListAsync(ct);
        var artifacts = await db.Artifacts.Where(a => a.WorkflowId == workflowId).ToListAsync(ct);
        var audit = await db.AuditEvents.Where(e => e.WorkflowId == workflowId).OrderBy(e => e.Seq).ToListAsync(ct);
        var metrics = await _metrics.GetForWorkflowAsync(workflowId, ct);

        var readiness = ComputeReadiness(wf, approvals, artifacts, risks);
        var markdown = RenderMarkdown(wf, requirements, decisions, risks, approvals, artifacts, audit, metrics, readiness);
        var json = JsonSerializer.Serialize(new
        {
            workflow = new { wf.Id, wf.Name, wf.ScenarioKey, Status = wf.Status.ToString() },
            requirementCount = requirements.Count,
            decisionCount = decisions.Count,
            riskCount = risks.Count,
            artifactCount = artifacts.Count(a => a.Status != ArtifactStatus.Superseded),
            metrics,
            releaseReadiness = readiness
        }, new JsonSerializerOptions { WriteIndented = true });

        await _workspace.WriteFilesAsync(wf.WorkspacePath, new[]
        {
            ("review-package/ReviewPackage.md", markdown),
            ("review-package/review-package.json", json)
        }, ct);

        db.Artifacts.Add(new Artifact
        {
            WorkflowId = workflowId,
            ProducedByNodeId = nodeId,
            Type = ArtifactType.ReviewPackage,
            Name = "Engineering Review Package",
            Status = ArtifactStatus.Approved,
            ContentJson = json,
            ContentPath = "review-package/ReviewPackage.md",
            RequirementIdsJson = "[]"
        });
        await db.SaveChangesAsync(ct);

        await _audit.LogAsync(workflowId, nodeId, AuditEventType.ReviewPackageAssembled, "system",
            $"Review package assembled. Release readiness: {(readiness.Ready ? "READY" : "NOT READY")}.", ct: ct);
    }

    private sealed record Readiness(bool Ready, List<string> Blockers, string ValidationOverall);

    private static Readiness ComputeReadiness(Workflow wf, List<Approval> approvals, List<Artifact> artifacts, List<RiskItem> risks)
    {
        var blockers = new List<string>();
        if (wf.Status != WorkflowStatus.Completed)
            blockers.Add($"Workflow status is {wf.Status}, not Completed.");
        if (approvals.Any(a => a.Status == ApprovalStatus.AutoFailed))
            blockers.Add("One or more policy gates failed.");
        if (risks.Any(r => r.Severity == RiskLevel.Critical && r.Status == RiskStatus.Open))
            blockers.Add("Open critical risk(s) remain.");

        var validation = LatestValidation(artifacts);
        var overall = validation?.Overall ?? "none";
        if (validation is { Overall: "fail" })
            blockers.Add("Validation did not pass.");

        return new Readiness(blockers.Count == 0, blockers, overall);
    }

    private static ValidationOutput? LatestValidation(List<Artifact> artifacts)
    {
        var report = artifacts
            .Where(a => a.Type == ArtifactType.ValidationReport && a.Status != ArtifactStatus.Superseded)
            .OrderByDescending(a => a.Version).FirstOrDefault();
        if (report?.ContentJson is null) return null;
        var (ok, v, _) = JsonExtractor.TryParse<ValidationOutput>(report.ContentJson);
        return ok ? v : null;
    }

    private static string RenderMarkdown(
        Workflow wf, List<RequirementItem> requirements, List<Decision> decisions, List<RiskItem> risks,
        List<Approval> approvals, List<Artifact> artifacts, List<AuditEvent> audit, WorkflowMetrics metrics, Readiness readiness)
    {
        var sb = new StringBuilder();
        void H(string t) => sb.AppendLine($"\n## {t}\n");

        sb.AppendLine($"# Engineering Review Package — {wf.Name}");
        sb.AppendLine($"\n_Scenario: {wf.ScenarioKey} · Status: {wf.Status} · Generated from workflow {wf.Id}_");

        H("1. Requirement Interpretation");
        sb.AppendLine($"> {wf.RequirementText}\n");
        foreach (var kind in new[] { RequirementKind.Functional, RequirementKind.NonFunctional })
        {
            sb.AppendLine($"**{kind} requirements**");
            foreach (var r in requirements.Where(r => r.Kind == kind))
                sb.AppendLine($"- `{r.Code}` {r.Text}");
            sb.AppendLine();
        }

        H("2. Engineering Plan");
        sb.AppendLine(Reference(artifacts, ArtifactType.WorkPlan));

        H("3. Architecture Rationale");
        foreach (var d in decisions.Where(d => d.AgentType == AgentType.Architecture))
            sb.AppendLine($"- **{d.Title}** — {d.Rationale}");

        H("4. Generated Artifacts");
        sb.AppendLine("| Type | Name | Version | Requirements |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var a in artifacts.Where(a => a.Status != ArtifactStatus.Superseded).OrderBy(a => a.Type))
            sb.AppendLine($"| {a.Type} | {a.Name} | v{a.Version} | {FormatCodes(a.RequirementIdsJson)} |");

        H("5. Validation Results");
        var v = LatestValidation(artifacts);
        sb.AppendLine(v is null
            ? "_No validation report._"
            : $"- Build succeeded: **{v.BuildSucceeded}**\n- Tests: **{v.TestsPassed}/{v.TestsTotal}** passed\n- Overall: **{v.Overall}**");

        H("6. Risk Assessment");
        foreach (var r in risks.OrderByDescending(r => r.Severity))
            sb.AppendLine($"- **[{r.Severity}] {r.Title}** ({r.Category}) — {r.Mitigation}");

        H("7. Trade-off Analysis");
        foreach (var d in decisions.Where(d => d.AlternativesJson.Length > 2))
            sb.AppendLine($"- **{d.Title}**: {d.Rationale}");

        H("8. Assumptions");
        foreach (var r in requirements.Where(r => r.Kind == RequirementKind.Assumption))
            sb.AppendLine($"- `{r.Code}` {r.Text}");

        H("9. Limitations");
        sb.AppendLine("- In-memory storage is not durable; a persistent store is future work.");
        sb.AppendLine("- Real build/test validation applies to the generated .NET project only.");

        H("10. Approval History");
        sb.AppendLine("| Node | Stage | Kind | Status | By | Comment |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var a in approvals.OrderBy(a => a.RequestedAt))
            sb.AppendLine($"| {a.Title} | {a.Stage} | {a.Kind} | {a.Status} | {a.Approver ?? "-"} | {a.Comment ?? "-"} |");

        H("11. Audit Trail");
        sb.AppendLine($"_{audit.Count} events recorded._ Most recent:");
        foreach (var e in audit.TakeLast(10))
            sb.AppendLine($"- `{e.Seq}` {e.EventType} — {e.Summary}");

        H("12. Metrics");
        sb.AppendLine($"- Nodes succeeded: {metrics.NodesSucceeded}/{metrics.NodesTotal}");
        sb.AppendLine($"- Agent success rate: {metrics.AgentSuccessRate:P0}");
        sb.AppendLine($"- Retries: {metrics.Retries} · Rollbacks: {metrics.Rollbacks}");
        sb.AppendLine($"- Validation pass rate: {metrics.ValidationPassRate:P0}");
        sb.AppendLine($"- Requirement coverage: {metrics.RequirementCoverage:P0}");
        sb.AppendLine($"- Workflow latency: {metrics.WorkflowLatencySeconds:F1}s · Mean approval: {metrics.MeanApprovalSeconds:F1}s");
        sb.AppendLine($"- Tokens: {metrics.InputTokens} in / {metrics.OutputTokens} out across {metrics.AgentInvocations} calls");

        H("13. Release Readiness");
        sb.AppendLine(readiness.Ready
            ? "✅ **READY** — all gates passed, validation succeeded, no open critical risks."
            : "⛔ **NOT READY**");
        foreach (var b in readiness.Blockers)
            sb.AppendLine($"- {b}");

        return sb.ToString();
    }

    private static string Reference(List<Artifact> artifacts, ArtifactType type)
    {
        var a = artifacts.FirstOrDefault(x => x.Type == type && x.Status != ArtifactStatus.Superseded);
        return a is null ? "_(not produced)_" : $"See artifact **{a.Name}** (v{a.Version}).";
    }

    private static string FormatCodes(string json)
    {
        try { return string.Join(", ", JsonSerializer.Deserialize<List<string>>(json) ?? new()); }
        catch { return ""; }
    }
}
