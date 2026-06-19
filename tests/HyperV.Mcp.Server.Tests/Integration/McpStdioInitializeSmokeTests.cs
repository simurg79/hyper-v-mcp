using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
// Note: System.Text.Json is retained because Phase 3 parses JSON-RPC response
// frames below.

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// CI-runnable stdio smoke test that spawns the built HyperV.Mcp.Server.exe and
/// verifies the MCP handshake (initialize + notifications/initialized + tools/list)
/// over real stdio pipes.
///
/// <para>
/// Contract / design memory:
/// </para>
/// <list type="bullet">
///   <item><c>myplans/mcp-interface/mcp-interface-design.md</c> — MCP-D10:
///   client harnesses MUST async-drain BOTH stdout and stderr BEFORE writing
///   anything to stdin, MUST wait for the readiness banner on stderr, and MUST
///   NOT rely on a single post-WaitForExit ReadToEnd().</item>
///   <item><c>myplans/mcp-interface/mcp-interface-design.md</c> — Constraint #5:
///   Windows anonymous pipe buffer is 4 KB; tools/list response is ~7.5 KB,
///   so without async drain the server deadlocks on Console.Out.WriteLine.</item>
///   <item><c>myplans/phase2-smoke-test-plan.md</c> §6.1 — cold-start budget:
///   warm ~5–8 s, cold CI up to ~30 s. We use a 60 s readiness ceiling for CI.</item>
/// </list>
///
/// <para>
/// Readiness banner: <c>src/HyperV.Mcp.Server/Program.cs</c> uses
/// <c>Host.CreateApplicationBuilder</c> + <c>app.RunAsync()</c>, which emits the
/// standard ASP.NET Generic Host line <c>Application started. Press Ctrl+C to shut down.</c>
/// on stderr (logging is configured to <c>LogToStandardErrorThreshold = Trace</c> per MCP-D8).
/// We key on the substring <c>Application started</c>.
/// </para>
///
/// <para>
/// Tool-name assertion: <c>src/HyperV.Mcp.Server/Tools/VmTools.cs</c> registers
/// <c>vm_echo</c> as the health-check tool (line 27). It requires no Hyper-V
/// infrastructure to appear in <c>tools/list</c>, making it the safest target.
/// </para>
///
/// <para>
/// Hyper-V gate: matches the pattern in
/// <see cref="LiveEndToEndTests"/> — query the <c>vmms</c> service. The server
/// runs <c>EnsureInitializedAsync</c> at startup which imports the Hyper-V
/// module and probes <c>Get-VMHost</c>; without Hyper-V the readiness banner
/// is delayed/absent so we skip cleanly.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "RequiresHyperV")]
public class McpStdioInitializeSmokeTests
{
    private readonly ITestOutputHelper _output;

    public McpStdioInitializeSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Returns true when the host has Hyper-V (vmms service running). Mirrors
    /// the gate pattern used by <see cref="LiveEndToEndTests.CanRunLiveTests()"/>.
    /// </summary>
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

    /// <summary>
    /// Locates HyperV.Mcp.Server.exe by walking up from the test assembly's
    /// bin output to the repo root, then descending into the server bin folder
    /// for the same configuration (Debug/Release) and TFM (net8.0-windows).
    /// </summary>
    /// <summary>
    /// Result of a server-exe lookup: either a resolved path, or the list of
    /// candidate paths that were probed (used for diagnostics in
    /// <c>Assert.Fail</c> when the build output is missing).
    /// </summary>
    private readonly record struct ServerExeLookup(bool Found, string? Path, IReadOnlyList<string> AttemptedPaths);

