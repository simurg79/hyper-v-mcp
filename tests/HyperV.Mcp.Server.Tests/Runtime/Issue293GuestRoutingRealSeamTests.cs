using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #293 — guest calls against a Gen-2 Linux guest took arm D of
/// <c>GuestChannelRouter.Select</c> (the no-hint <c>hostIsLinux ? SSH : PSDirect</c> fall-through)
/// and landed on PowerShell Direct. Arm D is the only arm that logs nothing, which is how the
/// misroute stayed invisible.
///
/// <para>ANTI-MASK CONTRACT. The pre-existing per-VM routing suite pre-seeds the hint store by
/// hand, so <c>Select</c> is only ever reached in the already-classified state and arm D is never
/// exercised — that is precisely why PR #278 shipped 30 green tests over a non-functional feature
/// (issue #301). These guards therefore NEVER pre-seed a hint on the path under test: the hint must
/// be produced by the production <c>EnsureClassifiedAsync</c> → <c>IGuestOsProbe</c> sequence, which
/// is the decision path #293 fixed. Only the two transports beneath the router are faked, and only
/// so the selected channel is observable; the selection itself is entirely production code.</para>
///
/// See internal documentation — LGR-D7, LGR-D17.
/// Reference pattern: <see cref="Issue291VmPauseRequestedStateRealSeamTests"/>.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue293GuestRoutingRealSeamTests
{
    private const string Host = "local";
    private const string SshMarker = "SSH-CHANNEL-MARKER";
    private const string PsDirectMarker = "PSDIRECT-CHANNEL-MARKER";

    /// <summary>
    /// Records every probe call and answers from a per-(hostId, vmId) script. A concrete recording
    /// fake rather than a permissive mock, so an unexpected key yields "undeterminable" instead of
    /// a silently satisfying default.
    /// </summary>
    private sealed class RecordingGuestOsProbe : IGuestOsProbe
    {
        private readonly Dictionary<string, GuestRoutingHint> _linuxGuests;

        internal RecordingGuestOsProbe(Dictionary<string, GuestRoutingHint> linuxGuests)
            => _linuxGuests = linuxGuests;

        internal List<string> ProbedKeys { get; } = new();

        public Task<GuestRoutingHint?> ProbeAsync(string hostId, string vmId, CancellationToken ct)
        {
            var key = $"{hostId}::{vmId}";
            ProbedKeys.Add(key);
            return Task.FromResult(_linuxGuests.TryGetValue(key, out var hint) ? hint : null);
        }
    }

    private sealed class RouterHarness
    {
        internal required GuestChannelRouter Router { get; init; }
        internal required GuestRoutingHintStore HintStore { get; init; }
        internal required RecordingGuestOsProbe Probe { get; init; }
    }

    private static RouterHarness BuildRouter(
        HostProfile? hostProfile,
        Dictionary<string, GuestRoutingHint>? linuxGuests = null,
        GuestRoutingHintStore? hintStore = null)
    {
        var hints = hintStore ?? new GuestRoutingHintStore();

        var sshStore = new Mock<ISshSessionStore>();
        sshStore
            .Setup(s => s.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeSshExecClient(SshMarker));
        var sshChannel = new SshGuestChannel(sshStore.Object, NullLogger<SshGuestChannel>.Instance);

        var psHost = new Mock<IPowerShellHost>();
        psHost
            .Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(true, new object?[] { PsDirectMarker }, string.Empty, 0));
        psHost
            .Setup(h => h.InvokeWithTimeoutAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
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

        var hostResolver = new Mock<IHostResolver>();
        hostResolver.Setup(r => r.Resolve(It.IsAny<string?>())).Returns(hostProfile);

        var probe = new RecordingGuestOsProbe(linuxGuests ?? new Dictionary<string, GuestRoutingHint>());

        var router = new GuestChannelRouter(
            psChannel, sshChannel, hostResolver.Object, hints,
            NullLogger<GuestChannelRouter>.Instance, probe);

        return new RouterHarness { Router = router, HintStore = hints, Probe = probe };
    }

    private static HostProfile WindowsHost(string hostId = Host) => new()
    {
        HostId = hostId,
        ComputerName = "localhost",
        GuestOs = "windows",
    };

    private static string MarkerOf(PowerShellHostResult result)
    {
        result.Output.Should().NotBeNull().And.NotBeEmpty("every channel returns a non-empty envelope");
        return result.Output[0]?.ToString() ?? string.Empty;
    }

    // ── Guard 10: arm-D routing regression (#293) ────────────────────────────

    /// <summary>
    /// THE #293 guard. A Linux guest that the store has NO hint for must still reach the SSH
    /// channel, because the production classification step runs before the routing decision.
    ///
    /// <para>Before the fix the five facade methods called <c>Select</c> directly; with no hint and
    /// a non-Linux host profile, arm D returned the Windows channel and the guest's command went to
    /// PowerShell Direct. The host profile here is deliberately <c>windows</c>, so SSH can ONLY be
    /// reached via the per-VM classification the fix introduced — a fall-through to arm D produces
    /// the PSDirect marker and fails this test on an observed misroute.</para>
    /// </summary>
    [Fact]
    public async Task NoHint_LinuxGuest_ClassifiesBeforeRouting_ReachesSsh_NotPowerShellDirect()
    {
        const string vmId = "7aa0d399-a9ca-4c49-85c5-b24abd6650f8";
        var harness = BuildRouter(
            WindowsHost(),
            new Dictionary<string, GuestRoutingHint>
            {
                [$"{Host}::{vmId}"] = new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22),
            });

        // No hint is pre-seeded: the store starts empty, exactly as it does for the first guest
        // call against a freshly installed Linux VM.
        harness.HintStore.TryGet(Host, vmId, out _).Should().BeFalse(
            "the arm-D defect only exists in the no-hint state; pre-seeding would mask it");

        var result = await harness.Router.InvokeScriptAsync(
            Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        MarkerOf(result).Should().Contain(SshMarker,
            "#293: a Linux guest with no recorded hint MUST be classified before the routing " +
            "decision and reach the SSH channel. Arm D — the no-hint 'hostIsLinux ? SSH : PSDirect' " +
            "fall-through — sends it to PowerShell Direct, which is the defect.");
        MarkerOf(result).Should().NotContain(PsDirectMarker,
            "#293: a known-Linux guest MUST NEVER reach PowerShell Direct.");
        harness.Probe.ProbedKeys.Should().Contain($"{Host}::{vmId}",
            "the routing decision must be preceded by a real classification attempt");
    }

    /// <summary>
    /// The classification result must be durable: a second call reuses the recorded hint rather
    /// than re-probing on the hot path, and still routes to SSH.
    /// </summary>
    [Fact]
    public async Task NoHint_LinuxGuest_RecordsHint_SecondCallReusesItWithoutReprobing()
    {
        const string vmId = "7aa0d399-a9ca-4c49-85c5-b24abd6650f8";
        var harness = BuildRouter(
            WindowsHost(),
            new Dictionary<string, GuestRoutingHint>
            {
                [$"{Host}::{vmId}"] = new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22),
            });

        await harness.Router.InvokeScriptAsync(Host, vmId, "ubuntu", "pw", "A", args: null, ct: default);
        var second = await harness.Router.InvokeScriptAsync(Host, vmId, "ubuntu", "pw", "B", args: null, ct: default);

        MarkerOf(second).Should().Contain(SshMarker);
        harness.Probe.ProbedKeys.Should().ContainSingle(
            "a recorded hint must suppress re-probing on the guest hot path (LGR-D17)");
        harness.HintStore.TryGet(Host, vmId, out var hint).Should().BeTrue();
        hint!.GuestOs.Should().Be(GuestOsKind.Linux);
    }

    /// <summary>
    /// An undeterminable probe refuses. The pre-#332 expectation — degrade to PowerShell Direct —
    /// is SUPERSEDED: that fall-through was the fail-open defect, since the host running Windows is
    /// not evidence that the guest does.
    /// See internal documentation — LGR-D27, LGR-D30.
    /// </summary>
    [Fact]
    public async Task NoHint_UndeterminableGuest_Refuses_DoesNotSelectAnyTransport()
    {
        var harness = BuildRouter(WindowsHost());

        var thrown = await Assert.ThrowsAsync<GuestRoutingUnavailableException>(
            () => harness.Router.InvokeScriptAsync(
                Host, "vm-windows-nohint", "administrator", "pw", "Get-Thing", args: null, ct: default));

        thrown.VmId.Should().Be("vm-windows-nohint");
        thrown.Message.Should().Contain("not determined",
            "the refusal must carry the minimum recovery fact (LGR-D35)");
    }

    /// <summary>
    /// A faulting probe yields the ordinary refusal. It MUST NOT surface the probe's own exception
    /// type: every non-determination funnels into the single refusal over the existing
    /// <c>SESSION_FAILED</c> mapping.
    /// See internal documentation — LGR-D25 (C-2 note), LGR-T7.
    /// </summary>
    [Fact]
    public async Task ProbeThrows_RefusesWithoutSurfacingProbeException()
    {
        var throwingProbe = new Mock<IGuestOsProbe>();
        throwingProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe exploded"));

        var hints = new GuestRoutingHintStore();
        var sshStore = new Mock<ISshSessionStore>();
        sshStore
            .Setup(s => s.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeSshExecClient(SshMarker));
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
        var hostResolver = new Mock<IHostResolver>();
        hostResolver.Setup(r => r.Resolve(It.IsAny<string?>())).Returns(WindowsHost());

        var logs = new RecordingLogger<GuestChannelRouter>();
        var router = new GuestChannelRouter(
            new PowerShellDirectChannel(
                psHost.Object, psSessionStore.Object, NullLogger<PowerShellDirectChannel>.Instance),
            new SshGuestChannel(sshStore.Object, NullLogger<SshGuestChannel>.Instance),
            hostResolver.Object, hints, logs, throwingProbe.Object);

        var thrown = await Assert.ThrowsAsync<GuestRoutingUnavailableException>(
            () => router.InvokeScriptAsync(
                Host, "vm-probe-faults", "administrator", "pw", "Get-Thing", args: null, ct: default));

        thrown.VmId.Should().Be("vm-probe-faults");
        thrown.Message.Should().Contain("not determined");
        thrown.Message.Should().NotContain("probe exploded",
            "the probe's own failure text MUST NOT reach the caller");

        // A faulted probe is a different operational condition from one that answered "unknown".
        // Suppressing the caller-facing detail is only safe because the marker preserves the cause.
        logs.At(LogLevel.Warning).Should().Contain(
            message => message.Contains("refusing") && message.Contains("probe: faulted"),
            "LGR-D35: the refusal must carry the FAULT marker, not 'undeterminable'");
    }

    // ── Guard 1: two-VM compound-key host isolation ──────────────────────────

    /// <summary>
    /// The hint store and the router key on the compound <c>(hostId, vmId)</c>. A VM GUID alone is
    /// NOT unique across independent hosts — an import or clone reproduces one — so a Linux
    /// determination on host A must not drive routing for the same GUID on host B.
    /// </summary>
    [Fact]
    public async Task SameVmIdOnTwoHosts_LinuxOnHostA_DoesNotRouteHostBToSsh()
    {
        const string sharedVmId = "11111111-2222-3333-4444-555555555555";
        const string hostA = "host-a";
        const string hostB = "host-b";

        var hints = new GuestRoutingHintStore();

        // Host A's guest is Linux; host B's identically-identified guest is undeterminable.
        var harnessA = BuildRouter(
            WindowsHost(hostA),
            new Dictionary<string, GuestRoutingHint>
            {
                [$"{hostA}::{sharedVmId}"] = new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22),
            },
            hints);

        var resultA = await harnessA.Router.InvokeScriptAsync(
            hostA, sharedVmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);
        MarkerOf(resultA).Should().Contain(SshMarker, "host A's guest is Linux");

        // Same store, same VM GUID, different host.
        var harnessB = BuildRouter(WindowsHost(hostB), linuxGuests: null, hintStore: hints);

        // Host B's guest is undeterminable, so it refuses rather than routing (LGR-D27). The
        // compound-key property under test is that it does NOT inherit host A's Linux verdict:
        // a VM-only key would send host B's guest over SSH to another machine.
        var thrown = await Assert.ThrowsAsync<GuestRoutingUnavailableException>(
            () => harnessB.Router.InvokeScriptAsync(
                hostB, sharedVmId, "administrator", "pw", "Get-Thing", args: null, ct: default));

        thrown.Message.Should().Contain("not determined",
            "LGR-D7: a Linux result on one host MUST NOT leak into routing on another");
        hints.TryGet(hostB, sharedVmId, out _).Should().BeFalse(
            "LGR-D7: identity is the compound (hostId, vmId); host A's record is not host B's");
    }

    /// <summary>
    /// The store itself must keep the two records distinct, and removing one must not evict the
    /// other. Deletion keyed on the VM GUID alone would silently drop an unrelated host's hint.
    /// </summary>
    [Fact]
    public void HintStore_SameVmIdOnTwoHosts_RecordsAreIndependent_AndRemoveIsScoped()
    {
        const string sharedVmId = "11111111-2222-3333-4444-555555555555";
        var store = new GuestRoutingHintStore();

        var hintA = new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22);
        var hintB = new GuestRoutingHint(GuestOsKind.Linux, "192.168.9.9", 2222);
        store.Record("host-a", sharedVmId, hintA);
        store.Record("host-b", sharedVmId, hintB);

        store.TryGet("host-a", sharedVmId, out var gotA).Should().BeTrue();
        store.TryGet("host-b", sharedVmId, out var gotB).Should().BeTrue();
        gotA!.SshHost.Should().Be("10.0.0.5",
            "LGR-D7: the two hosts' records for one VM GUID MUST NOT collide onto a single key");
        gotB!.SshHost.Should().Be("192.168.9.9");

        store.Remove("host-a", sharedVmId);

        store.TryGet("host-a", sharedVmId, out _).Should().BeFalse();
        store.TryGet("host-b", sharedVmId, out var survivor).Should().BeTrue(
            "LGR-D7: removal MUST be scoped to one host; a VM-only key would evict both");
        survivor!.SshHost.Should().Be("192.168.9.9");
    }

    /// <summary>
    /// <c>ForgetGuestClassification</c> is the recreated-VM path. It must also be host-scoped, or
    /// destroying a VM on one host would silently reset another host's routing.
    /// </summary>
    [Fact]
    public async Task ForgetGuestClassification_IsHostScoped_DoesNotDisturbOtherHost()
    {
        const string sharedVmId = "11111111-2222-3333-4444-555555555555";
        const string hostA = "host-a";
        const string hostB = "host-b";
        var hints = new GuestRoutingHintStore();

        var harnessA = BuildRouter(
            WindowsHost(hostA),
            new Dictionary<string, GuestRoutingHint>
            {
                [$"{hostA}::{sharedVmId}"] = new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22),
            },
            hints);
        var harnessB = BuildRouter(
            WindowsHost(hostB),
            new Dictionary<string, GuestRoutingHint>
            {
                [$"{hostB}::{sharedVmId}"] = new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.6", 22),
            },
            hints);

        await harnessA.Router.InvokeScriptAsync(hostA, sharedVmId, "u", "p", "A", args: null, ct: default);
        await harnessB.Router.InvokeScriptAsync(hostB, sharedVmId, "u", "p", "B", args: null, ct: default);

        harnessA.Router.ForgetGuestClassification(hostA, sharedVmId);

        hints.TryGet(hostA, sharedVmId, out _).Should().BeFalse("host A's classification was dropped");
        hints.TryGet(hostB, sharedVmId, out var survivor).Should().BeTrue(
            "LGR-D7/LGR-D20: forgetting one host's classification MUST NOT evict another host's");
        survivor!.SshHost.Should().Be("10.0.0.6");
    }
}
