using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #208 (TC-L01) — the misclassification regression guard.
///
/// A Linux ISO that is NOT a supported target (not Windows, not recognized Ubuntu 24.04) must
/// resolve to OS_NOT_SUPPORTED, NEVER LINUX_PROVISION_FAILED. The defect: an inconclusive/failing
/// inspection (an `ERROR:*` token, unrecognized/absent output, a timeout, or a caught exception)
/// was collapsed indistinguishably from a clean negative and could reach the Ubuntu orchestrator.
///
/// Deliberately NON-LIVE and UN-GATED (runs in ordinary CI): drives the REAL
/// <see cref="GuestOsClassifier"/> over the REAL <see cref="IsoInspector"/> backed by a FAKE
/// <see cref="IPowerShellExecutor"/> returning the inconclusive outputs. No host, ISO, mount, or env
/// opt-in — the fake reproduces terminal outputs the stubbed <c>TestIsoInspector</c> could never
/// surface (ISO-D33, the "mock re-shadowing trap").
///
/// Red-before / green-after (ISO-D33): the fix tells a POSITIVE confirmation apart from a clean
/// negative AND an inconclusive failure via <see cref="IsoMarkerProbeResult"/>. Pre-fix the real
/// casper probe collapsed `ERROR:*` / unrecognized / timeout to the SAME <c>Found=false</c> the
/// classifier trusted with only a bool, so
/// <see cref="RealInspector_InconclusiveInspection_IsInspectionFailed_NotCleanNegative"/> FAILS
/// unfixed and PASSES after the fix. Classifier-level assertions pin that the collapse can never
/// resolve UbuntuServer2404.
///
/// See internal documentation — ISO-D16.1;
/// internal documentation — ISO-D32 / ISO-D33.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue208OsNotSupportedErrorCodeTests
{
    private const string IsoPath = @"C:\ISOs\some-unprobeable-linux.iso";

    private readonly ITestOutputHelper _output;

    public Issue208OsNotSupportedErrorCodeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static IsoInspector RealInspectorOver(IPowerShellExecutor fakeExecutor)
        => new(fakeExecutor, NullLogger<IsoInspector>.Instance);

    private static GuestOsClassifier RealClassifierOver(IPowerShellExecutor fakeExecutor)
        // REAL inspector + REAL classifier — only the PowerShell boundary is faked, so the
        // inspector's actual terminal-output parsing (and its fail-closed collapse) is exercised.
        => new(RealInspectorOver(fakeExecutor), NullLogger<GuestOsClassifier>.Instance);

    public static IEnumerable<object[]> InconclusiveExecutors()
    {
        // Each fake reproduces a distinct non-happy inspection outcome that MUST NOT positively
        // confirm any target. Both the Windows install.wim probe and the Ubuntu casper/ probe run
        // over the same fake, so neither can confirm — the classifier must resolve Unsupported.
        yield return new object[] { "ERROR token", InconclusivePowerShellExecutor.EmittingError() };
        yield return new object[] { "unrecognized output", InconclusivePowerShellExecutor.EmittingUnrecognized() };
        yield return new object[] { "empty output", InconclusivePowerShellExecutor.EmittingEmpty() };
        yield return new object[] { "timeout", InconclusivePowerShellExecutor.Timeout() };
        yield return new object[] { "throwing", InconclusivePowerShellExecutor.Throwing() };
        // The 🔴 shape: a timeout whose buffered stdout still carries a positive token. Timeout must
        // take precedence so it collapses to Unsupported just like every other inconclusive outcome.
        yield return new object[] { "buffered positive token + timeout", InconclusivePowerShellExecutor.BufferedPositiveTokenButTimedOut() };
    }

    [Theory]
    [MemberData(nameof(InconclusiveExecutors))]
    public async Task InconclusiveInspection_ClassifiesUnsupported_NeverUbuntu(
        string caseName, IPowerShellExecutor fakeExecutor)
    {
        var target = await RealClassifierOver(fakeExecutor).ClassifyAsync(IsoPath);

        _output.WriteLine($"[{caseName}] classifier resolved: {target}");

        // The core contract (ISO-D16.1 / ISO-D32): an inspection that cannot POSITIVELY confirm a
        // supported target must resolve Unsupported → OS_NOT_SUPPORTED. Crucially it must NOT be
        // UbuntuServer2404 — that is the value that would reach UbuntuAutoinstallOrchestrator and
        // surface the wrong LINUX_PROVISION_FAILED code (#208 TC-L01).
        target.Should().NotBe(InstallTarget.UbuntuServer2404,
            $"[{caseName}] an inconclusive/failed inspection must NEVER be classified as Ubuntu — " +
            "that is the misclassification that yields LINUX_PROVISION_FAILED instead of OS_NOT_SUPPORTED");
        target.Should().Be(InstallTarget.Unsupported,
            $"[{caseName}] no probe positively confirmed a supported target, so the classifier must " +
            "resolve Unsupported (→ OS_NOT_SUPPORTED), fail-closed per ISO-D16.1 / ISO-D32");
    }

    [Theory]
    [MemberData(nameof(InconclusiveExecutors))]
    public async Task RealInspector_InconclusiveInspection_IsInspectionFailed_NotCleanNegative(
        string caseName, IPowerShellExecutor fakeExecutor)
    {
        // RED-BEFORE anchor (ISO-D16.1 / ISO-D32): the real casper probe must report the inconclusive
        // outcome as InspectionFailed — DISTINCT from the clean NotConfirmed negative. Pre-fix, every
        // non-happy path collapsed to Found=false (i.e. an undistinguished negative), so this
        // assertion fails on the unfixed inspector and passes after the fix.
        var casper = await RealInspectorOver(fakeExecutor).ProbeCasperLayoutAsync(IsoPath);
        var windows = await RealInspectorOver(fakeExecutor).ProbeWindowsInstallWimAsync(IsoPath);

        _output.WriteLine($"[{caseName}] windows={windows} casper={casper}");

        casper.Should().Be(IsoMarkerProbeResult.InspectionFailed,
            $"[{caseName}] an inconclusive casper inspection must be InspectionFailed, not a clean " +
            "NotConfirmed negative — the distinction is what keeps the classifier fail-closed");
        windows.Should().Be(IsoMarkerProbeResult.InspectionFailed,
            $"[{caseName}] an inconclusive Windows inspection must likewise be InspectionFailed");
        casper.Should().NotBe(IsoMarkerProbeResult.Confirmed,
            $"[{caseName}] an inconclusive inspection must never positively confirm the Ubuntu marker");
    }

    [Fact]
    public async Task RealInspector_CleanNegative_IsNotConfirmed_NotInspectionFailed()
    {
        // Contrast anchor: a provable absence (NO_CASPER / NO_WIM) is NotConfirmed — a clean verdict,
        // NOT InspectionFailed. Both non-Confirmed states still resolve Unsupported at the classifier,
        // but keeping them distinct proves the fix does not simply relabel every negative as a failure.
        var fake = InconclusivePowerShellExecutor.CleanNegative();

        var casper = await RealInspectorOver(fake).ProbeCasperLayoutAsync(IsoPath);
        var windows = await RealInspectorOver(fake).ProbeWindowsInstallWimAsync(IsoPath);

        casper.Should().Be(IsoMarkerProbeResult.NotConfirmed,
            "a NO_CASPER terminal token is a clean negative (marker provably absent), not a probe failure");
        windows.Should().Be(IsoMarkerProbeResult.NotConfirmed,
            "a NO_WIM terminal token is a clean negative, not a probe failure");
    }

    [Fact]
    public async Task CleanNegativeInspection_AlsoClassifiesUnsupported()
    {
        // A clean negative (NO_WIM + NO_CASPER) is the ordinary "neither target" path and must also
        // resolve Unsupported. Anchors the contrast: both a provable-absence and an inconclusive
        // failure resolve to the SAME external answer (OS_NOT_SUPPORTED), so the fix does not
        // special-case one input shape.
        var fake = InconclusivePowerShellExecutor.CleanNegative();

        var target = await RealClassifierOver(fake).ClassifyAsync(IsoPath);

        target.Should().Be(InstallTarget.Unsupported,
            "a provably-not-Windows, provably-not-Ubuntu ISO classifies Unsupported (ISO-D22 step 3)");
    }

    [Fact]
    public async Task ConfirmedUbuntu_StillClassifiesUbuntu_HappyPathNotRegressed()
    {
        // Guard the supported Ubuntu happy path: a positive UBUNTU_2404_OK (with a non-Windows
        // install.wim probe) must still confirm UbuntuServer2404 through the real inspector/classifier.
        var fake = InconclusivePowerShellExecutor.ConfirmingUbuntu();

        var target = await RealClassifierOver(fake).ClassifyAsync(IsoPath);

        target.Should().Be(InstallTarget.UbuntuServer2404,
            "a positively-confirmed Ubuntu 24.04 ISO must still classify UbuntuServer2404 (no happy-path regression)");
    }

    [Fact]
    public async Task ConfirmedWindows_StillClassifiesWindows_HappyPathNotRegressed()
    {
        // Guard the supported Windows happy path: a positive WIN_OK must still confirm Windows.
        var fake = InconclusivePowerShellExecutor.ConfirmingWindows();

        var target = await RealClassifierOver(fake).ClassifyAsync(IsoPath);

        target.Should().Be(InstallTarget.Windows,
            "a positively-confirmed install.wim ISO must still classify Windows (no happy-path regression)");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Caller-facing contract: the EXTERNAL error code at the real mapping boundary.
    // These exercise HyperVManager.OsInstallAsync → OsNotSupportedException → ErrorMapper so the
    // test proves OS_NOT_SUPPORTED (not merely the internal InstallTarget.Unsupported enum). Still
    // non-live/un-gated: only the PowerShell boundary is faked (same seam as Issue97OsInstallValidationTests).
    // ═════════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(InconclusiveExecutors))]
    public async Task InconclusiveInspection_MapsToOsNotSupported_AtBoundary_AndDoesNotInvokeUbuntuOrchestrator(
        string caseName, IPowerShellExecutor fakeExecutor)
    {
        using var boundary = new OsInstallBoundaryFixture(fakeExecutor);

        var thrown = await Record.ExceptionAsync(() => boundary.RunOsInstallAsync());

        thrown.Should().BeOfType<OsNotSupportedException>(
            $"[{caseName}] an inconclusive inspection must reject the ISO with OsNotSupportedException at the boundary");

        // The mandated EXTERNAL contract: OsNotSupportedException maps to OS_NOT_SUPPORTED — never
        // LINUX_PROVISION_FAILED. Exercise the real ErrorMapper so the caller-facing code is proven.
        var envelope = new ErrorMapper().MapException(thrown!);
        envelope.Success.Should().BeFalse();
        envelope.ErrorCode.Should().Be(ErrorCodes.OsNotSupported,
            $"[{caseName}] the external error code must be OS_NOT_SUPPORTED at the mapping boundary");
        envelope.ErrorCode.Should().NotBe(ErrorCodes.LinuxProvisionFailed,
            $"[{caseName}] LINUX_PROVISION_FAILED must never be produced for an inconclusive ISO");

        // The Ubuntu orchestrator is the ONLY producer of LINUX_PROVISION_FAILED. Prove it was never
        // reached — an inconclusive ISO must be rejected at classification, before any dispatch.
        boundary.OrchestratorInvocations.Should().Be(0,
            $"[{caseName}] UbuntuAutoinstallOrchestrator must NOT be invoked on the inconclusive path");
    }

    [Fact]
    public async Task ConfirmedUbuntu_AtBoundary_InvokesUbuntuOrchestrator_Once()
    {
        // Contrast: the supported Ubuntu happy path DOES dispatch to the orchestrator exactly once,
        // proving the spy would have caught an erroneous invocation in the inconclusive cases above.
        using var boundary = new OsInstallBoundaryFixture(
            InconclusivePowerShellExecutor.ConfirmingUbuntu());

        // The spy returns a success result, so the confirmed path completes without throwing.
        var result = await boundary.RunOsInstallAsync();

        result.Should().NotBeNull(
            "a confirmed Ubuntu 24.04 ISO must dispatch and return a result, not be rejected as unsupported");
        boundary.OrchestratorInvocations.Should().Be(1,
            "a confirmed Ubuntu 24.04 ISO must dispatch to UbuntuAutoinstallOrchestrator exactly once");
    }
}

/// <summary>
/// Wires the REAL <see cref="HyperVManager.OsInstallAsync"/> boundary over the REAL
/// <see cref="GuestOsClassifier"/> + <see cref="IsoInspector"/> (faked only at the PowerShell seam)
/// and a spy <see cref="IUbuntuAutoinstallOrchestrator"/> that records invocations. Non-live: a
/// real 1-byte .iso on disk satisfies the File.Exists gate; no host, mount, or env opt-in.
/// </summary>
internal sealed class OsInstallBoundaryFixture : IDisposable
{
    private readonly string _tempDir;
    private readonly string _isoPath;
    private readonly HyperVManager _manager;
    private readonly SpyUbuntuOrchestrator _spyOrchestrator = new();

    public int OrchestratorInvocations => _spyOrchestrator.Invocations;

    public OsInstallBoundaryFixture(IPowerShellExecutor fakeExecutor)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "issue208-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _isoPath = Path.Combine(_tempDir, "some-unprobeable-linux.iso");
        File.WriteAllBytes(_isoPath, new byte[] { 0 });

        const string localHostId = "local";
        var options = new ServerOptions
        {
            DefaultHostId = localHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [localHostId] = new HostProfile
                {
                    HostId = localHostId,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = Path.Combine(_tempDir, "base.vhdx"),
                    StorageRoot = _tempDir,
                },
            },
        };

        var realInspector = new IsoInspector(fakeExecutor, NullLogger<IsoInspector>.Instance);
        var realClassifier = new GuestOsClassifier(realInspector, NullLogger<GuestOsClassifier>.Instance);

        _manager = new HyperVManager(
            fakeExecutor,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            realInspector,
            guestOsClassifier: realClassifier,
            ubuntuOrchestrator: _spyOrchestrator);
    }

    public Task<OsInstallResult> RunOsInstallAsync()
        => _manager.OsInstallAsync(
            "local", "issue208-vm", _isoPath, "P@ssw0rd!",
            cpuCount: 4, memoryMB: 8192, diskSizeGB: 127,
            skipPreflight: false);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>
