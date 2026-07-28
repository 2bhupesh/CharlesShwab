using System.Text.Json;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Web.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Web.Services;

/// <summary>
/// Read-side queries and entity→DTO mapping for the API. Reads go straight through the context
/// factory; control operations stay in the core services. Keeping all mapping here means the core
/// domain model never leaks past the Web layer's contracts.
/// </summary>
public sealed class ReadModel
{
    private static readonly WorkflowPhase[] PhaseOrder =
        { WorkflowPhase.Intake, WorkflowPhase.Planning, WorkflowPhase.Design, WorkflowPhase.Generation, WorkflowPhase.Validation, WorkflowPhase.Release };

    private readonly IDbContextFactory<AgenticDbContext> _db;
    public ReadModel(IDbContextFactory<AgenticDbContext> db) => _db = db;

    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(string? status, string? scenario, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var workflows = await db.Workflows.AsNoTracking().ToListAsync(ct);
        var result = new List<WorkflowSummary>();
        foreach (var wf in workflows.OrderByDescending(w => w.CreatedAt))
        {
            if (status is not null && !wf.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)) continue;
            if (scenario is not null && !wf.ScenarioKey.Equals(scenario, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(await SummaryAsync(db, wf, ct));
        }
        return result;
    }

    public async Task<WorkflowDetail?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wf is null) return null;

        var nodes = await db.Nodes.AsNoTracking().Where(n => n.WorkflowId == id).ToListAsync(ct);
        var edges = await db.Edges.AsNoTracking().Where(e => e.WorkflowId == id).ToListAsync(ct);
        var pendingApprovals = await db.Approvals.AsNoTracking()
            .Where(a => a.WorkflowId == id && a.Status == ApprovalStatus.Pending).ToListAsync(ct);

        var nodeDtos = nodes.Select(n => new NodeDto(
            n.Id.ToString(), n.Key, n.Name, n.AgentType.ToString(), n.Phase.ToString(), n.Status.ToString(),
            n.Attempt, n.StartedAt, n.CompletedAt, n.ErrorMessage)).ToList();
        var edgeDtos = edges.Select(e => new EdgeDto(e.FromNodeId.ToString(), e.ToNodeId.ToString(), e.Kind.ToString())).ToList();

