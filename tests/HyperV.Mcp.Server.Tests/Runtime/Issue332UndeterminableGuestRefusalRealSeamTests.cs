using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #332 — an undeterminable guest OS selected PowerShell Direct because the host runs
/// Windows. PR #304 added KVP classification in FRONT of that fall-through but never removed it, so
/// routing degraded back into the #293 misroute on every probe miss.
///
/// <para>ANTI-MASK CONTRACT. A mocked <c>IPowerShellExecutor</c> captures script text without
/// executing it, so guards written against it go green over dead code (PR #278 / issue #301). These
/// guards therefore run the REAL <see cref="GuestChannelRouter"/>,
/// <see cref="GuestRoutingHintStore"/>, <see cref="PowerShellDirectChannel"/> and
/// <see cref="ErrorMapper"/>; only the two transports' outermost seams are faked, so the selected
/// channel is observable. The hint store is never pre-seeded on the path under test.</para>
///
/// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D27..LGR-D37.
/// Reference pattern: <see cref="Issue330VmPauseFailureDiagnosabilityRealSeamTests"/>.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue332UndeterminableGuestRefusalRealSeamTests
{
    private const string Host = "local";
    private const string SshMarker = "SSH-CHANNEL-MARKER";
    private const string PsDirectMarker = "PSDIRECT-CHANNEL-MARKER";

    /// <summary>Answers from a script and counts calls, so probe suppression is observable.</summary>
    private sealed class ScriptedGuestOsProbe : IGuestOsProbe
    {
        private readonly Func<string, string, GuestRoutingHint?> _answer;

        internal ScriptedGuestOsProbe(Func<string, string, GuestRoutingHint?> answer)
            => _answer = answer;

        internal int ProbeCount { get; private set; }

        public Task<GuestRoutingHint?> ProbeAsync(string hostId, string vmId, CancellationToken ct)
        {
            ProbeCount++;
            return Task.FromResult(_answer(hostId, vmId));
        }
    }

    /// <summary>Never completes until the budget cancels it — the C-2 / AC-15 case.</summary>
    private sealed class HangingGuestOsProbe : IGuestOsProbe
    {
        public async Task<GuestRoutingHint?> ProbeAsync(string hostId, string vmId, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return null;
        }
    }

    private sealed class RouterHarness
    {
        internal required GuestChannelRouter Router { get; init; }
        internal required GuestRoutingHintStore HintStore { get; init; }
        internal required Mock<IPowerShellHost> PsHost { get; init; }
        internal required Mock<IHostResolver> HostResolver { get; init; }

        internal required RecordingLogger<GuestChannelRouter> Logs { get; init; }
    }

    private static HostProfile WindowsHost() => new()
    {
        HostId = Host,
        ComputerName = "localhost",
        GuestOs = "windows",
    };

    private static RouterHarness BuildRouter(
        IGuestOsProbe? probe,
        HostProfile? hostProfile = null,
        GuestRoutingHintStore? hintStore = null,
        TimeProvider? timeProvider = null)
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
        var benign = new PowerShellHostResult(true, new object?[] { PsDirectMarker }, string.Empty, 0);
        psHost
            .Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(benign);
        psHost
            .Setup(h => h.InvokeWithTimeoutAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(benign);

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
        hostResolver.Setup(r => r.Resolve(It.IsAny<string?>())).Returns(hostProfile ?? WindowsHost());

        var logs = new RecordingLogger<GuestChannelRouter>();
        var router = new GuestChannelRouter(
            psChannel, sshChannel, hostResolver.Object, hints, logs, probe, timeProvider);

        return new RouterHarness
        {
            Router = router,
            HintStore = hints,
            PsHost = psHost,
            HostResolver = hostResolver,
            Logs = logs,
        };
    }

    // ── LGR-T9 / AC-11 / AC-12: the undeterminable refusal ───────────────────

    /// <summary>
    /// LGR-T9. Empty hint store, a Windows HOST profile, and an undeterminable probe: the guest OS
    /// is unknown, so no transport may be selected. Asserted POSITIVELY on the throw — a "did not
    /// return the Windows channel" assertion would pass vacuously, since a refusal returns no
    /// channel at all.
    ///
    /// <para>Pinned to the FIRST Select call: on a later call the LGR-D32 negative cache could
    /// suppress the probe and produce a refusal for the wrong reason.</para>
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D27, LGR-D30, LGR-T9.
    /// </summary>
    [Fact]
    public async Task UndeterminableGuest_OnWindowsHost_RefusesWithRecoveryFact_AndMapsToSessionFailed()
    {
        const string vmId = "3f2a1b4c-5d6e-7f80-9a1b-2c3d4e5f6071";
        var probe = new ScriptedGuestOsProbe((_, _) => null);
        var harness = BuildRouter(probe);

        harness.HintStore.TryGet(Host, vmId, out _).Should().BeFalse(
            "the defect only exists in the unclassified state; pre-seeding would mask it");

        var thrown = await Assert.ThrowsAsync<GuestRoutingUnavailableException>(
            () => harness.Router.InvokeScriptAsync(
                Host, vmId, "administrator", "pw", "Get-Thing", args: null, ct: default));

        probe.ProbeCount.Should().BeGreaterThan(0,
            "the refusal must follow a real first-call determination attempt, not the negative cache");
        thrown.VmId.Should().Be(vmId, "FR-HINT-6: diagnostics must name the target VM");
        thrown.Message.Should().Contain(vmId);
        thrown.Message.Should().Contain("not determined",
            "AC-12: the consumer must learn that this VM's guest OS was not determined");
        thrown.Message.Should().Contain("determinable",
            "AC-12: recovery is an action on the target guest, not a change to the request");

        // The REAL mapper, so the caller-facing code is proven rather than assumed (LGR-D30).
        var envelope = new ErrorMapper().MapException(thrown);
        envelope.Success.Should().BeFalse();
        envelope.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "LGR-D30: the refusal reuses the EXISTING session-failure code; no new code is added");

        harness.HintStore.TryGet(Host, vmId, out _).Should().BeFalse(
            "FR-HINT-4: an undeterminable attempt MUST NOT be recorded as a known classification");

        // AC-12 / FR-HINT-6 / LGR-D33: the refusal must be visible in diagnostics, naming the VM,
        // and must carry the marker of the outcome that ACTUALLY produced it (LGR-D35).
        var refusals = harness.Logs.At(LogLevel.Warning).Where(m => m.Contains("refusing")).ToList();
        refusals.Should().ContainSingle("LGR-D33: the refusal arm logs at Warning");
        refusals[0].Should().Contain(vmId, "FR-HINT-6: the Warning must name the target VM");
        refusals[0].Should().Contain("probe: undeterminable",
            "LGR-D35: the marker is the supported discriminator and must state the real outcome");
    }

    /// <summary>
    /// AC-11. No PowerShell-Direct session may be opened on the undeterminable path. The refusal is
    /// a Select-time decision raised strictly BEFORE any endpoint or session is resolved, so no
    /// session-store ownership is ever taken (LGR-D34).
    /// </summary>
    [Fact]
    public async Task UndeterminableGuest_OpensNoPowerShellDirectSession()
    {
        var harness = BuildRouter(new ScriptedGuestOsProbe((_, _) => null));

        await Assert.ThrowsAsync<GuestRoutingUnavailableException>(
            () => harness.Router.InvokeScriptAsync(
                Host, "vm-undeterminable", "administrator", "pw", "Get-Thing", args: null, ct: default));

        harness.PsHost.Invocations.Should().BeEmpty(
            "AC-11: an undeterminable guest MUST NOT reach the Windows-only transport — the host " +
            "running Windows is not evidence that the guest does");
    }

    /// <summary>
    /// AC-15 / C-2. A probe that never completes is abandoned at the classification budget and
    /// yields the refusal rather than a guessed transport, without hanging the caller.
    /// </summary>
    [Fact]
    public async Task ProbeNeverCompletes_AbandonsWithinBudget_RefusesRatherThanGuessing()
    {
        var harness = BuildRouter(new HangingGuestOsProbe());
        var started = DateTimeOffset.UtcNow;

        var thrown = await Assert.ThrowsAsync<GuestRoutingUnavailableException>(
            () => harness.Router.InvokeScriptAsync(
                Host, "vm-hangs", "administrator", "pw", "Get-Thing", args: null, ct: default));

        var elapsed = DateTimeOffset.UtcNow - started;
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "C-2: the command MUST NOT hang on classification");
        thrown.Message.Should().Contain("not determined");
        harness.PsHost.Invocations.Should().BeEmpty(
            "AC-15: exceeding the budget yields an undeterminable refusal, never a guessed transport");

        // A budget overrun is NOT the same operational condition as a probe that answered "unknown";
        // reporting one as the other defeats LGR-D35's only supported discriminator.
        harness.Logs.At(LogLevel.Warning).Should().Contain(
            message => message.Contains("refusing") && message.Contains("probe: timeout"),
            "LGR-D33/D35: a budget-exceeded refusal must carry the timeout marker, not 'undeterminable'");
    }

    // ── LGR-T12 / AC-3 residual: the known-Windows path is unchanged ─────────

    /// <summary>
    /// LGR-T12 / AC-3 (residual). PowerShell Direct requires a KNOWN-Windows determination, and on
    /// that path the invocation arguments stay byte-identical to today's — asserted at the real
    /// <see cref="IPowerShellHost"/> seam against a pre-classified control (LGS-SSH-D4).
    /// </summary>
    [Fact]
    public async Task KnownWindowsGuest_RoutesToPowerShellDirect_WithByteIdenticalArguments()
    {
        const string vmId = "9c8b7a65-4321-4fed-8ba9-876543210fed";
        var probe = new ScriptedGuestOsProbe((_, _) => new GuestRoutingHint(GuestOsKind.Windows, null, 0));
        var probed = BuildRouter(probe);

        var result = await probed.Router.InvokeScriptAsync(
            Host, vmId, "administrator", "pw", "Get-Thing", args: null, ct: default);

        result.Output[0]?.ToString().Should().Contain(PsDirectMarker,
            "a known-Windows determination must select PowerShell Direct");
        probed.HintStore.TryGet(Host, vmId, out var recorded).Should().BeTrue();
        recorded!.GuestOs.Should().Be(GuestOsKind.Windows,
            "LGR-D28: a positive Windows determination is recorded as a known classification");

        // Control: the same call with the classification already recorded, i.e. today's path.
        var control = BuildRouter(probe: null, hintStore: SeededWindows(vmId));
        await control.Router.InvokeScriptAsync(
            Host, vmId, "administrator", "pw", "Get-Thing", args: null, ct: default);

        ScriptOf(probed.PsHost).Should().Be(ScriptOf(control.PsHost),
            "AC-3 residual: the known-Windows PSDirect path must be byte-unchanged");

        // LGR-D33 requires SUCCESSFUL selections to be diagnostics-visible too, not only misses;
        // without this the PSDirect arm was the one arm with no marker at all.
        probed.Logs.At(LogLevel.Debug).Should().Contain(
            message => message.Contains("PowerShell Direct") && message.Contains(vmId),
            "LGR-D33: every routing decision — including the successful one — carries a Debug marker");
    }

    private static GuestRoutingHintStore SeededWindows(string vmId)
    {
        var store = new GuestRoutingHintStore();
        store.Record(Host, vmId, new GuestRoutingHint(GuestOsKind.Windows, null, 0));
        return store;
    }

    private static string ScriptOf(Mock<IPowerShellHost> psHost)
    {
        var invocation = psHost.Invocations.Should().ContainSingle(
            "the routed call must reach the PowerShell host exactly once").Subject;
        return invocation.Arguments[0]?.ToString() ?? string.Empty;
    }

    // ── LGR-T10 / AC-7 / AC-14: a determinable Linux guest still routes ──────

    /// <summary>
    /// AC-7 / AC-14. A Linux VM that was never registered and never installed by the autoinstall
    /// flow routes its FIRST guest-interaction command to SSH, on a host reporting Windows — so
    /// classification is never inferred from the host OS in either direction.
    /// </summary>
    [Fact]
    public async Task FirstCommand_DeterminableLinuxGuest_OnWindowsHost_RoutesToSsh()
    {
        const string vmId = "5e4d3c2b-1a09-4876-b5c4-d3e2f1a09876";
        var probe = new ScriptedGuestOsProbe((_, _) => new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22));
        var harness = BuildRouter(probe);

        var result = await harness.Router.InvokeScriptAsync(
            Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        result.Output[0]?.ToString().Should().Contain(SshMarker,
            "AC-7: the first command against a determinable Linux guest routes to SSH");
        harness.PsHost.Invocations.Should().BeEmpty(
            "AC-14: a known-Linux guest MUST NEVER reach PowerShell Direct");
    }

    /// <summary>
    /// LGR-T10 / AC-9 / FR-HINT-7. A prior undeterminable attempt suppresses the PROBE only, never
    /// the refusal, and never past the expiry: once the guest becomes determinable a later command
    /// routes by that determination instead of repeating the refusal.
    ///
    /// <para>Driven by an injected clock rather than wall-clock sleeps (LGR-T6).</para>
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D31, LGR-D32.
    /// </summary>
    [Fact]
    public async Task PriorUndeterminable_SuppressesProbeNotRefusal_AndReclassifiesAfterExpiry()
    {
        const string vmId = "2b3c4d5e-6f70-4819-a2b3-c4d5e6f70819";
        var determinable = false;
        var probe = new ScriptedGuestOsProbe(
            (_, _) => determinable ? new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22) : null);

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
        var harness = BuildRouter(probe, timeProvider: clock);

        async Task<GuestRoutingUnavailableException> RefusalAsync() =>
            await Assert.ThrowsAsync<GuestRoutingUnavailableException>(
                () => harness.Router.InvokeScriptAsync(
                    Host, vmId, "u", "pw", "Get-Thing", args: null, ct: default));

        await RefusalAsync();
        // One classification round may read more than once — LGR-D36 permits a best-effort
        // in-budget re-read — so suppression is measured as "no FURTHER reads", not as a count of 1.
        var afterFirstRound = probe.ProbeCount;
        afterFirstRound.Should().BeGreaterThan(0, "the first call must really attempt determination");

        await RefusalAsync();
        await RefusalAsync();

        probe.ProbeCount.Should().Be(afterFirstRound,
            "LGR-D31: a burst of calls collapses onto ONE classification round — the cache suppresses the probe");
        // A cache-suppressed refusal is a different operational condition from a probe that ran and
        // answered "unknown"; conflating them defeats LGR-D35's only supported discriminator.
        harness.Logs.At(LogLevel.Warning).Should().Contain(
            message => message.Contains("refusing")
                && message.Contains("probe: suppressed-negative-cache"),
            "LGR-D35: a cache-suppressed refusal must say so rather than claim a fresh undeterminable probe");
        // The decisive half of LGR-D31: suppression must never turn a refusal into a fall-through.
        harness.PsHost.Invocations.Should().BeEmpty(
            "LGR-D31: the negative cache suppresses the PROBE only, NEVER the refusal");

        // The guest becomes determinable; past the expiry the next command must re-probe.
        determinable = true;
        clock.Advance(TimeSpan.FromSeconds(6));

        var result = await harness.Router.InvokeScriptAsync(
            Host, vmId, "ubuntu", "pw", "Get-Thing", args: null, ct: default);

        probe.ProbeCount.Should().BeGreaterThan(afterFirstRound,
            "an expired entry must not suppress re-determination");
        result.Output[0]?.ToString().Should().Contain(SshMarker,
            "AC-9 / FR-HINT-7: a VM that has become determinable routes by that determination");
    }

    /// <summary>
    /// LGR-D35. Two interleaved routing attempts against the SAME VM must each report the cause of
    /// their OWN attempt. The interleaving is forced at the real seam: the host resolver runs a
    /// second, cache-suppressed attempt to completion in the middle of the first attempt's
    /// selection, so any cause held per VM rather than per attempt is relabelled before the first
    /// refusal is raised.
    /// </summary>
    [Fact]
    public async Task InterleavedRoutingAttempts_EachReportItsOwnCause()
    {
        const string vmId = "7a6b5c4d-3e2f-4109-8877-665544332211";
        var harness = BuildRouter(new ScriptedGuestOsProbe((_, _) => null));
        var reentered = false;

        harness.HostResolver.Setup(resolver => resolver.Resolve(It.IsAny<string?>())).Returns(() =>
        {
            if (!reentered)
            {
                reentered = true;
                // The nested attempt is cache-suppressed and must NOT relabel the outer one.
                Assert.ThrowsAsync<GuestRoutingUnavailableException>(
                    () => harness.Router.InvokeScriptAsync(
                        Host, vmId, "u", "pw", "Get-Thing", args: null, ct: default))
                    .GetAwaiter().GetResult();
            }
            return WindowsHost();
        });

        await Assert.ThrowsAsync<GuestRoutingUnavailableException>(
            () => harness.Router.InvokeScriptAsync(
                Host, vmId, "u", "pw", "Get-Thing", args: null, ct: default));

        var refusals = harness.Logs.At(LogLevel.Warning).Where(m => m.Contains("refusing")).ToList();
        refusals.Should().HaveCount(2, "both attempts refuse");
        refusals.Should().Contain(
            message => message.Contains("probe: suppressed-negative-cache"),
            "the nested attempt was suppressed by the negative cache");
        refusals.Should().Contain(
            message => message.Contains("probe: undeterminable"),
            "LGR-D35: the outer attempt ran a real probe and MUST NOT inherit the nested attempt's cause");
    }

    /// <summary>Minimal manual-advance clock; the negative cache must be testable without sleeping.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        internal FakeTimeProvider(DateTimeOffset start) => _utcNow = start;

        internal void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    // ── LGR-D28: the three-valued rule at the classification seam ────────────

    /// <summary>
    /// LGR-D28's decisive case. An EMPTY intrinsic-item pool must be undeterminable. Implementing
    /// known-Windows as "both Linux predicates are false" would record known-Windows here, and
    /// under LGR-D31 that recorded classification then suppresses all further determination —
    /// reinstating the #332 fail-open permanently rather than fixing it.
    /// </summary>
    [Fact]
    public void EmptyIntrinsicItems_AreUndeterminable_NotKnownWindows()
    {
        var hint = KvpGuestOsProbe.BuildHint(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        hint.Should().BeNull(
            "LGR-D28: absence of evidence is not evidence of Windows; an empty KVP pool is undeterminable");
    }

    /// <summary>
    /// LGR-D28. A NoEvidence item contributes nothing and MUST NOT be read as agreeing with the
    /// other item, in either direction.
    /// </summary>
    [Theory]
    // One item says Windows, the other is absent ⇒ known-Windows.
    [InlineData("2", null, GuestOsKind.Windows)]
    [InlineData(null, "Windows Server 2022", GuestOsKind.Windows)]
    // One item says Linux, the other is absent ⇒ known-Linux.
    [InlineData("1", null, GuestOsKind.Linux)]
    [InlineData(null, "Ubuntu 24.04", GuestOsKind.Linux)]
    // Disagreement ⇒ Linux takes precedence; a Windows guest is never sent to SSH by this rule.
    [InlineData("2", "Ubuntu 24.04", GuestOsKind.Linux)]
    [InlineData("1", "Windows Server 2022", GuestOsKind.Linux)]
    // Blank is NoEvidence, exactly like absent.
    [InlineData("   ", null, null)]
    [InlineData(null, "   ", null)]
    public void ThreeValuedRule_EvaluatesEachItemIndependently(
        string? platformId, string? osName, GuestOsKind? expected)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (platformId is not null) items["OSPlatformId"] = platformId;
        if (osName is not null) items["OSName"] = osName;

        var hint = KvpGuestOsProbe.BuildHint(items);

        hint?.GuestOs.Should().Be(expected);
    }

    /// <summary>
    /// LGR-D29. <c>OSPlatformId</c> is an EXACT ordinal test on the trimmed value. Integer-parsing,
    /// prefix-matching or normalizing would each convert a malformed value into a PSDirect grant,
    /// so every near-miss must fail toward Linux.
    /// </summary>
    [Theory]
    [InlineData("2", GuestOsKind.Windows)]
    [InlineData(" 2 ", GuestOsKind.Windows)]
    [InlineData("20", GuestOsKind.Linux)]
    [InlineData("02", GuestOsKind.Linux)]
    [InlineData("2x", GuestOsKind.Linux)]
    [InlineData("2 0", GuestOsKind.Linux)]
    [InlineData("two", GuestOsKind.Linux)]
    public void PlatformId_UsesExactOrdinalEquality_NearMissesAreNotWindows(
        string platformId, GuestOsKind expected)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OSPlatformId"] = platformId,
        };

        KvpGuestOsProbe.BuildHint(items)!.GuestOs.Should().Be(expected);
    }

    /// <summary>
    /// LGR-D29. <c>OSName</c> uses word-boundary token matching, never <c>String.Contains</c>: a
    /// Linux name carrying the token as a substring must not be granted PowerShell Direct
    /// (issue #289 bug class).
    /// </summary>
    [Theory]
    [InlineData("Windows Server 2022", GuestOsKind.Windows)]
    [InlineData("WindowsLikeLinux", GuestOsKind.Linux)]
    [InlineData("Ubuntu 24.04 LTS", GuestOsKind.Linux)]
    public void OsName_UsesTokenMatching_NotSubstring(string osName, GuestOsKind expected)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OSName"] = osName,
        };

        KvpGuestOsProbe.BuildHint(items)!.GuestOs.Should().Be(expected);
    }
}
