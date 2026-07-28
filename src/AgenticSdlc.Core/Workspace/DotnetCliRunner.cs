using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace AgenticSdlc.Core.Workspace;

/// <summary>Result of a dotnet CLI invocation.</summary>
public sealed record CliResult(bool Available, int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => Available && ExitCode == 0;
}

/// <summary>Parsed test counts from a TRX result file.</summary>
public sealed record TestCounts(int Total, int Passed, int Failed);

/// <summary>
/// Shells out to the real dotnet CLI so validation reflects the actual toolchain, not a simulation
/// (spec §7.4, P-5). Gracefully reports unavailability (<see cref="CliResult.Available"/> = false) so
/// the Validation agent can degrade rather than crash when no SDK is present (FR-25).
/// </summary>
public sealed class DotnetCliRunner
{
    public async Task<CliResult> RunAsync(string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                return new CliResult(false, -1, "", "failed to start dotnet");
        }
        catch (Exception ex)
        {
            // dotnet not found on PATH, or not permitted.
            return new CliResult(false, -1, "", ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new CliResult(true, -1, stdout.ToString(), "timed out");
        }

        return new CliResult(true, process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Parses total/passed/failed from the first TRX file found under a directory.</summary>
    public static TestCounts? ParseTrx(string searchDir)
    {
        var trx = Directory.EnumerateFiles(searchDir, "*.trx", SearchOption.AllDirectories).FirstOrDefault();
        if (trx is null) return null;

        try
        {
            var doc = XDocument.Load(trx);
            var counters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
            if (counters is null) return null;
            int Get(string attr) => int.TryParse(counters.Attribute(attr)?.Value, out var v) ? v : 0;
            var total = Get("total");
            var passed = Get("passed");
            var failed = Get("failed") + Get("error") + Get("timeout") + Get("aborted");
            return new TestCounts(total, passed, failed);
        }
        catch
        {
            return null;
        }
    }
}