    /// <summary>
    /// Locates HyperV.Mcp.Server.exe by walking up from the test assembly's
    /// bin output to the repo root, then descending into the server bin folder
    /// for the same configuration (Debug/Release) and TFM (net8.0-windows).
    /// Returns the full set of probed paths so callers can produce actionable
    /// diagnostics when the build output is missing.
    /// </summary>
    private static ServerExeLookup ResolveServerExePath()
    {
        var attempted = new List<string>();

        // AppContext.BaseDirectory ≈ tests/HyperV.Mcp.Server.Tests/bin/<Config>/net8.0-windows/
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);                                        // net8.0-windows
        var configDir = Path.GetDirectoryName(baseDir);                             // .../bin/<Config>
        var config = configDir is null ? null : Path.GetFileName(configDir);       // Debug or Release
        var binDir = configDir is null ? null : Path.GetDirectoryName(configDir);  // .../bin
        var testProjDir = binDir is null ? null : Path.GetDirectoryName(binDir);   // tests/HyperV.Mcp.Server.Tests
        var testsRoot = testProjDir is null ? null : Path.GetDirectoryName(testProjDir); // tests/
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

    [Fact(Timeout = 180_000)]
    public async Task Initialize_AndToolsList_RespondsOverStdio()
    {
        // Finding 4: distinguish two skip-vs-fail conditions.
        //   * Hyper-V missing  → legitimate environment SKIP (mirrors LiveEndToEndTests).
        //   * Hyper-V present but server exe missing → BUILD INTEGRITY FAILURE
        //     (would let CI pass without exercising the smoke test). Hard-fail
        //     with the full list of probed paths to make the cause obvious.
        if (!IsHyperVAvailable())
        {
            _output.WriteLine("SKIP: Initialize_AndToolsList_RespondsOverStdio — Hyper-V (vmms service) not available.");
            return;
        }

        var lookup = ResolveServerExePath();
        if (!lookup.Found || lookup.Path is null)
        {
            Assert.Fail(
                $"Server exe not found at any candidate path. " +
                $"Searched: {string.Join(", ", lookup.AttemptedPaths)}. " +
                $"Run a Debug build of HyperV.Mcp.Server first.");
        }
        var serverExe = lookup.Path!;
        _output.WriteLine($"Server exe: {serverExe}");

        // Finding 3: MCP stdio contract is UTF-8. Be explicit (no BOM) on
        // captured stdout/stderr; stdin is re-wrapped after Start() below.
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

        // MCP-D10: async drain BOTH streams BEFORE writing to stdin.
        // Constraint #5: Windows pipe buffer is 4 KB; tools/list response is ~7.5 KB.
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

        // Finding 3: write stdin as UTF-8 (no BOM) with LF newlines (the
        // JSON-RPC stdio convention) and AutoFlush so each frame is observable.
        await using var stdinWriter = new StreamWriter(proc.StandardInput.BaseStream, utf8NoBom)
        {
            NewLine = "\n",
            AutoFlush = true,
        };

        try
        {
            // Phase 1: wait up to 60 s for stderr "Application started" (cold CI ceiling).
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

            // Phase 2: write three JSON-RPC frames separated by '\n'.
            const string initRequest      = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"smoke\",\"version\":\"1.0\"}}}";
            const string initializedNotif = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";
            const string toolsListRequest = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}";

            await stdinWriter.WriteLineAsync(initRequest);
            await stdinWriter.WriteLineAsync(initializedNotif);
            await stdinWriter.WriteLineAsync(toolsListRequest);

            // Phase 3: wait up to 30 s for both id=1 and id=2 responses.
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

                // Assert id=1 result.protocolVersion is a non-empty string.
                var r1 = resp1.RootElement;
                r1.TryGetProperty("result", out var r1Result).Should().BeTrue("initialize response must have a result");
                r1Result.TryGetProperty("protocolVersion", out var pv).Should().BeTrue();
                pv.ValueKind.Should().Be(JsonValueKind.String);
                pv.GetString().Should().NotBeNullOrEmpty();

                // Assert id=2 result.tools is a non-empty array containing vm_echo
                // (verified in src/HyperV.Mcp.Server/Tools/VmTools.cs line 27).
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

            // Phase 4: close stdin and wait for clean exit.
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
            // Ensure async readers finish flushing.
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
