using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #370 (re-scoped) — a STOCK Ubuntu ISO was accepted, the VM booted media carrying no
/// <c>autoinstall</c> kernel argument, subiquity parked at its consent prompt, nothing installed,
/// and the caller got a bare <c>LINUX_INSTALL_TIMEOUT</c> 40 minutes later with no triage context.
///
/// Substitution is kept to the process boundary (<see cref="IPowerShellExecutor"/>) plus, where a
/// test needs it, a dispatch-recording orchestrator and a throwing KVP reader; the real
/// <c>IsoInspector</c>, <c>GuestOsClassifier</c>, <c>HyperVManager</c> and <c>ErrorMapper</c>
/// execute. A stubbed capability service or diagnostics assembler would prove nothing.
///
/// See internal documentation
/// — UMD-D1..UMD-D9.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue370UbuntuMediaCapabilityRealSeamTests
{
    private const string Host = "local";
    private const string VmName = "issue370-ubuntu-vm";
    private const string Password = "P@ssw0rd-ubuntu";

    private readonly ITestOutputHelper _output;

    public Issue370UbuntuMediaCapabilityRealSeamTests(ITestOutputHelper output) => _output = output;

    // ════════════════════════════════════════════════════════════════════
    // GRUB fixtures — text, not ISO images (UMD-D9).
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Stock Ubuntu 24.04 live-server GRUB menu: no autoinstall argument anywhere.</summary>
    private const string StockGrubConfiguration = @"set timeout=30
menuentry ""Try or Install Ubuntu Server"" {
	set gfxpayload=keep
	linux	/casper/vmlinuz  ---
	initrd	/casper/initrd
}
menuentry ""Test memory"" {
	linux16	/boot/memtest86+.bin
}
";

    /// <summary>Helper-produced media: the bare token on the installer kernel line.</summary>
    private const string PreparedGrubConfiguration = @"set timeout=30
menuentry ""Try or Install Ubuntu Server"" {
	set gfxpayload=keep
	linux	/casper/vmlinuz autoinstall ds=nocloud;s=/cdrom/  ---
	initrd	/casper/initrd
}
menuentry ""Test memory"" {
	linux16	/boot/memtest86+.bin
}
";

    // ════════════════════════════════════════════════════════════════════
    // Guard — token detection (UMD-D3). Revert: equality → Contains.
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    // Adversarial forms that MUST NOT be read as the token. Every one of these is green under
    // `Contains`, which is exactly the issue #289 substring bug class this closes.
    [InlineData("	linux /casper/vmlinuz noautoinstall ---")]
    [InlineData("	linux /casper/vmlinuz myautoinstallfoo ---")]
    [InlineData("	linux /casper/vmlinuz foo=autoinstall ---")]
    [InlineData("	linux /casper/vmlinuz autoinstall=1 ---")]
    [InlineData("	linux /casper/vmlinuz autoinstall=yes ---")]
    [InlineData("	linux /casper/vmlinuz autoinstall=0 ---")]
    // A comment and a menu title are not kernel-load lines, so the word never reaches the scan.
    [InlineData("# autoinstall\n	linux /casper/vmlinuz ---")]
    [InlineData("menuentry \"autoinstall\" {\n	linux /casper/vmlinuz ---")]
    // Arguments after '---' are handed to the BOOTED SYSTEM, not the installer.
    [InlineData("	linux /casper/vmlinuz --- autoinstall")]
    // The kernel IMAGE PATH is not an argument: a prepared ISO may well name its kernel this way.
    [InlineData("	linux /casper/autoinstall-vmlinuz ---")]
    // The degenerate case: an image path EQUAL to the token. Only this form distinguishes
    // "skip the image path" from "scan from the image path onward" (UMD-D3 step 4).
    [InlineData("	linux autoinstall ---")]
    public void Evaluate_AdversarialTokenForms_AreNotCapable(string grubConfiguration)
    {
        AutoinstallCapabilityEvaluator.Evaluate(grubConfiguration)
            .Should().Be(AutoinstallCapability.NotCapable,
                "only a whitespace-delimited argument STRING-EQUAL to 'autoinstall' may count (UMD-D3)");
    }

    [Theory]
    [InlineData("	linux /casper/vmlinuz autoinstall ---")]
    [InlineData("	linux16 /casper/vmlinuz autoinstall ---")]
    [InlineData("	linuxefi /casper/vmlinuz autoinstall ---")]
    // UMD-Q4: media carrying the token on a kernel line WITHOUT the '---' separator is accepted.
    [InlineData("	linux /casper/vmlinuz autoinstall ds=nocloud;s=/cdrom/")]
    public void Evaluate_BareTokenOnAnyKernelLoadLine_IsCapable(string grubConfiguration)
    {
        AutoinstallCapabilityEvaluator.Evaluate(grubConfiguration)
            .Should().Be(AutoinstallCapability.Capable);
    }

    [Fact]
    public void Evaluate_StockAndPreparedFixtures_AreDistinguished()
    {
        AutoinstallCapabilityEvaluator.Evaluate(StockGrubConfiguration)
            .Should().Be(AutoinstallCapability.NotCapable);
        AutoinstallCapabilityEvaluator.Evaluate(PreparedGrubConfiguration)
            .Should().Be(AutoinstallCapability.Capable,
                "the memtest linux16 line legitimately lacks the token, so a per-line universal " +
                "requirement would wrongly refuse prepared media (UMD-D3 step 5)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    // No qualifying kernel-load line at all — nothing was judged, so nothing may be asserted.
    [InlineData("set timeout=30\nmenuentry \"Boot\" {\n}\n")]
    public void Evaluate_NothingJudgeable_IsUndeterminable(string? grubConfiguration)
    {
        AutoinstallCapabilityEvaluator.Evaluate(grubConfiguration)
            .Should().Be(AutoinstallCapability.Undeterminable, "fail-closed, but never over-claim (UMD-D4)");
    }

    [Fact]
    public void RefusalMessage_Undeterminable_SaysCouldNotBeConfirmed_NotAbsent()
    {
        var message = AutoinstallCapabilityEvaluator.BuildRefusalMessage(
            AutoinstallCapability.Undeterminable, @"C:\ISOs\mystery.iso");

        message.Should().Contain("could not be confirmed",
            "the error must never claim more than was observed (UMD-D4)");
        message.Should().Contain(@"C:\ISOs\mystery.iso");
        message.Should().Contain("prepare-ubuntu-autoinstall-iso.py");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard — refusal placement + no VM / no dispatch (AC-20, FR-30).
    // Revert: move the check after VM creation → recorded scripts contain New-VM.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StockMedia_IsRefusedSynchronously_WithNoVmAndNoOrchestratorDispatch()
    {
        var executor = new UbuntuProbeScriptedExecutor(StockGrubConfiguration);
        var orchestrator = new DispatchRecordingOrchestrator();
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();

        var manager = BuildManager(executor, orchestrator, tempScope.Path, isoFile.Path);

        var act = async () => await manager.OsInstallAsync(
            Host, VmName, isoFile.Path, Password,
            cpuCount: 2, memoryMB: 4096, diskSizeGB: 32, guestUsername: "ubuntu");

        var refusal = (await act.Should().ThrowAsync<LinuxPreconditionUnmetException>(
            "stock media cannot install unattended and must be refused up front (FR-30/AC-20)")).Which;

        // FR-32 / AC-21 — all three mandated parts.
        refusal.Message.Should().Contain("autoinstall boot configuration");
        refusal.Message.Should().Contain(isoFile.Path);
        refusal.Message.Should().Contain("prepare-ubuntu-autoinstall-iso.py");

        orchestrator.Dispatched.Should().BeFalse("a refused request must never reach the orchestrator");
        executor.AnyScriptContains("New-VM").Should().BeFalse(
            "the refusal must precede VM creation — no VM may exist (AC-20)");
        executor.AnyScriptContains("Add-VMDvdDrive").Should().BeFalse("no media may be attached");
        executor.AnyScriptContains("Start-VM").Should().BeFalse("no wait may begin");

        // Proves the real recognition seam ran rather than a canned verdict, and that capability
        // added NO mount: two is the pre-existing baseline (the Windows probe, then the Ubuntu one).
        executor.MountScriptCount.Should().Be(2,
            "capability must be derived from the SAME mount recognition already performs — a third " +
            "mount would violate UMD-D2 / constraint 7");
    }

    [Fact]
    public async Task StockMedia_MapsThroughRealErrorMapper_ToLinuxPreconditionUnmet()
    {
        var executor = new UbuntuProbeScriptedExecutor(StockGrubConfiguration);
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();
        var manager = BuildManager(executor, new DispatchRecordingOrchestrator(), tempScope.Path, isoFile.Path);

        McpToolResponse response;
        try
        {
            await manager.OsInstallAsync(Host, VmName, isoFile.Path, Password,
                cpuCount: 2, memoryMB: 4096, diskSizeGB: 32);
            throw new Xunit.Sdk.XunitException("expected a refusal");
        }
        catch (Exception ex)
        {
            response = new ErrorMapper().MapException(ex);
        }

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.LinuxPreconditionUnmet,
            "the refusal reuses the existing code — no new error code is introduced (FR-31/UMD-D5)");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard — skipPreflight immunity (AC-23). Revert: move the check inside !skipPreflight.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StockMedia_WithSkipPreflightTrue_IsStillRefused()
    {
        var executor = new UbuntuProbeScriptedExecutor(StockGrubConfiguration);
        var orchestrator = new DispatchRecordingOrchestrator();
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();
        var manager = BuildManager(executor, orchestrator, tempScope.Path, isoFile.Path);

        var act = async () => await manager.OsInstallAsync(
            Host, VmName, isoFile.Path, Password,
            cpuCount: 2, memoryMB: 4096, diskSizeGB: 32, skipPreflight: true);

        await act.Should().ThrowAsync<LinuxPreconditionUnmetException>(
            "skipPreflight gates resource floors only; it MUST NOT bypass capability (FR-33/AC-23)");
        orchestrator.Dispatched.Should().BeFalse();
        executor.AnyScriptContains("New-VM").Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard — prepared media accepted (AC-22, FR-34).
    // Revert: emit GRUBCFG_B64 AFTER the terminal token → InspectionFailed → OS_NOT_SUPPORTED.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PreparedMedia_IsNotRefused_AndReachesTheOrchestrator()
    {
        var executor = new UbuntuProbeScriptedExecutor(PreparedGrubConfiguration);
        var orchestrator = new DispatchRecordingOrchestrator();
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();
        var manager = BuildManager(executor, orchestrator, tempScope.Path, isoFile.Path);

        var result = await manager.OsInstallAsync(
            Host, VmName, isoFile.Path, Password,
            cpuCount: 2, memoryMB: 4096, diskSizeGB: 32, guestUsername: "ubuntu");

        result.Should().NotBeNull();
        orchestrator.Dispatched.Should().BeTrue(
            "prepared media must never be refused for lacking autoinstall capability (FR-34/AC-22)");
    }

    /// <summary>
    /// The emitted script is asserted directly because the ordering it encodes is invisible to any
    /// test that supplies stdout itself: the driver compares the LAST non-empty line for equality, so
    /// a payload written after the terminal token makes EVERY Ubuntu ISO fail as InspectionFailed.
    /// The guarded read matters for the same reason — the script body runs under
    /// <c>$ErrorActionPreference='Stop'</c>.
    /// See internal documentation
    /// — UMD-D2.
    /// </summary>
    [Fact]
    public async Task EmittedCasperProbeScript_WritesPayloadBeforeTheTerminalToken_AndGuardsTheRead()
    {
        var executor = new UbuntuProbeScriptedExecutor(PreparedGrubConfiguration);
        var inspector = new IsoInspector(executor, NullLogger<IsoInspector>.Instance);

        await inspector.ProbeUbuntuMediaAsync(@"C:\ISOs\ubuntu.iso");

        var probeScript = executor.RecordedScripts.Single(
            script => script.Contains("UBUNTU_2404_OK", StringComparison.Ordinal));

        var payloadIndex = probeScript.IndexOf("GRUBCFG_B64:", StringComparison.Ordinal);
        var tokenIndex = probeScript.IndexOf("'UBUNTU_2404_OK'", StringComparison.Ordinal);

        payloadIndex.Should().BeGreaterThan(0, "the script must emit the GRUB payload");
        tokenIndex.Should().BeGreaterThan(payloadIndex,
            "the terminal token MUST be written last, or the last-non-empty-line driver breaks");

        probeScript.Should().Contain("grub.cfg");
        probeScript.Should().Contain("try {",
            "the grub.cfg read must be individually non-fatal under $ErrorActionPreference='Stop'");
    }

    [Fact]
    public async Task RealInspector_DecodesTheBase64Payload_FromTheSameMountAsRecognition()
    {
        var executor = new UbuntuProbeScriptedExecutor(PreparedGrubConfiguration);
        var inspector = new IsoInspector(executor, NullLogger<IsoInspector>.Instance);

        var probe = await inspector.ProbeUbuntuMediaAsync(@"C:\ISOs\ubuntu.iso");

        probe.Result.Should().Be(IsoMarkerProbeResult.Confirmed);
        probe.GrubConfiguration.Should().Be(PreparedGrubConfiguration);
        executor.MountScriptCount.Should().Be(1, "one probe means exactly one mount");
    }

    [Fact]
    public async Task RealInspector_CorruptBase64Payload_DegradesToNull_RatherThanThrowing()
    {
        var executor = new CorruptPayloadPowerShellExecutor();
        var inspector = new IsoInspector(executor, NullLogger<IsoInspector>.Instance);

        var probe = await inspector.ProbeUbuntuMediaAsync(@"C:\ISOs\ubuntu.iso");

        probe.Result.Should().Be(IsoMarkerProbeResult.Confirmed,
            "a corrupt payload must not break recognition");
        probe.GrubConfiguration.Should().BeNull("undecodable payload is treated as absent (UMD-D2)");
    }

    /// <summary>
    /// The design reviewer's non-blocking suggestion: an unreadable <c>grub.cfg</c> must degrade to a
    /// null payload (→ Undeterminable → refusal), NOT to <c>ERROR:</c> → <c>InspectionFailed</c> →
    /// <c>OS_NOT_SUPPORTED</c>, which is what an unguarded read under
    /// <c>$ErrorActionPreference='Stop'</c> would produce (UMD-D2).
    /// </summary>
    /// <summary>
    /// Behavioral counterpart to the script-text assertion above: the executor here MODELS the
    /// PowerShell semantics the guard depends on — under <c>$ErrorActionPreference='Stop'</c> a failing
    /// <c>grub.cfg</c> read terminates the script unless it sits inside its own try/catch. Deleting the
    /// suppression therefore flips this outcome from the Undeterminable refusal to
    /// <c>InspectionFailed</c> → <c>OS_NOT_SUPPORTED</c>, which no assertion on script text can catch.
    /// See internal documentation
    /// — UMD-D2.
    /// </summary>
    [Fact]
    public async Task FailingGrubRead_UnderStopPreference_RefusesAsUndeterminable_NotOsNotSupported()
    {
        var executor = new ThrowingGrubReadPowerShellExecutor();
        var orchestrator = new DispatchRecordingOrchestrator();
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();
        var manager = BuildManager(executor, orchestrator, tempScope.Path, isoFile.Path);

        McpToolResponse response;
        try
        {
            await manager.OsInstallAsync(Host, VmName, isoFile.Path, Password,
                cpuCount: 2, memoryMB: 4096, diskSizeGB: 32, guestUsername: "ubuntu");
            throw new Xunit.Sdk.XunitException("expected a refusal");
        }
        catch (Exception ex)
        {
            response = new ErrorMapper().MapException(ex);
        }

        executor.GrubReadWasGuarded.Should().BeTrue(
            "the emitted script must wrap the grub.cfg read in its own try/catch");
        response.ErrorCode.Should().Be(ErrorCodes.LinuxPreconditionUnmet,
            "an unreadable grub.cfg is a capability question, never a media-recognition failure");
        response.ErrorCode.Should().NotBe(ErrorCodes.OsNotSupported);
        response.Error.Should().Contain("could not be confirmed");
        orchestrator.Dispatched.Should().BeFalse();
    }

    [Fact]
    public async Task UnreadableGrubConfiguration_StillRecognizesUbuntu_AndRefusesRatherThanUnsupported()
    {
        var executor = new UbuntuProbeScriptedExecutor(grubConfiguration: null);
        var orchestrator = new DispatchRecordingOrchestrator();
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();
        var manager = BuildManager(executor, orchestrator, tempScope.Path, isoFile.Path);

        var act = async () => await manager.OsInstallAsync(
            Host, VmName, isoFile.Path, Password,
            cpuCount: 2, memoryMB: 4096, diskSizeGB: 32);

        var refusal = (await act.Should().ThrowAsync<LinuxPreconditionUnmetException>()).Which;
        refusal.Message.Should().Contain("could not be confirmed");
        orchestrator.Dispatched.Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard — channel tri-state over the REAL KvpCompletionReader (AC-24 a/b/c).
    // Revert: collapse Undetermined into Unavailable.
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("CHANNEL_AVAILABLE", GuestCompletionChannelState.AvailableNoSignal)]
    [InlineData("CHANNEL_UNAVAILABLE", GuestCompletionChannelState.Unavailable)]
    [InlineData("CHANNEL_NO_VM", GuestCompletionChannelState.Undetermined)]
    [InlineData("KVP_ERROR:Access denied", GuestCompletionChannelState.Undetermined)]
    [InlineData("some unrecognized banter", GuestCompletionChannelState.Undetermined)]
    public async Task RealKvpReader_ChannelProbe_MapsEachHostShapeToItsOwnState(
        string hostOutput, GuestCompletionChannelState expected)
    {
        var executor = new FixedOutputPowerShellExecutor(hostOutput);
        var reader = new KvpCompletionReader(executor, NullLogger<KvpCompletionReader>.Instance);

        var state = await reader.ProbeChannelStateAsync(VmName);

        state.Should().Be(expected, "the three states must be distinguishable by the caller (AC-24)");
    }

    [Fact]
    public async Task RealKvpReader_ChannelProbeTimeout_IsUndetermined_NotUnavailable()
    {
        var executor = new FixedOutputPowerShellExecutor("CHANNEL_AVAILABLE", timedOut: true);
        var reader = new KvpCompletionReader(executor, NullLogger<KvpCompletionReader>.Instance);

        (await reader.ProbeChannelStateAsync(VmName))
            .Should().Be(GuestCompletionChannelState.Undetermined);
    }

    [Fact]
    public async Task RealKvpReader_CompletionRead_KeepsPendingOnFailureSemantics()
    {
        // UMD-D6 explicitly leaves ReadCompletionAsync untouched: an absent completion key alone is
        // NOT evidence the channel is down, which is why the two questions have separate answers.
        var executor = new FixedOutputPowerShellExecutor("KVP_ERROR:boom");
        var reader = new KvpCompletionReader(executor, NullLogger<KvpCompletionReader>.Instance);

        var status = await reader.ReadCompletionAsync(VmName);

        status.Signal.Should().Be(GuestCompletionSignal.Pending);
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard — timeout composition through the real orchestrator + mapper (AC-24..26).
    // Revert: redact AFTER composition → the ISO path is dropped.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Timeout_CarriesAttachedIsoAndChannelState_InBothMessageAndDetails()
    {
        var stopwatch = Stopwatch.StartNew();
        var attachedIso = @"C:\ISOs\WRONG-media.iso";
        var executor = new TimeoutPathScriptedExecutor(
            attachedIsoPath: attachedIso, channelToken: "CHANNEL_AVAILABLE");
        using var tempScope = new TempScope();
        var orchestrator = BuildRealOrchestrator(executor, tempScope.Path);

        var act = async () => await orchestrator.InstallAsync(Request(timeoutMinutes: 0));
        var timeout = (await act.Should().ThrowAsync<LinuxInstallTimeoutException>()).Which;

        stopwatch.Stop();
        _output.WriteLine($"timeout path elapsed {stopwatch.ElapsedMilliseconds} ms; " +
                          $"scripts={executor.ScriptCount}");

        timeout.AttachedIsoPath.Should().Be(attachedIso,
            "the media ACTUALLY attached is what discriminates 'wrong media' from 'no signal' (UMD-D7)");
        timeout.ChannelState.Should().Be(GuestCompletionChannelState.AvailableNoSignal);

        timeout.Message.Should().Contain(attachedIso);
        timeout.Message.Should().Contain("available, but no completion signal arrived");

        var response = new ErrorMapper().MapException(timeout);
        response.ErrorCode.Should().Be(ErrorCodes.LinuxInstallTimeout);

        var details = JsonSerializer.Serialize(response.Details);
        details.Should().Contain(attachedIso.Replace("\\", "\\\\"));
        details.Should().Contain(nameof(GuestCompletionChannelState.AvailableNoSignal));

        // The probe is asked ONCE, at the deadline — not per poll (UMD-D6).
        executor.ChannelProbeCount.Should().Be(1);
    }

    [Theory]
    [InlineData("CHANNEL_UNAVAILABLE", GuestCompletionChannelState.Unavailable, "unavailable")]
    [InlineData("KVP_ERROR:boom", GuestCompletionChannelState.Undetermined, "undetermined")]
    public async Task Timeout_ReportsEachChannelState_Distinguishably(
        string channelToken, GuestCompletionChannelState expected, string expectedWording)
    {
        var executor = new TimeoutPathScriptedExecutor(
            attachedIsoPath: @"C:\ISOs\prepared.iso", channelToken: channelToken);
        using var tempScope = new TempScope();
        var orchestrator = BuildRealOrchestrator(executor, tempScope.Path);

        var act = async () => await orchestrator.InstallAsync(Request(timeoutMinutes: 0));
        var timeout = (await act.Should().ThrowAsync<LinuxInstallTimeoutException>()).Which;

        timeout.ChannelState.Should().Be(expected);
        timeout.Message.Should().Contain(expectedWording);

        var details = JsonSerializer.Serialize(new ErrorMapper().MapException(timeout).Details);
        details.Should().Contain(expected.ToString());
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard — attached-ISO undetermined (AC-25, FR-36).
    // Revert: echo request.IsoPath on read failure → this turns red.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Timeout_WhenAttachedIsoCannotBeRead_ReportsUndetermined_AndNeverEchoesTheRequestedPath()
    {
        var executor = new TimeoutPathScriptedExecutor(
            attachedIsoPath: null, channelToken: "CHANNEL_AVAILABLE");
        using var tempScope = new TempScope();
        var orchestrator = BuildRealOrchestrator(executor, tempScope.Path);
        var request = Request(timeoutMinutes: 0);

        var act = async () => await orchestrator.InstallAsync(request);
        var timeout = (await act.Should().ThrowAsync<LinuxInstallTimeoutException>()).Which;

        timeout.AttachedIsoPath.Should().BeNull();
        timeout.Message.Should().Contain("undetermined",
            "an unreadable item is reported as undetermined, never omitted or reported healthy (FR-36/AC-25)");

        var response = new ErrorMapper().MapException(timeout);
        var details = JsonSerializer.Serialize(response.Details);

        // The negative assertion UMD-D9 mandates: substituting the requested path would assert
        // precisely the fact under investigation.
        response.Error.Should().NotContain(request.IsoPath);
        timeout.Message.Should().NotContain(request.IsoPath);
        details.Should().NotContain(request.IsoPath.Replace("\\", "\\\\"));
    }

    /// <summary>
    /// A throwing channel probe must leave the state Undetermined. Claiming "available, but no
    /// completion signal arrived" from a probe that never answered would assert the very fact under
    /// investigation — the same over-claiming UMD-D4 forbids on the media side.
    /// See internal documentation
    /// — UMD-D6.
    /// </summary>
    [Fact]
    public async Task Timeout_WhenTheChannelProbeThrows_ReportsUndetermined_NotHealthy()
    {
        var executor = new TimeoutPathScriptedExecutor(
            attachedIsoPath: @"C:\ISOs\prepared.iso", channelToken: "CHANNEL_AVAILABLE");
        using var tempScope = new TempScope();
        var orchestrator = BuildRealOrchestrator(
            executor, tempScope.Path, new ThrowingChannelProbeKvpReader());

        var act = async () => await orchestrator.InstallAsync(Request(timeoutMinutes: 0));
        var timeout = (await act.Should().ThrowAsync<LinuxInstallTimeoutException>()).Which;

        timeout.ChannelState.Should().Be(GuestCompletionChannelState.Undetermined);
        timeout.Message.Should().Contain("undetermined");
        timeout.Message.Should().NotContain("available, but no completion signal arrived");
    }

    /// <summary>
    /// Kills the mutant that feeds the two mandated items THROUGH the redactor instead of appending
    /// them after it. The ISO name here matches the Unix-crypt shape the redactor strips, so routing
    /// it through sanitization replaces the very path the caller needs to see that the WRONG media
    /// was attached. <c>$</c> is a legal Windows filename character, so this is a real path, not a
    /// contrived string.
    /// See internal documentation
    /// — UMD-D8.
    /// </summary>
    [Fact]
    public void ComposeTimeoutMessage_RedactorHostilePath_SurvivesBecauseItIsAppendedAfterSanitization()
    {
        const string RedactorHostileIsoPath = @"C:\ISOs\$6$rounds$prepared.iso";

        var composed = UbuntuAutoinstallOrchestrator.ComposeTimeoutMessage(
            timeoutMinutes: 40,
            attachedIsoPath: RedactorHostileIsoPath,
            channelState: GuestCompletionChannelState.AvailableNoSignal);

        composed.Should().Contain(RedactorHostileIsoPath,
            "no redaction rule may strip a mandated triage item, whatever the path looks like");
        composed.Should().NotContain("***REDACTED***");
    }

    /// <summary>
    /// FR-37 / AC-26 as a falsifiable property, over the REAL
    /// <see cref="ErrorMapper.PrepareDiagnosticCause"/> path: credential-bearing input in the
    /// redacted region IS stripped from both <c>message</c> and <c>details</c>, while BOTH mandated
    /// triage items survive in both. The secret is supplied through the summary — the only region
    /// fed to the redactor — so removing the sanitization call, or moving the append inside that
    /// region, turns this red.
    /// See internal documentation
    /// — UMD-D8.
    /// </summary>
    [Fact]
    public void Timeout_CredentialBearingDiagnostics_AreRedacted_WhileBothMandatedItemsSurvive()
    {
        // The path is deliberately crypt-shaped ($ is legal in a Windows filename): routing the
        // mandated items through the redactor would strip it, so this one guard fails BOTH ways the
        // property can be broken — removing sanitization, or appending inside the redacted region.
        const string AttachedIso = @"C:\ISOs\$6$rounds$prepared.iso";
        const string SecretLiteral = "Sup3rS3cret-Adm1n-Pw!";

        // A shape the guest/host genuinely emits on a failed autoinstall, carrying the credential
        // both as the caller's literal password and as a cloud-init key the redactor matches.
        var rawCause =
            $"cloud-init failed: net user ubuntu {SecretLiteral} /add\n" +
            $"hashed_passwd: $6$rounds=4096$abcdefgh$Zx9QeR\n" +
            $"adminPassword={SecretLiteral}";

        var composed = UbuntuAutoinstallOrchestrator.ComposeTimeoutMessage(
            timeoutMinutes: 40,
            attachedIsoPath: AttachedIso,
            channelState: GuestCompletionChannelState.Unavailable,
            rawSummary: rawCause,
            password: SecretLiteral);

        var timeout = new LinuxInstallTimeoutException(
            composed,
            timeoutMinutes: 40,
            vmId: "11111111-2222-3333-4444-555555555555",
            vmName: VmName,
            attachedIsoPath: AttachedIso,
            channelState: GuestCompletionChannelState.Unavailable);

        var response = new ErrorMapper().MapException(timeout);
        var details = JsonSerializer.Serialize(response.Details);

        // The secret must not reach either surface.
        response.Error.Should().NotContain(SecretLiteral,
            "a credential in the diagnostic cause must be redacted before it reaches the caller (FR-37)");
        details.Should().NotContain(SecretLiteral);
        response.Error.Should().Contain("***REDACTED***",
            "the credential-bearing summary really did pass through the redactor");

        // Both mandated items must survive in BOTH surfaces (AC-26).
        response.Error.Should().Contain(AttachedIso,
            "the attached media path is a mandated triage item and must outlive redaction");
        response.Error.Should().Contain("unavailable",
            "the channel state is a mandated triage item and must outlive redaction");
        details.Should().Contain(AttachedIso.Replace("\\", "\\\\"));
        details.Should().Contain(nameof(GuestCompletionChannelState.Unavailable));
    }

    // ════════════════════════════════════════════════════════════════════
    // Shared harness.
    // ════════════════════════════════════════════════════════════════════

    private static UbuntuInstallRequest Request(int timeoutMinutes = 60) => new()
    {
        HostId = Host,
        Name = VmName,
        IsoPath = @"C:\ISOs\requested-by-the-caller.iso",
        AdminPassword = Password,
        GuestUsername = "ubuntu",
        CpuCount = 2,
        MemoryMB = 4096,
        DiskSizeGB = 32,
        TimeoutMinutes = timeoutMinutes,
    };

    private static ServerOptions BuildOptions(string storageRoot) => new()
    {
        DefaultHostId = Host,
        Hosts = new Dictionary<string, HostProfile>
        {
            [Host] = new HostProfile
            {
                HostId = Host,
                ComputerName = "localhost",
                TrustPolicy = "local",
                StorageRoot = storageRoot,
                BaseVhdxPath = Path.Combine(storageRoot, "base.vhdx"),
            },
        },
    };

    /// <summary>
    /// REAL <see cref="IsoInspector"/> → REAL <see cref="GuestOsClassifier"/> → REAL
    /// <see cref="HyperVManager"/>. Only the PowerShell boundary is substituted.
    /// </summary>
    private static HyperVManager BuildManager(
        IPowerShellExecutor executor,
        IUbuntuAutoinstallOrchestrator orchestrator,
        string storageRoot,
        string isoPath)
    {
        var options = BuildOptions(storageRoot);
        var inspector = new IsoInspector(executor, NullLogger<IsoInspector>.Instance);
        var classifier = new GuestOsClassifier(inspector, NullLogger<GuestOsClassifier>.Instance);

        return new HyperVManager(
            executor,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            inspector,
            fileSystemProbe: null,
            baseImageHashCache: null,
            guestOsClassifier: classifier,
            ubuntuOrchestrator: orchestrator);
    }

    /// <summary>REAL orchestrator over the REAL KVP reader; only PowerShell is scripted.</summary>
    private static UbuntuAutoinstallOrchestrator BuildRealOrchestrator(
        IPowerShellExecutor executor, string stagingTempPath, IKvpCompletionReader? kvpReader = null)
    {
        var hostResolver = new HostResolver(BuildOptions(stagingTempPath));
        return new UbuntuAutoinstallOrchestrator(
            executor,
            kvpReader ?? new KvpCompletionReader(executor, NullLogger<KvpCompletionReader>.Instance),
            hostResolver,
            new FixedTempPathProvider(stagingTempPath),
            new GuestRoutingHintStore(),
            new SeedMediaAuthor(
                executor,
                new OscdimgProbe(new SystemEnvironment()),
                NullLogger<SeedMediaAuthor>.Instance),
            NullLogger<UbuntuAutoinstallOrchestrator>.Instance);
    }
}

// ════════════════════════════════════════════════════════════════════════
// Test doubles — all at IPowerShellExecutor, the process boundary (UMD-D9).
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Emits the probe stdout shapes the REAL <see cref="IsoInspector"/> parses, in the UMD-D2 normative
/// order (payload first, terminal token last). Nothing about recognition or capability is canned —
/// the production script driver, base64 decode and evaluator all run for real.
/// </summary>
internal sealed class UbuntuProbeScriptedExecutor : IPowerShellExecutor
{
    private readonly string? _grubConfiguration;
    private readonly List<string> _scripts = new();

    public UbuntuProbeScriptedExecutor(string? grubConfiguration) => _grubConfiguration = grubConfiguration;

    /// <summary>How many ISO inspection mounts were issued — constraint 7 must keep this at one.</summary>
    public int MountScriptCount { get; private set; }

    public bool AnyScriptContains(string needle)
        => _scripts.Exists(script => script.Contains(needle, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> RecordedScripts => _scripts;

    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        _scripts.Add(script);

        if (script.Contains("Get-DiskImage", StringComparison.Ordinal)
            && !script.Contains("Mount-DiskImage", StringComparison.Ordinal))
        {
            return Result("NOT_MOUNTED");
        }

        if (script.Contains("Mount-DiskImage", StringComparison.Ordinal))
        {
            MountScriptCount++;

            // The Windows probe runs first and must report a clean negative so classification
            // proceeds to the Ubuntu probe (ISO-D22 ordering).
            if (script.Contains("install.wim", StringComparison.Ordinal))
            {
                return Result("NO_WIM");
            }

            var stdout = new StringBuilder();
            if (_grubConfiguration is not null)
            {
                stdout.Append("GRUBCFG_B64:")
                      .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(_grubConfiguration)))
                      .Append('\n');
            }
            stdout.Append("UBUNTU_2404_OK");
            return Result(stdout.ToString());
        }

        return Result(string.Empty);
    }

    private static Task<PowerShellResult> Result(string stdout)
        => Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = stdout });
}

/// <summary>
/// Completion reads behave normally (never ready), but the channel probe throws — the host-side
/// failure the orchestrator's catch branch exists for.
/// </summary>
internal sealed class ThrowingChannelProbeKvpReader : IKvpCompletionReader
{
    public Task<GuestCompletionStatus> ReadCompletionAsync(string vmName, CancellationToken ct = default)
        => Task.FromResult(new GuestCompletionStatus(GuestCompletionSignal.Pending, null));

    public Task<GuestCompletionChannelState> ProbeChannelStateAsync(string vmName, CancellationToken ct = default)
        => throw new InvalidOperationException("WMI query failed.");
}

/// <summary>
/// Models the PowerShell host for a mount whose <c>grub.cfg</c> read FAILS. The emitted script is
/// interpreted rather than pattern-matched for a canned answer: when the read is wrapped in its own
/// try/catch the failure is swallowed and the script still reaches its terminal token; when it is
/// not, <c>$ErrorActionPreference='Stop'</c> unwinds to the outer catch and the script emits
/// <c>ERROR:</c>. That is precisely the regression the suppression exists to prevent.
/// </summary>
internal sealed class ThrowingGrubReadPowerShellExecutor : IPowerShellExecutor
{
    /// <summary>Whether the inspected script guarded its grub.cfg read.</summary>
    public bool GrubReadWasGuarded { get; private set; }

    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        if (!script.Contains("Mount-DiskImage", StringComparison.Ordinal))
        {
            return Result("NOT_MOUNTED");
        }
        if (script.Contains("install.wim", StringComparison.Ordinal))
        {
            return Result("NO_WIM");
        }

        GrubReadWasGuarded = ReadIsIndividuallyGuarded(script);
        return GrubReadWasGuarded
            ? Result("UBUNTU_2404_OK")
            : Result("ERROR:Access to the path 'D:\\boot\\grub\\grub.cfg' is denied.");
    }

    /// <summary>
    /// True when a <c>try</c>/<c>catch</c> pair encloses the read between the grub path assignment
    /// and the terminal token — the section the script's own comment declares must be non-fatal.
    /// </summary>
    private static bool ReadIsIndividuallyGuarded(string script)
    {
        var readIndex = script.IndexOf("ReadAllBytes", StringComparison.Ordinal);
        if (readIndex < 0) return false;

        var tokenIndex = script.IndexOf("'UBUNTU_2404_OK'", StringComparison.Ordinal);
        if (tokenIndex < readIndex) return false;

        var openIndex = script.LastIndexOf("try {", readIndex, StringComparison.Ordinal);
        var pathIndex = script.IndexOf("$grubPath", StringComparison.Ordinal);
        if (openIndex < 0 || openIndex < pathIndex) return false;

        var catchIndex = script.IndexOf("catch", readIndex, StringComparison.Ordinal);
        return catchIndex > 0 && catchIndex < tokenIndex;
    }

    private static Task<PowerShellResult> Result(string stdout)
        => Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = stdout });
}

