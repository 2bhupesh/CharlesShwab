using AgenticSdlc.Core.Persistence;
using AgenticSdlc.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Web.Api;

public static class ArtifactEndpoints
{
    public static void MapArtifactEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/workflows/{id:guid}/artifacts", async (Guid id, ReadModel read, CancellationToken ct) =>
            Results.Ok(await read.GetArtifactsAsync(id, ct))).WithTags("Artifacts");

        api.MapGet("/artifacts/{artifactId:guid}", async (Guid artifactId, ReadModel read, CancellationToken ct) =>
            await read.GetArtifactAsync(artifactId, "", ct) is { } d ? Results.Ok(d) : Results.NotFound()).WithTags("Artifacts");

        api.MapGet("/artifacts/{artifactId:guid}/download", async (Guid artifactId, IDbContextFactory<AgenticDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var a = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == artifactId, ct);
            if (a is null) return Results.NotFound();

            if (a.ContentJson is not null)
                return Results.File(System.Text.Encoding.UTF8.GetBytes(a.ContentJson), "application/json", $"{a.Name}.json");

            var wf = await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == a.WorkflowId, ct);
            var full = SafePath(wf.WorkspacePath, a.ContentPath);
            return full is not null && File.Exists(full)
                ? Results.File(full, "text/plain", Path.GetFileName(full))
                : Results.NotFound();
        }).WithTags("Artifacts");

        api.MapGet("/workflows/{id:guid}/workspace/tree", async (Guid id, IDbContextFactory<AgenticDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wf is null) return Results.NotFound();
            var root = Path.Combine(wf.WorkspacePath, "generated");
            if (!Directory.Exists(root)) return Results.Ok(Array.Empty<string>());
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(p => p)
                .ToList();
            return Results.Ok(files);
        }).WithTags("Artifacts");

        api.MapGet("/workflows/{id:guid}/workspace/file", async (Guid id, string path, IDbContextFactory<AgenticDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wf is null) return Results.NotFound();
            var root = Path.Combine(wf.WorkspacePath, "generated");
            var full = SafePath(root, path);
            if (full is null) return Results.Problem("Path escapes the workspace.", statusCode: StatusCodes.Status400BadRequest);
            return File.Exists(full) ? Results.Text(await File.ReadAllTextAsync(full, ct), "text/plain") : Results.NotFound();
        }).WithTags("Artifacts");
    }

    /// <summary>Canonicalizes a relative path and refuses anything that escapes the root (NFR-8).</summary>
    private static string? SafePath(string root, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }
}