/// Spy orchestrator: counts <see cref="InstallAsync"/> calls so a test can prove the inconclusive
/// path never reaches the only producer of LINUX_PROVISION_FAILED. Returns a minimal success result
/// for the happy-path contrast case.
/// </summary>
internal sealed class SpyUbuntuOrchestrator : IUbuntuAutoinstallOrchestrator
{
    private int _invocations;

    public int Invocations => _invocations;

    public Task<OsInstallResult> InstallAsync(UbuntuInstallRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _invocations);
        return Task.FromResult(new OsInstallResult { Name = request.Name, State = "Running" });
    }
}

/// <summary>
/// Fake <see cref="IPowerShellExecutor"/> that returns a scripted terminal outcome for the two
/// ISO-marker inspection scripts, mirroring the real inspector's paths without any host, mount, or
/// PowerShell process. Recognizes the pre-mount probe (Get-DiskImage/Attached) and the out-of-band
/// dismount so the inspector's leak-safe flow runs, and returns the configured outcome for the
/// actual Mount-DiskImage inspection script.
/// </summary>
internal sealed class InconclusivePowerShellExecutor : IPowerShellExecutor
{
    private readonly PowerShellResult? _windowsResult;
    private readonly PowerShellResult? _casperResult;
    private readonly bool _throw;

