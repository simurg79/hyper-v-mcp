using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// Regression tests for Issue #87 — server must exit cleanly within the
/// watchdog grace period after the MCP SDK transport observes stdin EOF,
/// while remaining alive as long as stdin stays open.
///
/// <para>
/// Contract enforced (MCP-D12):
/// </para>
/// <list type="bullet">
///   <item>(a) When stdin is closed, the server process exits with code 0
///   well within the 5s grace period (we allow up to 10s here for slow CI).</item>
///   <item>(b) While stdin remains open, the server stays alive past a
///   readiness wait + ~2s idle window — i.e. the watchdog is not a
///   false-positive on a still-open pipe.</item>
/// </list>
///
/// <para>
/// The spawn / readiness / exe-resolution pattern mirrors
/// <see cref="McpStdioInitializeSmokeTests"/>; see that file for the full
/// MCP-D10/D11/Constraint-#5 rationale. This file deliberately uses generous
/// CI-tolerant timeouts since the assertion is "exits within grace period",
/// not the ~60ms observed locally.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "RequiresHyperV")]
[Collection("McpStdioServerSpawn")]
public class Issue87StdinEofShutdownTests
{
    private readonly ITestOutputHelper _output;

    public Issue87StdinEofShutdownTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool IsHyperVAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var vmms = System.ServiceProcess.ServiceController
                .GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, "vmms", StringComparison.OrdinalIgnoreCase));
            return vmms?.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct ServerExeLookup(bool Found, string? Path, IReadOnlyList<string> AttemptedPaths);

    private static ServerExeLookup ResolveServerExePath()
    {
        var attempted = new List<string>();

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var configDir = Path.GetDirectoryName(baseDir);
        var config = configDir is null ? null : Path.GetFileName(configDir);
        var binDir = configDir is null ? null : Path.GetDirectoryName(configDir);
        var testProjDir = binDir is null ? null : Path.GetDirectoryName(binDir);
        var testsRoot = testProjDir is null ? null : Path.GetDirectoryName(testProjDir);
        var repoRoot = testsRoot is null ? null : Path.GetDirectoryName(testsRoot);

        if (repoRoot is null || config is null)
        {
            attempted.Add($"<unable to derive repo root from AppContext.BaseDirectory='{AppContext.BaseDirectory}'>");
            return new ServerExeLookup(false, null, attempted);
        }

        var candidate = Path.Combine(repoRoot, "src", "HyperV.Mcp.Server", "bin", config, tfm, "HyperV.Mcp.Server.exe");
        attempted.Add(candidate);
        if (File.Exists(candidate))
        {
            return new ServerExeLookup(true, candidate, attempted);
        }
        return new ServerExeLookup(false, null, attempted);
    }

    private sealed class SpawnedServer : IDisposable
    {
        public Process Process { get; }
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly object _stdoutLock = new();
        private readonly object _stderrLock = new();

        public SpawnedServer(Process p)
        {
            Process = p;
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_stdoutLock) { _stdout.AppendLine(e.Data); }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_stderrLock) { _stderr.AppendLine(e.Data); }
            };
        }

        public string SnapshotStdout() { lock (_stdoutLock) { return _stdout.ToString(); } }
        public string SnapshotStderr() { lock (_stderrLock) { return _stderr.ToString(); } }

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
            try { Process.Dispose(); } catch { }
        }
    }

    private static SpawnedServer StartServer(string serverExe)
    {
        var utf8NoBom = new UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = serverExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
        };
        var proc = new Process { StartInfo = psi };
        var wrapper = new SpawnedServer(proc);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return wrapper;
    }

    private async Task<bool> WaitForReadinessAsync(SpawnedServer server, int timeoutSeconds = 60)
    {
        const string readyMarker = "Application started";
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (server.Process.HasExited) return false;
            if (server.SnapshotStderr().Contains(readyMarker, StringComparison.Ordinal)) return true;
            await Task.Delay(200);
        }
        return false;
    }

    private (bool skip, string? exe) ResolveOrSkip()
    {
        if (!IsHyperVAvailable())
        {
            _output.WriteLine("SKIP: Hyper-V (vmms service) not available.");
            return (true, null);
        }

        var lookup = ResolveServerExePath();
        if (!lookup.Found || lookup.Path is null)
        {
            Assert.Fail(
                $"Server exe not found at any candidate path. " +
                $"Searched: {string.Join(", ", lookup.AttemptedPaths)}. " +
                $"Run a Debug build of HyperV.Mcp.Server first.");
        }
        _output.WriteLine($"Server exe: {lookup.Path}");
        return (false, lookup.Path);
    }

    [Fact(Timeout = 180_000)]
    public async Task ServerExitsWithinGracePeriod_AfterStdinEof()
    {
        var (skip, serverExe) = ResolveOrSkip();
        if (skip) return;

        using var server = StartServer(serverExe!);
        var ready = await WaitForReadinessAsync(server);
        if (!ready)
        {
            Assert.Fail(
                $"Server did not become ready within 60s.\n" +
                $"--- STDOUT ---\n{server.SnapshotStdout()}\n--- STDERR ---\n{server.SnapshotStderr()}");
        }

        // Close stdin → SDK transport observes EOF → watchdog calls
        // StopApplication() → process exits cleanly within grace period (5s).
        // Allow 10s on slow CI.
        try { server.Process.StandardInput.Close(); } catch { }

        var sw = Stopwatch.StartNew();
        var exited = server.Process.WaitForExit(10_000);
        sw.Stop();

        if (!exited)
        {
            try { server.Process.Kill(entireProcessTree: true); } catch { }
            server.Process.WaitForExit(5_000);
            Assert.Fail(
                $"Server did not exit within 10s after stdin EOF (watchdog grace period is ~5s).\n" +
                $"Elapsed: {sw.ElapsedMilliseconds} ms.\n" +
                $"--- STDOUT ---\n{server.SnapshotStdout()}\n--- STDERR ---\n{server.SnapshotStderr()}");
        }

        // Flush async readers.
        server.Process.WaitForExit();
        _output.WriteLine($"Server exited {sw.ElapsedMilliseconds} ms after stdin close (exit code {server.Process.ExitCode}).");
        server.Process.ExitCode.Should().Be(0,
            $"server should exit cleanly after stdin EOF.\n" +
            $"--- STDOUT ---\n{server.SnapshotStdout()}\n--- STDERR ---\n{server.SnapshotStderr()}");
    }

    [Fact(Timeout = 180_000)]
    public async Task ServerStaysAlive_WhileStdinOpen()
    {
        var (skip, serverExe) = ResolveOrSkip();
        if (skip) return;

        using var server = StartServer(serverExe!);
        var ready = await WaitForReadinessAsync(server);
        if (!ready)
        {
            Assert.Fail(
                $"Server did not become ready within 60s.\n" +
                $"--- STDOUT ---\n{server.SnapshotStdout()}\n--- STDERR ---\n{server.SnapshotStderr()}");
        }

        // Hold stdin open and verify the server does NOT exit prematurely
        // (i.e. the watchdog is correctly passive).
        await Task.Delay(2_000);

        server.Process.HasExited.Should().BeFalse(
            $"server must remain alive while stdin is still open (no false-positive watchdog trigger).\n" +
            $"--- STDOUT ---\n{server.SnapshotStdout()}\n--- STDERR ---\n{server.SnapshotStderr()}");

        // Cleanup: close stdin and let it shut down so we don't leak the process.
        try { server.Process.StandardInput.Close(); } catch { }
        if (!server.Process.WaitForExit(10_000))
        {
            try { server.Process.Kill(entireProcessTree: true); } catch { }
            server.Process.WaitForExit(5_000);
        }
    }
}