/// <summary>Emits an undecodable payload line ahead of a valid terminal token.</summary>
internal sealed class CorruptPayloadPowerShellExecutor : IPowerShellExecutor
{
    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        if (script.Contains("Mount-DiskImage", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "GRUBCFG_B64:!!!not-base64!!!\nUBUNTU_2404_OK",
            });
        }
        return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "NOT_MOUNTED" });
    }
}

/// <summary><see cref="IUbuntuAutoinstallOrchestrator"/> that records whether it was dispatched.</summary>
internal sealed class DispatchRecordingOrchestrator : IUbuntuAutoinstallOrchestrator
{
    public bool Dispatched { get; private set; }

    public Task<OsInstallResult> InstallAsync(UbuntuInstallRequest request, CancellationToken ct = default)
    {
        Dispatched = true;
        return Task.FromResult(new OsInstallResult { Name = request.Name, VmId = "dispatched" });
    }
}

/// <summary>Returns one fixed stdout for every script — drives the real KVP reader's parsing.</summary>
internal sealed class FixedOutputPowerShellExecutor : IPowerShellExecutor
{
    private readonly string _stdout;
    private readonly bool _timedOut;

    public FixedOutputPowerShellExecutor(string stdout, bool timedOut = false)
    {
        _stdout = stdout;
        _timedOut = timedOut;
    }

    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
        => Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = _stdout, TimedOut = _timedOut });
}