    private InconclusivePowerShellExecutor(
        PowerShellResult? windowsResult, PowerShellResult? casperResult, bool @throw)
    {
        _windowsResult = windowsResult;
        _casperResult = casperResult;
        _throw = @throw;
    }

    private static InconclusivePowerShellExecutor Both(PowerShellResult shared)
        => new(shared, shared, @throw: false);

    // An `ERROR:` terminal line — the shape the inspection script's catch block emits.
    public static InconclusivePowerShellExecutor EmittingError()
        => Both(new PowerShellResult { ExitCode = 0, Stdout = "ERROR:The ISO could not be probed (device not ready)." });

    // Unrecognized stdout — neither a known token nor an ERROR: line.
    public static InconclusivePowerShellExecutor EmittingUnrecognized()
        => Both(new PowerShellResult { ExitCode = 0, Stdout = "some entirely unexpected banner text" });

    // Empty stdout — e.g. a silent non-zero exit.
    public static InconclusivePowerShellExecutor EmittingEmpty()
        => Both(new PowerShellResult { ExitCode = 1, Stdout = string.Empty });

    // Timed-out inspection — no recognizable terminal line and TimedOut set.
    public static InconclusivePowerShellExecutor Timeout()
        => Both(new PowerShellResult { ExitCode = -1, Stdout = string.Empty, TimedOut = true });

