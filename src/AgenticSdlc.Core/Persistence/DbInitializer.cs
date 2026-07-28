using Microsoft.EntityFrameworkCore;

namespace AgenticSdlc.Core.Persistence;

/// <summary>
/// Ensures the SQLite database exists and is configured for concurrent access. Write-ahead logging
/// plus a busy timeout address write contention under parallel node executors (spec §3.3, R-4).
/// </summary>
public static class DbInitializer
{
    public static async Task InitAsync(IDbContextFactory<AgenticDbContext> factory, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);

        // WAL allows concurrent readers alongside a writer; busy_timeout makes writers wait rather
        // than fail with SQLITE_BUSY when the lock is briefly held.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", ct);
    }
}
