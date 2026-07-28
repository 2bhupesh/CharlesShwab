using AgenticSdlc.Core.Workspace;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>WP-6 verification: workspace file I/O is contained, idempotent, and can scan/seed repos.</summary>
public class WorkspaceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ws-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Writes_files_and_is_idempotent()
    {
        var ws = new WorkspaceManager();
        var files = new[] { ("src/A.cs", "class A {}"), ("README.md", "# hi") };
        var written = await ws.WriteFilesAsync(_root, files);
        Assert.Equal(2, written.Count);
        Assert.True(File.Exists(Path.Combine(_root, "src", "A.cs")));

        // Rewriting identical content still succeeds.
        var again = await ws.WriteFilesAsync(_root, files);
        Assert.Equal(2, again.Count);
        Assert.Equal("class A {}", await File.ReadAllTextAsync(Path.Combine(_root, "src", "A.cs")));
    }

    [Fact]
    public async Task Refuses_to_write_outside_the_workspace()
    {
        var ws = new WorkspaceManager();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ws.WriteFilesAsync(_root, new[] { ("../escape.cs", "nope") }));
    }

    [Fact]
    public void Scans_an_existing_repo()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Thing.cs"), "public class Thing { }");
        var digest = new WorkspaceManager().ScanRepo(_root);
        Assert.Contains("Thing.cs", digest);
        Assert.Contains("public class Thing", digest);
    }

    [Fact]
    public void Copies_a_directory_skipping_build_output()
    {
        var srcDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(srcDir, "obj"));
        File.WriteAllText(Path.Combine(srcDir, "keep.cs"), "x");
        File.WriteAllText(Path.Combine(srcDir, "obj", "skip.cs"), "y");

        var dest = Path.Combine(_root, "dest");
        WorkspaceManager.CopyDirectory(srcDir, dest);

        Assert.True(File.Exists(Path.Combine(dest, "keep.cs")));
        Assert.False(File.Exists(Path.Combine(dest, "obj", "skip.cs")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
