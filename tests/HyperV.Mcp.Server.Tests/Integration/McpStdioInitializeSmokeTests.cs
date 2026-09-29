using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>Real-stdio MCP handshake: initialize, notifications/initialized and tools/list. Drain stdout and stderr before writing stdin;
/// the ~7.5 KB tools response exceeds the 4 KB Windows pipe buffer, so post-exit ReadToEnd deadlocks. Wait for stderr "Application started"
/// from the Generic Host (Trace logging to stderr); allow 60s for startup (warm ~5-8s, cold CI ~30s). Assert vm_echo, which needs no
/// Hyper-V infrastructure. Skip without vmms: startup imports Hyper-V and probes Get-VMHost, delaying or preventing readiness.</summary>
[Trait("Category", "Integration")]
[Trait("Category", "RequiresHyperV")]
public class McpStdioInitializeSmokeTests
{
    private readonly ITestOutputHelper _output;

    public McpStdioInitializeSmokeTests(ITestOutputHelper output)
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

    [Fact(Timeout = 180_000)]
    public async Task Initialize_AndToolsList_RespondsOverStdio()
    {
        // Missing Hyper-V permits a skip; a missing server executable with Hyper-V present is a build failure, not a skip. Report every
        // probed path.
        if (!IsHyperVAvailable())
        {
            _output.WriteLine("SKIP: Initialize_AndToolsList_RespondsOverStdio — Hyper-V (vmms service) not available.");
            return;
        }

        var serverExe = TestPaths.ServerExecutablePath();
        _output.WriteLine($"Server exe: {serverExe}");

        // MCP stdio requires UTF-8 without BOM; stdin is rewrapped after Start().
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

        using var proc = new Process { StartInfo = psi };

        // Drain both streams before stdin: the ~7.5 KB tools response exceeds the 4 KB Windows pipe buffer.
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();
        var stdoutLock = new object();
        var stderrLock = new object();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdoutLock) { stdoutSb.AppendLine(e.Data); }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrLock) { stderrSb.AppendLine(e.Data); }
        };

        string SnapshotStdout() { lock (stdoutLock) { return stdoutSb.ToString(); } }
        string SnapshotStderr() { lock (stderrLock) { return stderrSb.ToString(); } }

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Use UTF-8 without BOM and LF-delimited, autoflushed JSON-RPC frames.
        await using var stdinWriter = new StreamWriter(proc.StandardInput.BaseStream, utf8NoBom)
        {
            NewLine = "\n",
            AutoFlush = true,
        };

        try
        {
            const string readyMarker = "Application started";
            var readyDeadline = DateTime.UtcNow.AddSeconds(60);
            var ready = false;
            while (DateTime.UtcNow < readyDeadline)
            {
                if (proc.HasExited)
                {
                    Assert.Fail(
                        $"Server exited (code {proc.ExitCode}) before emitting readiness banner.\n" +
                        $"--- STDOUT ---\n{SnapshotStdout()}\n--- STDERR ---\n{SnapshotStderr()}");
                }
                if (SnapshotStderr().Contains(readyMarker, StringComparison.Ordinal))
                {
                    ready = true;
                    break;
                }
                await Task.Delay(200);
            }
            if (!ready)
            {
                Assert.Fail(
                    $"Server did not emit '{readyMarker}' within 60s.\n" +
                    $"--- STDOUT ---\n{SnapshotStdout()}\n--- STDERR ---\n{SnapshotStderr()}");
            }
            _output.WriteLine("Server emitted readiness banner.");

            const string initRequest      = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"smoke\",\"version\":\"1.0\"}}}";
            const string initializedNotif = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";
            const string toolsListRequest = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}";

            await stdinWriter.WriteLineAsync(initRequest);
            await stdinWriter.WriteLineAsync(initializedNotif);
            await stdinWriter.WriteLineAsync(toolsListRequest);

            var respDeadline = DateTime.UtcNow.AddSeconds(30);
            JsonDocument? resp1 = null;
            JsonDocument? resp2 = null;
            while (DateTime.UtcNow < respDeadline && (resp1 is null || resp2 is null))
            {
                var snapshot = SnapshotStdout();
                foreach (var rawLine in snapshot.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || !line.StartsWith("{")) continue;
                    JsonDocument? doc = null;
                    try { doc = JsonDocument.Parse(line); }
                    catch { continue; }

                    if (doc.RootElement.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.Number)
                    {
                        var id = idEl.GetInt32();
                        if (id == 1 && resp1 is null) { resp1 = doc; continue; }
                        if (id == 2 && resp2 is null) { resp2 = doc; continue; }
                    }
                    doc.Dispose();
                }
                if (resp1 is not null && resp2 is not null) break;
                if (proc.HasExited) break;
                await Task.Delay(200);
            }

            try
            {
                if (resp1 is null || resp2 is null)
                {
                    Assert.Fail(
                        $"Did not observe both id=1 and id=2 responses within 30s. " +
                        $"resp1={(resp1 is null ? "missing" : "ok")}, resp2={(resp2 is null ? "missing" : "ok")}.\n" +
                        $"--- STDOUT ---\n{SnapshotStdout()}\n--- STDERR ---\n{SnapshotStderr()}");
                }

                var r1 = resp1.RootElement;
                r1.TryGetProperty("result", out var r1Result).Should().BeTrue("initialize response must have a result");
                r1Result.TryGetProperty("protocolVersion", out var pv).Should().BeTrue();
                pv.ValueKind.Should().Be(JsonValueKind.String);
                pv.GetString().Should().NotBeNullOrEmpty();

                var r2 = resp2.RootElement;
                r2.TryGetProperty("result", out var r2Result).Should().BeTrue("tools/list response must have a result");
                r2Result.TryGetProperty("tools", out var tools).Should().BeTrue();
                tools.ValueKind.Should().Be(JsonValueKind.Array);
                tools.GetArrayLength().Should().BeGreaterThan(0);

                var toolNames = tools.EnumerateArray()
                    .Where(t => t.TryGetProperty("name", out _))
                    .Select(t => t.GetProperty("name").GetString())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
                _output.WriteLine($"tools/list returned {toolNames.Count} tool(s): {string.Join(", ", toolNames)}");
                toolNames.Should().Contain("vm_echo",
                    "vm_echo is the canonical health-check tool registered in VmTools.cs");
            }
            finally
            {
                resp1?.Dispose();
                resp2?.Dispose();
            }

            try { stdinWriter.Close(); } catch { }
            try { proc.StandardInput.Close(); } catch { }
            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                proc.WaitForExit(5_000);
                Assert.Fail(
                    $"Server did not exit within 15s after stdin close.\n" +
                    $"--- STDOUT ---\n{SnapshotStdout()}\n--- STDERR ---\n{SnapshotStderr()}");
            }
            proc.WaitForExit();
            proc.ExitCode.Should().Be(0,
                $"server should exit cleanly after stdin close.\n" +
                $"--- STDOUT ---\n{SnapshotStdout()}\n--- STDERR ---\n{SnapshotStderr()}");
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}
