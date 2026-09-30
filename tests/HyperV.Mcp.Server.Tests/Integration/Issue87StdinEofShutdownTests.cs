using System.Diagnostics;
using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>Stdin EOF must exit 0 within the 5s watchdog grace period; allow 10s for slow CI, not the ~60ms seen locally. An open pipe must
/// survive readiness plus ~2s idle. Uses the spawn/readiness/exe-resolution pattern from McpStdioInitializeSmokeTests.</summary>
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

        var serverExe = TestPaths.ServerExecutablePath();
        _output.WriteLine($"Server exe: {serverExe}");
        return (false, serverExe);
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

        // Allow 10s on slow CI for the 5s EOF shutdown grace period.
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
