using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #209 (S-A/S-B) — per-VM Linux SSH routing regression guards.
///
/// Spec  myplans/remoting/linux-guest-support/linux-guest-routing-spec.md — AC-1..AC-6.
/// Design myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D1..D10.
///
/// No live Hyper-V host or SSH server: the SSH path runs through a fake
/// <see cref="ISshExecClient"/> and the PSDirect path through a mock <see cref="IPowerShellHost"/>.
/// Each transport emits a distinct marker, so one public facade call reveals which channel
/// <see cref="GuestChannelRouter"/> selected.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue209PerVmSshRoutingTests
{
    private const string Host = "local";

    // Per-transport markers let one facade call prove which channel handled it, with no reflection
    // on the private Select() and no live host.
    private const string SshMarker = "SSH-CHANNEL-MARKER";
    private const string PsDirectMarker = "PSDIRECT-CHANNEL-MARKER";

    // Real router over real channels, each backed by a fake store so the chosen transport is
    // observable.
    private sealed class RouterHarness
    {
        internal required GuestChannelRouter Router { get; init; }
        internal required IGuestRoutingHintStore HintStore { get; init; }
    }

    private static RouterHarness BuildRouter(HostProfile? hostProfile, IGuestRoutingHintStore? hintStore = null)
    {
        var hints = hintStore ?? new GuestRoutingHintStore();

        var sshStore = new Mock<ISshSessionStore>();
        sshStore
            .Setup(s => s.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeSshExecClient(SshMarker));
        var sshChannel = new SshGuestChannel(sshStore.Object, NullLogger<SshGuestChannel>.Instance);

        // The mock session store must hand back a benign handle so ExecuteWithRetryAsync reaches
        // the host invocation.
        var psHost = new Mock<IPowerShellHost>();
        psHost
            .Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: new object?[] { PsDirectMarker },
                Stderr: string.Empty,
                ExitCode: 0));
        psHost
            .Setup(h => h.InvokeWithTimeoutAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: new object?[] { PsDirectMarker },
                Stderr: string.Empty,
                ExitCode: 0));
        var psSessionStore = new Mock<ISessionStore>();
        psSessionStore
            .Setup(s => s.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string hostId, string vmId, string _, string _, CancellationToken _) =>
                new SessionHandle(hostId, vmId, $"sess-{vmId}"));
        var psChannel = new PowerShellDirectChannel(
            psHost.Object, psSessionStore.Object, NullLogger<PowerShellDirectChannel>.Instance);

        var hostResolver = new Mock<IHostResolver>();
        hostResolver.Setup(r => r.Resolve(It.IsAny<string?>())).Returns(hostProfile);

        var router = new GuestChannelRouter(
            psChannel, sshChannel, hostResolver.Object, hints, NullLogger<GuestChannelRouter>.Instance);

        return new RouterHarness { Router = router, HintStore = hints };
    }

    private static HostProfile WindowsHost() => new()
    {
        HostId = Host,
        ComputerName = "localhost",
        GuestOs = "windows",
    };

    /// <summary>Extracts the transport marker from the facade result's JSON/plain output envelope.</summary>
    private static string MarkerOf(PowerShellHostResult result)
    {
        result.Output.Should().NotBeNull().And.NotBeEmpty("every channel returns a non-empty envelope");
        return result.Output[0]?.ToString() ?? string.Empty;
    }

    // AC-1 — a VM recorded as Linux WITH a usable SSH endpoint routes to the SSH channel.
    // The host profile is Windows, so the SSH marker can only come from the per-VM hint seam.
    [Fact]
    public async Task LinuxHintWithUsableEndpoint_RoutesToSshChannel()
    {
        var harness = BuildRouter(WindowsHost());
        const string vmId = "vm-linux-usable";
        harness.HintStore.Record(Host, vmId, new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22));

        var result = await harness.Router.InvokeScriptAsync(
            Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(SshMarker,
            "AC-1 / LGR-D3: a Linux hint with a usable SSH endpoint must route to the SSH channel, " +
            "NOT PowerShell Direct, even though the host property is 'windows'");
        MarkerOf(result).Should().NotContain(PsDirectMarker,
            "AC-1: the PSDirect transport must NOT be selected when a usable Linux hint exists");
    }

    // AC-4 — fail-closed: a Linux hint with NO usable SSH endpoint must NOT route to SSH and must
    // NOT fall through to Windows PSDirect. Select() throws GuestRoutingUnavailableException,
    // synchronously, which ErrorMapper maps to SESSION_FAILED.
    [Theory]
    [InlineData(null, 22)]    // null SshHost
    [InlineData("", 22)]      // empty SshHost
    [InlineData("   ", 22)]   // whitespace SshHost
    [InlineData("10.0.0.9", 0)]   // non-positive port
    [InlineData("10.0.0.9", -1)]  // negative port
    public void LinuxHintNoUsableEndpoint_FailsClosed_SyncThrow_MapsToSessionFailed(string? sshHost, int sshPort)
    {
        var harness = BuildRouter(WindowsHost());
        const string vmId = "vm-linux-unreachable";
        harness.HintStore.Record(Host, vmId, new GuestRoutingHint(GuestOsKind.Linux, sshHost, sshPort));

        // The Action discards the returned Task, so the synchronous Assert.Throws overload passes
        // only if Select() throws before any Task is produced — that is the contract under test.
        var thrown = Assert.Throws<GuestRoutingUnavailableException>(() =>
        {
            _ = harness.Router.InvokeScriptAsync(Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);
        });

        thrown.VmId.Should().Be(vmId);

        // The SAME synchronous exception maps through ErrorMapper to the existing SESSION_FAILED code.
        var response = new ErrorMapper().MapException(thrown);
        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "AC-4 / LGR-D3: a fail-closed Linux-routing failure must map to the existing SESSION_FAILED code");
    }

    // AC-3 — a Windows guest / no-hint VM still routes to PSDirect via the host-scoped default.
    [Fact(Skip = "Quarantined per LGR-T11 in myplans/remoting/linux-guest-support/linux-guest-routing-design.md: asserts the pre-#332 fail-open contract removed by LGR-D27; rewrite owned by issue #313.")]
    public async Task NoHint_WindowsHost_RoutesToPsDirect_Unchanged()
    {
        var harness = BuildRouter(WindowsHost());

        var result = await harness.Router.InvokeScriptAsync(
            Host, "vm-windows-nohint", "administrator", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(PsDirectMarker,
            "AC-3: a no-hint VM on a Windows host must keep the existing PowerShell Direct path");
        MarkerOf(result).Should().NotContain(SshMarker,
            "AC-3: the SSH channel must never be selected for a no-hint Windows VM");
    }

    [Fact(Skip = "Quarantined per LGR-T11 in myplans/remoting/linux-guest-support/linux-guest-routing-design.md: asserts the pre-#332 fail-open contract removed by LGR-D27; rewrite owned by issue #313.")]
    public async Task NoHint_NullHostProfile_RoutesToPsDirect()
    {
        // An unresolved host (null profile) must default to PSDirect, never throw or route to SSH.
        var harness = BuildRouter(hostProfile: null);

        var result = await harness.Router.InvokeScriptAsync(
            Host, "vm-null-profile", "administrator", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(PsDirectMarker,
            "AC-3: an unresolved host profile must default to PowerShell Direct");
    }

    // AC-5 — no single-name/host special-casing: a usable Linux hint routes to SSH regardless of
    // identifier shape.
    [Theory]
    [InlineData("local", "vm-1")]
    [InlineData("remote-host-7", "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")]
    [InlineData("HOST.example.com", "ubuntu box with spaces")]
    [InlineData("h", "x")]
    public async Task LinuxHint_RoutesToSsh_ForArbitraryIdentifiers_NoSpecialCasing(string hostId, string vmId)
    {
        var profile = new HostProfile { HostId = hostId, ComputerName = "localhost", GuestOs = "windows" };
        var harness = BuildRouter(profile);
        harness.HintStore.Record(hostId, vmId, new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22));

        var result = await harness.Router.InvokeScriptAsync(
            hostId, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(SshMarker,
            "AC-5: routing must be identifier-agnostic — arbitrary hostId/vmId route the same way");
    }

    [Theory]
    [InlineData("local", "vm-generic")]
    [InlineData("host-Z", "another-arbitrary-id")]
    public void LinuxHintUnreachable_FailsClosed_ForArbitraryIdentifiers(string hostId, string vmId)
    {
        var profile = new HostProfile { HostId = hostId, ComputerName = "localhost", GuestOs = "windows" };
        var harness = BuildRouter(profile);
        harness.HintStore.Record(hostId, vmId, new GuestRoutingHint(GuestOsKind.Linux, null, 22));

        Assert.Throws<GuestRoutingUnavailableException>(() =>
        {
            _ = harness.Router.InvokeScriptAsync(hostId, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);
        });
    }

    // Review #278 (Copilot) — an install-time hint whose IP resolution failed must NOT override a
    // working host-scoped SSH configuration: it still routes to SSH, never PSDirect, never throws.
    [Theory]
    [InlineData(null, 22)]
    [InlineData("", 22)]
    [InlineData("   ", 22)]
    [InlineData("10.0.0.9", 0)]
    [InlineData("10.0.0.9", -1)]
    public async Task LinuxHintNoUsableEndpoint_LinuxHost_RoutesToSshChannel(string? sshHost, int sshPort)
    {
        var profile = new HostProfile { HostId = Host, ComputerName = "localhost", GuestOs = "linux" };
        var harness = BuildRouter(profile);
        const string vmId = "vm-linux-hostssh";
        harness.HintStore.Record(Host, vmId, new GuestRoutingHint(GuestOsKind.Linux, sshHost, sshPort));

        var result = await harness.Router.InvokeScriptAsync(
            Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(SshMarker,
            "LGR-D8: an unusable per-VM hint must defer to the host-scoped SSH configuration, not fail closed");
        MarkerOf(result).Should().NotContain(PsDirectMarker,
            "LGR-D3: a known-Linux guest must never reach PowerShell Direct");
    }

    // A Linux host profile whose OWN endpoint is unusable is not a fallback: routing must still
    // fail closed synchronously rather than select SSH the session store could not resolve.
    [Theory]
    [InlineData("   ", 22)]
    [InlineData("localhost", 0)]
    [InlineData("localhost", -22)]
    public void LinuxHintNoUsableEndpoint_LinuxHostWithoutUsableEndpoint_FailsClosed(
        string hostSshHost, int hostSshPort)
    {
        var profile = new HostProfile
        {
            HostId = Host,
            ComputerName = "   ",
            GuestOs = "linux",
            SshHost = hostSshHost,
            SshPort = hostSshPort,
        };
        var harness = BuildRouter(profile);
        const string vmId = "vm-linux-nohostssh";
        harness.HintStore.Record(Host, vmId, new GuestRoutingHint(GuestOsKind.Linux, null, 22));

        var thrown = Assert.Throws<GuestRoutingUnavailableException>(() =>
        {
            _ = harness.Router.InvokeScriptAsync(Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);
        });

        thrown.VmId.Should().Be(vmId);
        new ErrorMapper().MapException(thrown).ErrorCode.Should().Be(ErrorCodes.SessionFailed);
    }

    // Regression guard for the 0dbf11c defect: the router-level fallback was ineffective because
    // the REAL SshSessionStore gave the hint exclusive ownership of endpoint resolution and threw.
    // A permissive ISshSessionStore mock cannot catch that, so this case wires the real store.
    [Theory]
    [InlineData(null, 22)]
    [InlineData("", 22)]
    [InlineData("   ", 22)]
    [InlineData("10.0.0.9", 0)]
    [InlineData("10.0.0.9", -1)]
    public async Task LinuxHintNoUsableEndpoint_RealSessionStore_ConnectsToHostScopedEndpoint(
        string? sshHost, int sshPort)
    {
        var profile = new HostProfile
        {
            HostId = Host,
            ComputerName = "host-fallback.example",
            GuestOs = "linux",
            SshPort = 2222,
        };
        var hints = new GuestRoutingHintStore();
        const string vmId = "vm-linux-realstore";
        hints.Record(Host, vmId, new GuestRoutingHint(GuestOsKind.Linux, sshHost, sshPort));

        var factory = new RecordingSshExecClientFactory(SshMarker);
        var harness = BuildRouterWithRealSshStore(profile, hints, factory);

        var result = await harness.Router.InvokeScriptAsync(
            Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(SshMarker,
            "the end-to-end fallback must actually open an SSH session, not fail with SESSION_FAILED");
        factory.LastHost.Should().Be("host-fallback.example",
            "an unusable hint must defer endpoint resolution to the host-scoped profile");
        factory.LastPort.Should().Be(2222);
    }

    [Fact]
    public async Task LinuxHintWithUsableEndpoint_RealSessionStore_UsesHintEndpoint()
    {
        // The hint stays authoritative when usable — a NAT'd guest address differs from ComputerName.
        var profile = new HostProfile
        {
            HostId = Host,
            ComputerName = "host-fallback.example",
            GuestOs = "linux",
            SshPort = 2222,
        };
        var hints = new GuestRoutingHintStore();
        const string vmId = "vm-linux-hint-wins";
        hints.Record(Host, vmId, new GuestRoutingHint(GuestOsKind.Linux, "192.168.7.42", 22));

        var factory = new RecordingSshExecClientFactory(SshMarker);
        var harness = BuildRouterWithRealSshStore(profile, hints, factory);

        var result = await harness.Router.InvokeScriptAsync(
            Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(SshMarker);
        factory.LastHost.Should().Be("192.168.7.42", "a usable hint must win over the host-scoped endpoint");
        factory.LastPort.Should().Be(22);
    }

    private static RouterHarness BuildRouterWithRealSshStore(
        HostProfile hostProfile, IGuestRoutingHintStore hints, ISshExecClientFactory factory)
    {
        var hostResolver = new Mock<IHostResolver>();
        hostResolver.Setup(r => r.Resolve(It.IsAny<string?>())).Returns(hostProfile);
        hostResolver.Setup(r => r.ResolveRequired(It.IsAny<string?>())).Returns(hostProfile);

        var sshStore = new SshSessionStore(
            factory, hostResolver.Object, hints, NullLogger<SshSessionStore>.Instance);
        var sshChannel = new SshGuestChannel(sshStore, NullLogger<SshGuestChannel>.Instance);

        var psHost = new Mock<IPowerShellHost>();
        psHost
            .Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(true, new object?[] { PsDirectMarker }, string.Empty, 0));
        var psSessionStore = new Mock<ISessionStore>();
        psSessionStore
            .Setup(s => s.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string hostId, string vmId, string _, string _, CancellationToken _) =>
                new SessionHandle(hostId, vmId, $"sess-{vmId}"));
        var psChannel = new PowerShellDirectChannel(
            psHost.Object, psSessionStore.Object, NullLogger<PowerShellDirectChannel>.Instance);

        var router = new GuestChannelRouter(
            psChannel, sshChannel, hostResolver.Object, hints, NullLogger<GuestChannelRouter>.Instance);

        return new RouterHarness { Router = router, HintStore = hints };
    }

    // AC-6 — GuestRoutingHintStore: Record/TryGet/Remove round-trip; clean miss on missing and
    // blank keys; HasUsableSshEndpoint semantics.
    [Fact]
    public void HintStore_RecordTryGetRemove_RoundTrips()
    {
        var store = new GuestRoutingHintStore();
        var hint = new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 2222);

        store.Record(Host, "vm-a", hint);

        store.TryGet(Host, "vm-a", out var got).Should().BeTrue("a recorded hint must be retrievable");
        got.Should().Be(hint, "TryGet must return the exact recorded hint");
        got.GuestOs.Should().Be(GuestOsKind.Linux);
        got.SshHost.Should().Be("10.0.0.5");
        got.SshPort.Should().Be(2222);

        store.Remove(Host, "vm-a");
        store.TryGet(Host, "vm-a", out var afterRemove).Should().BeFalse("a removed hint must be a clean miss");
        afterRemove.Should().BeNull("a missing key must yield the default out value");
    }

    [Fact]
    public void HintStore_TryGet_MissingKey_IsCleanMiss()
    {
        var store = new GuestRoutingHintStore();

        store.TryGet(Host, "never-recorded", out var hint).Should().BeFalse();
        hint.Should().BeNull("a missing key must yield false + default out, never a throw");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HintStore_TryGet_NullOrWhitespaceKey_IsCleanMiss_NoThrow(string? key)
    {
        var store = new GuestRoutingHintStore();

        var act = () => store.TryGet(Host, key!, out _);

        act.Should().NotThrow("a null/whitespace key must be a clean miss, mirroring Record/Remove");
        store.TryGet(Host, key!, out var hint).Should().BeFalse();
        hint.Should().BeNull();
    }

    [Theory]
    // HasUsableSshEndpoint is true ONLY when SshHost is non-blank AND SshPort > 0 (LGR-D3).
    [InlineData("10.0.0.5", 22, true)]
    [InlineData("10.0.0.5", 1, true)]
    [InlineData("host", 65535, true)]
    [InlineData(null, 22, false)]
    [InlineData("", 22, false)]
    [InlineData("   ", 22, false)]
    [InlineData("10.0.0.5", 0, false)]
    [InlineData("10.0.0.5", -1, false)]
    [InlineData(null, 0, false)]
    public void HasUsableSshEndpoint_TrueOnlyWhenHostNonBlankAndPortPositive(string? sshHost, int sshPort, bool expected)
    {
        var hint = new GuestRoutingHint(GuestOsKind.Linux, sshHost, sshPort);

        hint.HasUsableSshEndpoint.Should().Be(expected,
            "AC-6 / LGR-D3: an endpoint is usable iff SshHost is non-blank AND SshPort > 0");
    }

    // AC-2 (S-B) — after a successful Ubuntu autoinstall the per-VM hint is persisted with
    // GuestOs=Linux and the resolved guest IP / port 22, and OsInstallResult is unchanged.
    [Fact]
    public async Task UbuntuInstallSuccess_PersistsLinuxHint_WithResolvedSshEndpoint()
    {
        using var tempScope = new TempScope();
        var executor = new SshRoutingScriptedExecutor(guestIp: "192.168.7.42");
        var hintStore = new GuestRoutingHintStore();
        var orchestrator = BuildOrchestratorWithHintStore(executor, tempScope.Path, hintStore,
            new GuestCompletionStatus(GuestCompletionSignal.Ready, null));

        var result = await orchestrator.InstallAsync(UbuntuRequest());

        // Hint recording is a side effect, not a field on the result.
        result.Should().NotBeNull();
        result.VmId.Should().Be(SshRoutingScriptedExecutor.CreatedVmId);
        result.State.Should().Be("Running");
        result.GuestIpAddress.Should().Be("192.168.7.42");

        hintStore.TryGet(Host, SshRoutingScriptedExecutor.CreatedVmId, out var hint).Should().BeTrue(
            "AC-2 / LGR-D2 (S-B): a successful Ubuntu install must persist a per-VM routing hint");
        hint!.GuestOs.Should().Be(GuestOsKind.Linux, "the recorded hint must mark the guest as Linux");
        hint.SshHost.Should().Be("192.168.7.42", "the hint must carry the finalize-resolved guest IP");
        hint.SshPort.Should().Be(22, "the SSH default port for the freshly installed Ubuntu guest is 22");
        hint.HasUsableSshEndpoint.Should().BeTrue(
            "AC-1 continuity: the just-recorded hint must be immediately usable for SSH routing");
    }

    [Fact]
    public async Task UbuntuInstallSuccess_HintEnablesSshRouting_EndToEnd()
    {
        // The hint recorded by the install is exactly what the router needs — no re-recording.
        using var tempScope = new TempScope();
        var executor = new SshRoutingScriptedExecutor(guestIp: "192.168.7.42");
        var sharedHintStore = new GuestRoutingHintStore();
        var orchestrator = BuildOrchestratorWithHintStore(executor, tempScope.Path, sharedHintStore,
            new GuestCompletionStatus(GuestCompletionSignal.Ready, null));

        await orchestrator.InstallAsync(UbuntuRequest());

        var harness = BuildRouter(WindowsHost(), sharedHintStore);
        var result = await harness.Router.InvokeScriptAsync(
            Host, SshRoutingScriptedExecutor.CreatedVmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(SshMarker,
            "AC-2→AC-1: the hint persisted by a successful install must drive the router to SSH");
    }

    [Fact]
    public async Task DirectConstruction_SharedHintStore_InstallHintReachesRouter()
    {
        // The default (non-injected) orchestrator MUST record into the caller's store; a private
        // instance would leave install-recorded hints invisible to the router.
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();

        var sharedHintStore = new GuestRoutingHintStore();
        var executor = new SshRoutingScriptedExecutor(guestIp: "192.168.7.99");
        var classifier = new Mock<IGuestOsClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallTarget.UbuntuServer2404);
        // Issue #370 / UMD-D2: prepared media, so the capability guard does not refuse a routing
        // test for a reason unrelated to routing.
        classifier.Setup(c => c.ClassifyMediaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestOsClassification(
                InstallTarget.UbuntuServer2404, TestIsoInspector.PreparedGrubConfiguration));

        var options = new ServerOptions
        {
            DefaultHostId = Host,
            Hosts = new Dictionary<string, HostProfile>
            {
                [Host] = new HostProfile
                {
                    HostId = Host,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    StorageRoot = tempScope.Path,
                    BaseVhdxPath = Path.Combine(tempScope.Path, "base.vhdx"),
                },
            },
        };

        var manager = new HyperVManager(
            executor,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(found: false, casperFound: true),
            fileSystemProbe: null,
            baseImageHashCache: null,
            guestOsClassifier: classifier.Object,
            ubuntuOrchestrator: null,
            guestRoutingHintStore: sharedHintStore);

        // Small timeout so a regression in the scripted KVP path fails fast instead of
        // burning the default 60-minute install budget.
        await manager.OsInstallAsync(
            Host, "issue209-ubuntu-vm", isoFile.Path, "P@ssw0rd-ubuntu",
            cpuCount: 2, memoryMB: 4096, diskSizeGB: 32, timeoutMinutes: 1, guestUsername: "ubuntu");

        sharedHintStore.TryGet(Host, SshRoutingScriptedExecutor.CreatedVmId, out var hint).Should().BeTrue(
            "the default orchestrator must record into the hint store supplied by the caller");
        hint!.SshHost.Should().Be("192.168.7.99");

        var harness = BuildRouter(WindowsHost(), sharedHintStore);
        var result = await harness.Router.InvokeScriptAsync(
            Host, SshRoutingScriptedExecutor.CreatedVmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(SshMarker);
    }

    // AC-2 harness helpers.
    private static UbuntuInstallRequest UbuntuRequest() => new()
    {
        HostId = Host,
        Name = "issue209-ubuntu-vm",
        IsoPath = @"C:\ISOs\ubuntu-24.04-live-server-amd64.iso",
        AdminPassword = "P@ssw0rd-ubuntu",
        GuestUsername = "ubuntu",
        CpuCount = 2,
        MemoryMB = 4096,
        DiskSizeGB = 32,
        TimeoutMinutes = 60,
    };

    private static UbuntuAutoinstallOrchestrator BuildOrchestratorWithHintStore(
        IPowerShellExecutor executor, string stagingTempPath,
        IGuestRoutingHintStore hintStore, GuestCompletionStatus kvp)
    {
        var resolver = new Mock<IHostResolver>();
        resolver.Setup(r => r.ResolveRequired(It.IsAny<string?>()))
            .Returns(new HostProfile
            {
                HostId = Host,
                ComputerName = "localhost",
                StorageRoot = @"C:\HyperVMCP\VMs",
                DefaultSwitch = "Default Switch",
            });

        var reader = new Mock<IKvpCompletionReader>();
        reader.Setup(r => r.ReadCompletionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(kvp);

        return new UbuntuAutoinstallOrchestrator(
            executor,
            reader.Object,
            resolver.Object,
            new FixedTempPathProvider(stagingTempPath),
            hintStore,
            new SeedMediaAuthor(
                executor,
                new OscdimgProbe(new SystemEnvironment()),
                NullLogger<SeedMediaAuthor>.Instance),
            NullLogger<UbuntuAutoinstallOrchestrator>.Instance);
    }
}

// Test doubles.

/// <summary>
/// Captures the endpoint the real <see cref="SshSessionStore"/> resolved, so a test can prove the
/// host-scoped fallback is honoured end to end rather than only at the routing decision.
/// </summary>
internal sealed class RecordingSshExecClientFactory : ISshExecClientFactory
{
    private readonly string _marker;
    public RecordingSshExecClientFactory(string marker) => _marker = marker;

    internal string? LastHost { get; private set; }
    internal int LastPort { get; private set; }

    public Task<ISshExecClient> ConnectAsync(
        string host, int port, string username, string password, CancellationToken ct = default)
    {
        LastHost = host;
        LastPort = port;
        return Task.FromResult<ISshExecClient>(new FakeSshExecClient(_marker));
    }
}

/// <summary>
/// Echoes a fixed marker in stdout so the SSH channel yields an observable envelope without a live
/// SSH server, matching the shape in
/// myplans/remoting/linux-guest-support/linux-ssh-exec-first-slice-design.md (LGS-SSH-D5).
/// </summary>
internal sealed class FakeSshExecClient : ISshExecClient
{
    private readonly string _marker;
    public FakeSshExecClient(string marker) => _marker = marker;

    public bool IsConnected => true;

    public Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default)
        => Task.FromResult(new SshCommandResult(Stdout: _marker, Stderr: string.Empty, ExitStatus: 0));

    // Routing fixtures never transfer. Throwing keeps this fake from standing in for a transfer.
    public Task UploadAsync(string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
        => throw new NotSupportedException("This exec-only fake does not perform SFTP transfers.");

    public Task DownloadAsync(string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
        => throw new NotSupportedException("This exec-only fake does not perform SFTP transfers.");

    public void Dispose() { }
}

/// <summary>
/// Drives the Ubuntu orchestrator to a successful finalize whose JSON carries a caller-supplied
/// guest IP, so the persisted hint's SSH host can be asserted. Mirrors the Issue #208
/// <c>ScriptedPowerShellExecutor</c> phase recognition.
/// </summary>
internal sealed class SshRoutingScriptedExecutor : IPowerShellExecutor
{
    internal const string CreatedVmId = "99999999-8888-7777-6666-555555555555";

    private readonly string _guestIp;
    public SshRoutingScriptedExecutor(string guestIp) => _guestIp = guestIp;

    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        // The default (non-injected) orchestrator builds a real KvpCompletionReader over this
        // executor, so the completion KVP MUST be answerable here or the poll runs to its deadline.
        if (script.Contains("Msvm_KvpExchangeComponent", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout =
                    "<INSTANCE CLASSNAME=\"Msvm_KvpExchangeDataItem\">"
                    + "<PROPERTY NAME=\"Name\"><VALUE>hyperv-mcp/os-install</VALUE></PROPERTY>"
                    + "<PROPERTY NAME=\"Data\"><VALUE>ready</VALUE></PROPERTY>"
                    + "</INSTANCE>",
            });
        }

        if (script.Contains("autoinstall:", StringComparison.Ordinal) ||
            script.Contains("SEED_OK", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "SEED_OK" });
        }

        if (script.Contains("Remove-VMDvdDrive", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = JsonSerializer.Serialize(new
                {
                    vmId = CreatedVmId,
                    name = "issue209-ubuntu-vm",
                    state = "Running",
                    processorCount = 2,
                    memoryMB = 4096L,
                    guestIpAddress = _guestIp,
                }),
            });
        }

        if (script.Contains("New-VM", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = JsonSerializer.Serialize(new { vmId = CreatedVmId, state = "Running" }),
            });
        }

        return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "{}" });
    }
}
