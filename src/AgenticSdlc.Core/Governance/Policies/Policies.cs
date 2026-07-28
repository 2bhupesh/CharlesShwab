using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticSdlc.Core.Agents.Contracts;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Governance.Policies;

/// <summary>Shared helper to fetch the latest non-superseded artifact of a type for a workflow.</summary>
internal static class ArtifactQuery
{
    public static async Task<Artifact?> LatestAsync(AgenticDbContext db, Guid workflowId, ArtifactType type, CancellationToken ct) =>
        await db.Artifacts
            .Where(a => a.WorkflowId == workflowId && a.Type == type && a.Status != ArtifactStatus.Superseded)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct);
}

/// <summary>
/// Spec exit gate: fails softly into a clarification request when the specification contains blocking
/// ambiguities — the ambiguous-requirement scenario (FR-34). Not blocking ⇒ pass.
/// </summary>
public sealed class NoBlockingAmbiguitiesPolicy : IGatePolicy
{
    private readonly IDbContextFactory<AgenticDbContext> _db;
    public NoBlockingAmbiguitiesPolicy(IDbContextFactory<AgenticDbContext> db) => _db = db;
    public string Name => "NoBlockingAmbiguities";

    public async Task<PolicyResult> EvaluateAsync(WorkflowNode node, string? parametersJson, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var spec = await ArtifactQuery.LatestAsync(db, node.WorkflowId, ArtifactType.EngineeringSpecification, ct);
        if (spec?.ContentJson is null) return PolicyResult.Ok("no specification to check");

        var (ok, output, _) = JsonExtractor.TryParse<SpecOutput>(spec.ContentJson);
        if (!ok || output is null) return PolicyResult.Ok("specification unparseable; not treated as blocking");

        var blocking = output.Ambiguities.Where(a => a.IsBlocking).ToList();
        if (blocking.Count == 0) return PolicyResult.Ok("no blocking ambiguities");

        var questions = blocking
            .Select((a, i) => new ClarificationQuestion($"Q{i + 1}", a.ClarifyingQuestion, a.Text))
            .ToList();
        return PolicyResult.NeedsClarification(questions, $"{blocking.Count} blocking ambiguities require clarification");
    }
}

/// <summary>Validate exit gate: the generated project must build.</summary>
public sealed class BuildMustSucceedPolicy : IGatePolicy
{
    private readonly IDbContextFactory<AgenticDbContext> _db;
    public BuildMustSucceedPolicy(IDbContextFactory<AgenticDbContext> db) => _db = db;
    public string Name => "BuildMustSucceed";

    public async Task<PolicyResult> EvaluateAsync(WorkflowNode node, string? parametersJson, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var report = await ArtifactQuery.LatestAsync(db, node.WorkflowId, ArtifactType.ValidationReport, ct);
        if (report?.ContentJson is null) return PolicyResult.Fail("no validation report produced");

        var (ok, v, _) = JsonExtractor.TryParse<ValidationOutput>(report.ContentJson);
        if (!ok || v is null) return PolicyResult.Fail("validation report unparseable");
        if (v.Overall.Equals("skipped", StringComparison.OrdinalIgnoreCase))
            return PolicyResult.Ok("build validation skipped (toolchain unavailable)");
        return v.BuildSucceeded
            ? PolicyResult.Ok("build succeeded")
            : PolicyResult.Fail($"build failed: {string.Join("; ", v.BuildErrors.Take(3))}");
    }
}

/// <summary>Validate exit gate: the test pass rate must meet the configured threshold.</summary>
public sealed class ValidationPassRatePolicy : IGatePolicy
{
    private readonly IDbContextFactory<AgenticDbContext> _db;
    public ValidationPassRatePolicy(IDbContextFactory<AgenticDbContext> db) => _db = db;
    public string Name => "ValidationPassRate";

    private sealed record Parameters(double MinPassRate);

