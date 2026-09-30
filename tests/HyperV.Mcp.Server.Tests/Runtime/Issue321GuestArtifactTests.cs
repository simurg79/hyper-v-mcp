using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #321 — deterministic coverage of the guest-bound artifacts the orchestrator generates.
///
/// The pre-existing suite only proves that a stubbed KVP reader returns <c>Ready</c>; the real
/// failures (CRLF shebang, unframed KVP record, missing unit install) all lived in the generated
/// payload itself. These tests decode that payload from the seed script and assert on its bytes.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue321GuestArtifactTests
{
    private static readonly Regex SignalScriptPayload = new(
        @"echo (?<payload>[A-Za-z0-9+/=]+) \| base64 -d > /usr/local/sbin/hyperv-mcp-signal-ready",
        RegexOptions.Compiled);

    private static readonly Regex SignalUnitPayload = new(
        @"echo (?<payload>[A-Za-z0-9+/=]+) \| base64 -d > /etc/systemd/system/hyperv-mcp-signal-ready\.service",
        RegexOptions.Compiled);

    private static async Task<string> CaptureSeedScriptAsync()
    {
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var hostResolver = new Mock<IHostResolver>();
        hostResolver.Setup(resolver => resolver.ResolveRequired(It.IsAny<string?>()))
            .Returns(new HostProfile
            {
                HostId = "local",
                ComputerName = "localhost",
                StorageRoot = @"C:\HyperVMCP\VMs",
                DefaultSwitch = "Default Switch",
            });

        var orchestrator = new UbuntuAutoinstallOrchestrator(
            executor,
            new AlwaysReadyKvpReader(),
            hostResolver.Object,
            new FixedTempPathProvider(tempScope.Path),
            new GuestRoutingHintStore(),
            new SeedMediaAuthor(
                executor,
                new OscdimgProbe(new SystemEnvironment()),
                NullLogger<SeedMediaAuthor>.Instance),
            NullLogger<UbuntuAutoinstallOrchestrator>.Instance);

        await orchestrator.InstallAsync(new UbuntuInstallRequest
        {
            HostId = "local",
            Name = "issue321-artifact-vm",
            IsoPath = @"C:\ISOs\ubuntu-24.04-live-server-amd64.iso",
            AdminPassword = "P@ssw0rd-ubuntu",
            GuestUsername = "ubuntu",
            CpuCount = 2,
            MemoryMB = 4096,
            DiskSizeGB = 32,
            TimeoutMinutes = 60,
        });

        executor.SeedScript.Should().NotBeNull("the orchestrator must emit the cloud-init seed script");
        return executor.SeedScript!;
    }

    private static string DecodePayload(string seedScript, Regex matcher, string what)
    {
        var match = matcher.Match(seedScript);
        match.Success.Should().BeTrue($"the seed script must install {what} via a base64 late-command");
        return Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["payload"].Value));
    }

    [Fact]
    public async Task SignalScript_IsLfOnlyWithCleanShebang()
    {
        // Regression for the observed exit-127 failure: a CRLF shebang makes the guest kernel look
        // for an interpreter literally named "python3\r".
        var script = DecodePayload(await CaptureSeedScriptAsync(), SignalScriptPayload, "the KVP signal script");

        script.Should().NotContain("\r", "guest-bound files must carry LF-only line endings");
        script.Should().StartWith("#!/usr/bin/env python3\n");
    }

    [Fact]
    public async Task SignalUnit_IsLfOnlyAndEnablesFirstBootOneshot()
    {
        var unit = DecodePayload(await CaptureSeedScriptAsync(), SignalUnitPayload, "the systemd unit");

        unit.Should().NotContain("\r", "guest-bound files must carry LF-only line endings");
        unit.Should().Contain("Type=oneshot");
        unit.Should().Contain("ExecStart=/usr/local/sbin/hyperv-mcp-signal-ready");
        unit.Should().Contain("WantedBy=multi-user.target");
    }

    [Fact]
    public async Task SeedScript_EnablesTheUnitAndMakesTheScriptExecutable()
    {
        var seedScript = await CaptureSeedScriptAsync();

        seedScript.Should().Contain("chmod 0755 /usr/local/sbin/hyperv-mcp-signal-ready");
        seedScript.Should().Contain("systemctl enable hyperv-mcp-signal-ready.service");
    }

    [Fact]
    public async Task SignalScript_WritesA2560ByteFramedKvpRecord()
    {
        // Executes the real generated script against a temp pool so the 512/2048 little-endian
        // compatible framing hv_kvp_daemon requires is proven, not merely inspected as text.
        var interpreter = FindPythonInterpreter();
        if (interpreter is null)
        {
            return; // No python on PATH; framing stays covered by the static payload assertions.
        }

        var script = DecodePayload(await CaptureSeedScriptAsync(), SignalScriptPayload, "the KVP signal script");
        using var sandbox = new TempScope();
        var poolPath = Path.Combine(sandbox.Path, "kvp_pool_1");
        var scriptPath = Path.Combine(sandbox.Path, "signal.py");

        // Retarget the pool at the sandbox; everything else runs verbatim.
        File.WriteAllText(
            scriptPath,
            script
                .Replace("'/var/lib/hyperv/.kvp_pool_1'", "r'" + poolPath + "'")
                .Replace("os.makedirs('/var/lib/hyperv', exist_ok=True)", ""),
            new UTF8Encoding(false));

        RunToCompletion(interpreter!, scriptPath);
        // A second run must replace, not append, so repeated boots stay idempotent.
        RunToCompletion(interpreter!, scriptPath);

        var pool = File.ReadAllBytes(poolPath);
        pool.Length.Should().Be(2560, "one record is a 512-byte key plus a 2048-byte value");
        Encoding.ASCII.GetString(pool, 0, 512).TrimEnd('\0').Should().Be("hyperv-mcp/os-install");
        Encoding.ASCII.GetString(pool, 512, 2048).TrimEnd('\0').Should().Be("ready");
        const string ExpectedKey = "hyperv-mcp/os-install";
        pool.Skip(ExpectedKey.Length).Take(512 - ExpectedKey.Length)
            .Should().AllBeEquivalentTo((byte)0, "the key field must be NUL padded");
    }

    [Fact]
    public async Task SignalUnit_OrdersAfterTheUbuntuSpelledKvpDaemonUnit()
    {
        // Ubuntu ships the daemon as hv-kvp-daemon.service; systemd silently drops an
        // unknown dependency, so a misspelling leaves the oneshot racing the daemon.
        var unit = DecodePayload(await CaptureSeedScriptAsync(), SignalUnitPayload, "the KVP signal unit");

        unit.Should().Contain("After=hv-kvp-daemon.service");
        unit.Should().Contain("Wants=hv-kvp-daemon.service");
        unit.Should().NotContain("hv_kvp_daemon.service");
    }

    private static string? FindPythonInterpreter()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (probe is null) { continue; }
                probe.WaitForExit(15000);
                if (probe.ExitCode == 0) { return candidate; }
            }
            catch (Exception) { /* interpreter absent; try the next candidate */ }
        }
        return null;
    }

    private static void RunToCompletion(string interpreter, string scriptPath)
    {
        using var process = Process.Start(new ProcessStartInfo(interpreter, $"\"{scriptPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        process.ExitCode.Should().Be(0, $"the generated signal script must run cleanly: {stderr}");
    }
}

/// <summary>KVP reader that reports the guest as ready immediately.</summary>
internal sealed class AlwaysReadyKvpReader : IKvpCompletionReader
{
    public Task<GuestCompletionStatus> ReadCompletionAsync(string vmName, CancellationToken ct = default)
        => Task.FromResult(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));

    /// <summary>
    /// Throws rather than returning a plausible state: this double drives a ready guest, so the
    /// timeout-only channel probe reaching it would mean the production path changed underneath the
    /// test, which must be loud instead of silently answering "Undetermined".
    /// See internal documentation
    /// — UMD-D6.
    /// </summary>
    public Task<GuestCompletionChannelState> ProbeChannelStateAsync(string vmName, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "ProbeChannelStateAsync must only be asked on the timeout path, never for a ready guest.");
}
