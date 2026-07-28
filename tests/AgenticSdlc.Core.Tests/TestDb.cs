using AgenticSdlc.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// A disposable temp-file SQLite database exposing a real <see cref="IDbContextFactory{TContext}"/>.
/// We deliberately use a file, not <c>:memory:</c> — the platform relies on a context factory that
/// opens multiple independent connections, and an in-memory database would not be shared across them.
/// </summary>
public sealed class TestDb : IAsyncDisposable
{
    private readonly string _path;
    public IDbContextFactory<AgenticDbContext> Factory { get; }

    private TestDb(string path, IDbContextFactory<AgenticDbContext> factory)
    {
        _path = path;
        Factory = factory;
    }

    public static async Task<TestDb> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentic-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgenticDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var factory = new SimpleFactory(options);
        await DbInitializer.InitAsync(factory);
        return new TestDb(path, factory);
    }

    public AgenticDbContext NewContext() => Factory.CreateDbContext();

    public ValueTask DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch { /* best effort */ }
        }
        return ValueTask.CompletedTask;
    }

    private sealed class SimpleFactory(DbContextOptions<AgenticDbContext> options)
        : IDbContextFactory<AgenticDbContext>
    {
        public AgenticDbContext CreateDbContext() => new(options);
    }
}