    public async Task<PolicyResult> EvaluateAsync(WorkflowNode node, string? parametersJson, CancellationToken ct)
    {
        var min = 0.9;
        if (!string.IsNullOrWhiteSpace(parametersJson))
        {
            var (ok, p, _) = JsonExtractor.TryParse<Parameters>(parametersJson);
            if (ok && p is not null) min = p.MinPassRate;
        }

        await using var db = await _db.CreateDbContextAsync(ct);
        var report = await ArtifactQuery.LatestAsync(db, node.WorkflowId, ArtifactType.ValidationReport, ct);
        if (report?.ContentJson is null) return PolicyResult.Fail("no validation report produced");

        var (parsed, v, _) = JsonExtractor.TryParse<ValidationOutput>(report.ContentJson);
        if (!parsed || v is null) return PolicyResult.Fail("validation report unparseable");
        if (v.Overall.Equals("skipped", StringComparison.OrdinalIgnoreCase))
            return PolicyResult.Ok("test validation skipped");
        if (v.TestsTotal == 0) return PolicyResult.Ok("no tests to evaluate");

        var rate = (double)v.TestsPassed / v.TestsTotal;
        return rate >= min
            ? PolicyResult.Ok($"pass rate {rate:P0} ≥ {min:P0}")
            : PolicyResult.Fail($"pass rate {rate:P0} below required {min:P0}");
    }
}

/// <summary>Validate exit gate: no credential-shaped secrets in generated files (NFR-8).</summary>
public sealed class SecretScanPolicy : IGatePolicy
{
    private readonly IDbContextFactory<AgenticDbContext> _db;
    public SecretScanPolicy(IDbContextFactory<AgenticDbContext> db) => _db = db;
    public string Name => "SecretScan";

    private static readonly Regex[] Patterns =
    {
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled),
        new(@"-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"(?i)(password|passwd|pwd)\s*[:=]\s*[""']?[^\s""']{6,}", RegexOptions.Compiled),
        new(@"(?i)(api[_-]?key|secret|token)\s*[:=]\s*[""'][A-Za-z0-9_\-]{16,}[""']", RegexOptions.Compiled)
    };

    public async Task<PolicyResult> EvaluateAsync(WorkflowNode node, string? parametersJson, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == node.WorkflowId, ct);
        var root = wf?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return PolicyResult.Ok("no workspace files to scan");

        var findings = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); } catch { continue; }
            foreach (var p in Patterns)
                if (p.IsMatch(text))
                    findings.Add($"{Path.GetFileName(file)}: matched {p}");
        }

        return findings.Count == 0
            ? PolicyResult.Ok("no secrets detected")
            : PolicyResult.Fail($"potential secrets: {string.Join("; ", findings.Take(3))}");
    }
}

/// <summary>
/// Package entry gate: every high-impact artifact must have a granted human approval on its producing
/// node. Catches governance bypass structurally (spec §5.2).
/// </summary>
public sealed class ChangeControlPolicy : IGatePolicy
{
    private static readonly ArtifactType[] HighImpact = { ArtifactType.AdrSet, ArtifactType.DbScript };
    private readonly IDbContextFactory<AgenticDbContext> _db;
    public ChangeControlPolicy(IDbContextFactory<AgenticDbContext> db) => _db = db;
    public string Name => "ChangeControl";

    public async Task<PolicyResult> EvaluateAsync(WorkflowNode node, string? parametersJson, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var artifacts = await db.Artifacts
            .Where(a => a.WorkflowId == node.WorkflowId && a.Status != ArtifactStatus.Superseded && HighImpact.Contains(a.Type))
            .ToListAsync(ct);

        foreach (var a in artifacts)
        {
            var approved = await db.Approvals.AnyAsync(x =>
                x.NodeId == a.ProducedByNodeId &&
                x.GateType == GateType.HumanApproval &&
                x.Status == ApprovalStatus.Approved, ct);
            if (!approved)
                return PolicyResult.Fail($"{a.Type} '{a.Name}' was not approved by a human before release");
        }
        return PolicyResult.Ok($"{artifacts.Count} high-impact artifact(s) approved");
    }
}