/// <summary>
/// Drives the REAL orchestrator down its timeout path: seed and VM-create succeed, the completion
/// KVP never appears, the identity read reports (or fails to report) the attached DVD path, and the
/// channel probe returns a scripted token.
/// </summary>
internal sealed class TimeoutPathScriptedExecutor : IPowerShellExecutor
{
    private readonly string? _attachedIsoPath;
    private readonly string _channelToken;

    public TimeoutPathScriptedExecutor(string? attachedIsoPath, string channelToken)
    {
        _attachedIsoPath = attachedIsoPath;
        _channelToken = channelToken;
    }

    public int ScriptCount { get; private set; }
    public int ChannelProbeCount { get; private set; }

    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        ScriptCount++;

        if (script.Contains("autoinstall:", StringComparison.Ordinal))
        {
            return Result("STAGE_OK");
        }
        if (script.Contains("SEED_AUTHOR=", StringComparison.Ordinal))
        {
            return Result("SEED_AUTHOR=imapi2");
        }
        if (script.Contains("Get-VMIntegrationService", StringComparison.Ordinal))
        {
            ChannelProbeCount++;
            return Result(_channelToken);
        }
        if (script.Contains("New-VM", StringComparison.Ordinal))
        {
            return Result("{\"vmId\":\"11111111-2222-3333-4444-555555555555\",\"state\":\"Running\"}");
        }
        if (script.Contains("attachedIsoPath", StringComparison.Ordinal))
        {
            // A failed DVD read yields a null field — the orchestrator must NOT fall back to the
            // requested path.
            var dvd = _attachedIsoPath is null
                ? "null"
                : "\"" + _attachedIsoPath.Replace("\\", "\\\\") + "\"";
            return Result(
                "{\"vmId\":\"11111111-2222-3333-4444-555555555555\",\"state\":\"Running\"," +
                "\"attachedIsoPath\":" + dvd + "}");
        }

        // Completion polls: no KVP instance is ever returned, so the deadline is reached.
        return Result("KVP_NONE");
    }

    private static Task<PowerShellResult> Result(string stdout)
        => Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = stdout });
}