    // The exact 🔴 shape: a timed-out result that STILL carries a buffered positive token in stdout.
    // Fail-closed requires timeout to win over the token, so this must resolve InspectionFailed, not
    // Confirmed. Windows carries WIN_OK, casper carries UBUNTU_2404_OK.
    public static InconclusivePowerShellExecutor BufferedPositiveTokenButTimedOut()
        => new(
            windowsResult: new PowerShellResult { ExitCode = -1, Stdout = "WIN_OK", TimedOut = true },
            casperResult: new PowerShellResult { ExitCode = -1, Stdout = "UBUNTU_2404_OK", TimedOut = true },
            @throw: false);

    // Executor throws mid-inspection (after dispatch) — the caught-exception path.
    public static InconclusivePowerShellExecutor Throwing()
        => new(windowsResult: null, casperResult: null, @throw: true);

    // Clean negative: NO_WIM for the Windows probe, NO_CASPER for the casper probe.
    public static InconclusivePowerShellExecutor CleanNegative()
        => new(
            windowsResult: new PowerShellResult { ExitCode = 0, Stdout = "NO_WIM" },
            casperResult: new PowerShellResult { ExitCode = 0, Stdout = "NO_CASPER" },
            @throw: false);

    // Positive Ubuntu: Windows probe cleanly negative (NO_WIM), casper probe confirms UBUNTU_2404_OK.
    // Issue #370 / UMD-D2: the stdout carries the base64 GRUB payload FIRST and the terminal token
    // LAST, matching what the production script emits — the media is PREPARED, so the capability
    // guard admits it and this stays a dispatch test.
    public static InconclusivePowerShellExecutor ConfirmingUbuntu()
        => new(
            windowsResult: new PowerShellResult { ExitCode = 0, Stdout = "NO_WIM" },
            casperResult: new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "GRUBCFG_B64:" + Convert.ToBase64String(
                             System.Text.Encoding.UTF8.GetBytes(
                                 TestIsoInspector.PreparedGrubConfiguration))
                         + "\nUBUNTU_2404_OK",
            },
            @throw: false);

    // Positive Windows: Windows probe confirms WIN_OK (casper never reached in the classifier).
    public static InconclusivePowerShellExecutor ConfirmingWindows()
        => new(
            windowsResult: new PowerShellResult { ExitCode = 0, Stdout = "WIN_OK" },
            casperResult: new PowerShellResult { ExitCode = 0, Stdout = "NO_CASPER" },
            @throw: false);

    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        // Pre-mount probe (PS #1): report NOT_MOUNTED so the inspection proceeds.
        if (script.Contains("Get-DiskImage", StringComparison.Ordinal) &&
            script.Contains("Attached", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "NOT_MOUNTED" });
        }

        // Out-of-band dismount (PS #3): report success so best-effort cleanup is quiet.
        if (script.Contains("Dismount-DiskImage", StringComparison.Ordinal) &&
            !script.Contains("Mount-DiskImage", StringComparison.Ordinal))
        {
            return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "DISMOUNTED" });
        }

        // Inspection script (PS #2). The throwing case models an executor failure after dispatch.
        if (_throw)
        {
            throw new InvalidOperationException("Simulated executor failure during ISO inspection.");
        }

        // Route by which inspection script this is (install.wim vs casper) so each probe can be
        // given a distinct outcome.
        if (script.Contains("install.wim", StringComparison.Ordinal))
        {
            return Task.FromResult(_windowsResult!);
        }
        return Task.FromResult(_casperResult!);
    }
}
