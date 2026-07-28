using AgenticSdlc.Core;
using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Observability;
using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Web.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Web.Api;

public static class InsightEndpoints
{
    public static void MapMetricsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/workflows/{id:guid}/metrics", async (Guid id, MetricsService metrics, CancellationToken ct) =>
            Results.Ok(await metrics.GetForWorkflowAsync(id, ct))).WithTags("Metrics");
        api.MapGet("/metrics", async (MetricsService metrics, CancellationToken ct) =>
            Results.Ok(await metrics.GetGlobalAsync(ct))).WithTags("Metrics");
    }

    public static void MapReviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/workflows/{id:guid}/review-package", async (Guid id, IDbContextFactory<AgenticDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var pkg = await Latest(db, id, ct);
            return pkg?.ContentJson is not null
                ? Results.Content(pkg.ContentJson, "application/json")
                : Results.NotFound();
        }).WithTags("Review");

        api.MapGet("/workflows/{id:guid}/review-package.md", async (Guid id, IDbContextFactory<AgenticDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var pkg = await Latest(db, id, ct);
            if (pkg?.ContentPath is null) return Results.NotFound();
            var wf = await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == id, ct);
            var full = Path.GetFullPath(Path.Combine(wf.WorkspacePath, pkg.ContentPath));
            return full.StartsWith(Path.GetFullPath(wf.WorkspacePath), StringComparison.Ordinal) && File.Exists(full)
                ? Results.File(full, "text/markdown", $"review-{id}.md")
                : Results.NotFound();
        }).WithTags("Review");
    }

    public static void MapHealthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", async (IDbContextFactory<AgenticDbContext> dbf, ILlmProvider llm, CoreOptions options, CancellationToken ct) =>
        {
            bool dbOk;
            int active = 0, pending = 0;
            try
            {
                await using var db = await dbf.CreateDbContextAsync(ct);
                active = await db.Workflows.CountAsync(w => w.Status == WorkflowStatus.Running || w.Status == WorkflowStatus.AwaitingApproval, ct);
                pending = await db.Approvals.CountAsync(a => a.Status == ApprovalStatus.Pending, ct);
                dbOk = true;
            }
            catch { dbOk = false; }

            var dto = new HealthDto(
                dbOk ? "ok" : "degraded", dbOk,
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
                llm.Kind.ToString(), options.Llm.Model, active, pending);
            return dbOk ? Results.Ok(dto) : Results.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).WithTags("Ops");
    }

    private static async Task<Artifact?> Latest(AgenticDbContext db, Guid id, CancellationToken ct) =>
        await db.Artifacts.AsNoTracking()
            .Where(a => a.WorkflowId == id && a.Type == ArtifactType.ReviewPackage && a.Status != ArtifactStatus.Superseded)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct);
}