        return new WorkflowDetail(
            wf.Id.ToString(), wf.Name, wf.ScenarioKey, wf.Status.ToString(), wf.RequirementText,
            CurrentPhase(nodes), wf.CreatedAt, wf.CompletedAt, wf.FailureReason, wf.WorkspacePath,
            nodeDtos, edgeDtos, Phases(nodes), pendingApprovals.Select(ToGate).ToList());
    }

    public async Task<IReadOnlyList<ArtifactSummary>> GetArtifactsAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.WorkflowId == id).ToListAsync(ct);
        return artifacts.OrderBy(a => a.CreatedAt).Select(ToArtifactSummary).ToList();
    }

    public async Task<ArtifactDetail?> GetArtifactAsync(Guid artifactId, string workspaceRootless, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var a = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == artifactId, ct);
        if (a is null) return null;

        string? content = a.ContentJson;
        var truncated = false;
        if (content is null && a.ContentPath is not null)
        {
            var wf = await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == a.WorkflowId, ct);
            var full = Path.GetFullPath(Path.Combine(wf.WorkspacePath, a.ContentPath));
            if (full.StartsWith(Path.GetFullPath(wf.WorkspacePath), StringComparison.Ordinal) && File.Exists(full))
            {
                content = await File.ReadAllTextAsync(full, ct);
                if (content.Length > 512 * 1024) { content = content[..(512 * 1024)]; truncated = true; }
            }
        }

        var lineage = new ArtifactLineage(a.ProducedByNodeId.ToString(), a.SupersededByArtifactId?.ToString(), ParseCodes(a.RequirementIdsJson));
        return new ArtifactDetail(ToArtifactSummary(a), content, truncated, lineage);
    }

    public async Task<IReadOnlyList<DecisionDto>> GetDecisionsAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var decisions = await db.Decisions.AsNoTracking().Where(d => d.WorkflowId == id).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return decisions.Select(d => new DecisionDto(
            d.Id.ToString(), d.NodeId.ToString(), d.AgentType.ToString(), d.Title, d.Rationale,
            ParseCodes(d.RequirementIdsJson), d.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<RiskDto>> GetRisksAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var risks = await db.Risks.AsNoTracking().Where(r => r.WorkflowId == id).ToListAsync(ct);
        return risks.Select(r => new RiskDto(
            r.Id.ToString(), r.Category.ToString(), r.Severity.ToString(), r.Likelihood.ToString(),
            r.Title, r.Description, r.Mitigation, r.Status.ToString())).ToList();
    }

    public async Task<IReadOnlyList<PromptDto>> GetPromptsAsync(Guid id, Guid? nodeId, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var q = db.AgentExecutions.AsNoTracking().Where(e => e.WorkflowId == id);
        if (nodeId is { } n) q = q.Where(e => e.NodeId == n);
        var execs = await q.OrderBy(e => e.CreatedAt).ToListAsync(ct);
        return execs.Select(e => new PromptDto(
            e.Id.ToString(), e.NodeId.ToString(), e.AgentType.ToString(), e.Attempt, e.Provider.ToString(),
            e.Model, e.ParsedOk, e.InputTokens, e.OutputTokens, e.DurationMs, e.CreatedAt)).ToList();
    }

    public static GateDto ToGate(Approval a)
    {
        List<ClarificationQuestionDto>? questions = null;
        if (a.QuestionsJson is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<RawQuestion>>(a.QuestionsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                questions = parsed?.Select(q => new ClarificationQuestionDto(q.QuestionId, q.Question, q.Rationale, q.SuggestedOptions)).ToList();
            }
            catch { /* leave null */ }
        }
        return new GateDto(
            a.Id.ToString(), a.WorkflowId.ToString(), a.NodeId.ToString(), a.Kind.ToString(),
            a.Title, a.Description, a.Status.ToString(), a.RequestedAt, a.ResolvedAt, a.Approver, a.Comment, questions);
    }

    private async Task<WorkflowSummary> SummaryAsync(AgenticDbContext db, Workflow wf, CancellationToken ct)
    {
        var nodes = await db.Nodes.AsNoTracking().Where(n => n.WorkflowId == wf.Id).ToListAsync(ct);
        var pending = await db.Approvals.AsNoTracking().CountAsync(a => a.WorkflowId == wf.Id && a.Status == ApprovalStatus.Pending, ct);
        return new WorkflowSummary(
            wf.Id.ToString(), wf.Name, wf.ScenarioKey, wf.Status.ToString(), CurrentPhase(nodes),
            wf.CreatedAt, wf.CompletedAt,
            nodes.Count, nodes.Count(n => n.Status == NodeStatus.Succeeded), nodes.Count(n => n.Status == NodeStatus.Failed), pending);
    }

    private static ArtifactSummary ToArtifactSummary(Artifact a) => new(
        a.Id.ToString(), a.WorkflowId.ToString(), a.ProducedByNodeId.ToString(), a.Name, a.Type.ToString(),
        ContentType(a), a.Version, a.Status.ToString(), a.CreatedAt, ParseCodes(a.RequirementIdsJson), a.ContentPath is not null);

    private static string ContentType(Artifact a)
    {
        if (a.ContentJson is not null) return "json";
        return Path.GetExtension(a.ContentPath ?? "").ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".md" => "markdown",
            ".json" => "json",
            ".csproj" or ".xml" => "xml",
            _ => "text"
        };
    }

    private static string CurrentPhase(List<WorkflowNode> nodes)
    {
        foreach (var phase in PhaseOrder)
        {
            var inPhase = nodes.Where(n => n.Phase == phase).ToList();
            if (inPhase.Count == 0) continue;
            if (inPhase.All(n => n.Status is NodeStatus.Succeeded or NodeStatus.Skipped)) continue;
            return phase.ToString();
        }
        return WorkflowPhase.Release.ToString();
    }

    private static IReadOnlyList<PhaseDto> Phases(List<WorkflowNode> nodes)
    {
        var result = new List<PhaseDto>();
        foreach (var phase in PhaseOrder)
        {
            var inPhase = nodes.Where(n => n.Phase == phase).ToList();
            if (inPhase.Count == 0) continue;
            string status =
                inPhase.Any(n => n.Status == NodeStatus.Failed) ? "Failed" :
                inPhase.All(n => n.Status is NodeStatus.Succeeded or NodeStatus.Skipped) ? "Done" :
                inPhase.Any(n => n.Status is NodeStatus.Running or NodeStatus.AwaitingApproval) ? "Active" :
                "Pending";
            result.Add(new PhaseDto(phase.ToString(), status));
        }
        return result;
    }

    private static IReadOnlyList<string> ParseCodes(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return Array.Empty<string>(); }
    }

    private sealed record RawQuestion(string QuestionId, string Question, string? Rationale, List<string>? SuggestedOptions);
}
