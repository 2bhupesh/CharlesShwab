using System.Collections.Concurrent;
using System.Text;

namespace AgenticSdlc.Core.Workspace;

/// <summary>
/// Owns file I/O under <c>workspaces/{id}/</c>: writing generated files, scanning an existing
/// codebase for the brownfield agent, and seeding a workspace from a sample or a prior run. Writes
/// are serialized per workspace and skip identical content, so parallel generation nodes never race
/// (spec §7.3, NFR-7). All paths are contained within the workspace root (NFR-8).
/// </summary>
public sealed class WorkspaceManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public string GeneratedRoot(string workspacePath) => Path.Combine(workspacePath, "generated");

    /// <summary>Writes files under the workspace; returns the absolute paths written.</summary>
    public async Task<IReadOnlyList<string>> WriteFilesAsync(
        string workspacePath, IEnumerable<(string RelativePath, string Content)> files, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(workspacePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var root = Path.GetFullPath(workspacePath);
            var written = new List<string>();
            foreach (var (relative, content) in files)
            {
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to write outside the workspace: {relative}");

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                if (!File.Exists(full) || await File.ReadAllTextAsync(full, ct) != content)
                    await File.WriteAllTextAsync(full, content, ct);
                written.Add(full);
            }
            return written;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Recursively copies a source directory into the workspace, skipping build output.</summary>
    public void SeedFrom(string sourceDir, string workspacePath)
    {
        var target = GeneratedRoot(workspacePath);
        CopyDirectory(sourceDir, target);
    }

    public static void CopyDirectory(string sourceDir, string targetDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(dir)) continue;
            Directory.CreateDirectory(dir.Replace(sourceDir, targetDir));
        }
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            var dest = file.Replace(sourceDir, targetDir);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>
    /// Produces a text digest of an existing codebase — the file tree plus the content of up to
    /// <paramref name="maxFiles"/> source files — to inject into the brownfield agent's prompt.
    /// </summary>
    public string ScanRepo(string repoDir, int maxFiles = 20, int maxCharsPerFile = 4000)
    {
        if (!Directory.Exists(repoDir)) return "(no existing codebase found)";

        var sb = new StringBuilder();
        var files = Directory.EnumerateFiles(repoDir, "*", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .ToList();

        sb.AppendLine("### File tree");
        foreach (var f in files)
            sb.AppendLine("- " + Path.GetRelativePath(repoDir, f).Replace('\\', '/'));

        sb.AppendLine("\n### Key file contents");
        var codeExtensions = new[] { ".cs", ".csproj", ".json", ".md", ".sql", ".yml", ".yaml" };
        foreach (var f in files.Where(f => codeExtensions.Contains(Path.GetExtension(f))).Take(maxFiles))
        {
            var rel = Path.GetRelativePath(repoDir, f).Replace('\\', '/');
            string content;
            try { content = File.ReadAllText(f); } catch { continue; }
            if (content.Length > maxCharsPerFile) content = content[..maxCharsPerFile] + "\n...(truncated)";
            sb.AppendLine($"\n--- {rel} ---\n{content}");
        }
        return sb.ToString();
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}");
}
