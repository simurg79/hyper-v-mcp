using System.Text;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Tri-state verdict from an ISO marker probe. <see cref="NotConfirmed"/> (marker provably
/// absent) and <see cref="InspectionFailed"/> (no verdict) stay DISTINCT to keep the classifier
/// fail-closed: only <see cref="Confirmed"/> yields an install target; both others collapse to
/// Unsupported, so an unprobeable ISO can never be mis-driven into a target orchestrator.
/// See myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D16.1.
/// </summary>
public enum IsoMarkerProbeResult
{
    /// <summary>The marker was positively found; the ISO matches the probed OS family.</summary>
    Confirmed,

    /// <summary>The probe completed and the marker was provably absent (a clean negative).</summary>
    NotConfirmed,

    /// <summary>
    /// The probe reached no verdict (mount/inspection error, unrecognized or empty output,
    /// timeout, or exception). Fail-closed: this collapses to Unsupported, never a positive.
    /// </summary>
    InspectionFailed,
}

/// <summary>
/// Abstraction over the OS-family heuristic used by <c>vm_os_install</c> (ISO-D16).
/// Mounts an ISO read-only and reports whether it appears to be a Windows installer
/// (i.e., contains <c>sources\install.wim</c>). Modeled as an interface for unit testing.
/// </summary>
/// <remarks>
/// Issue #97 / ISO-D16: The current orchestration (autounattend.xml generation,
/// GVLK injection, DISM <c>/Apply-Image</c> of <c>install.wim</c>, Panther
/// <c>unattend.xml</c>, PowerShell-Direct-based completion detection) is
/// Windows-specific. Non-Windows ISOs must fail fast with
/// <c>OS_NOT_SUPPORTED</c> rather than fall through to a confusing later
/// failure (DISM not finding <c>install.wim</c>, completion monitoring
/// hanging, etc.). The heuristic is intentionally narrowed to
/// <c>install.wim</c> only (not <c>install.esd</c>) — broadening to
/// <c>install.esd</c> requires a DISM template change tracked as Q9.
/// </remarks>
public interface IIsoInspector
{
    /// <summary>
    /// Returns <c>true</c> when the ISO at <paramref name="isoPath"/> can be mounted
    /// and contains <c>sources\install.wim</c> on its primary volume; <c>false</c>
    /// otherwise. Errors during mount/inspection are treated as "not Windows".
    /// Use <see cref="ContainsWindowsInstallWimWithDiagnosticAsync"/> when an
    /// operator-facing diagnostic string is needed (e.g., for logging).
    /// </summary>
    /// <param name="isoPath">Absolute path to the ISO file (must already exist).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ContainsWindowsInstallWimAsync(string isoPath, CancellationToken ct = default)
    {
        return ContainsWindowsInstallWimWithDiagnosticAsync(isoPath, ct).ContinueWith(t => t.Result.Found,
            TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// As <see cref="ContainsWindowsInstallWimAsync"/>, but also returns a diagnostic string.
    /// </summary>
    Task<(bool Found, string? Diagnostic)> ContainsWindowsInstallWimWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> when the ISO at <paramref name="isoPath"/> mounts and looks like Ubuntu
    /// Server 24.04 live-installer media: a top-level <c>casper/</c> directory AND a <c>.disk/info</c>
    /// asserting 24.04 (OQ-U1, so a non-24.04 Ubuntu falls through to <c>OS_NOT_SUPPORTED</c> rather
    /// than being mis-driven). Mirrors <see cref="ContainsWindowsInstallWimWithDiagnosticAsync"/> and
    /// reuses its leak-safe mount/dismount machinery (MC-D1..D8 / issue #113). Mount/inspection errors
    /// are treated as "not Ubuntu 24.04".
    /// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D22 / OQ-U1.
    /// </summary>
    Task<(bool Found, string? Diagnostic)> ContainsCasperLayoutWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default);

    /// <summary>
    /// Fail-closed (ISO-D16.1) Windows probe: tells a POSITIVE <c>install.wim</c> confirmation
    /// (<see cref="IsoMarkerProbeResult.Confirmed"/>) apart from a clean negative
    /// (<see cref="IsoMarkerProbeResult.NotConfirmed"/>) and an inconclusive/failed inspection
    /// (<see cref="IsoMarkerProbeResult.InspectionFailed"/>). The default implementation degrades a
    /// legacy <c>(bool,diag)</c> probe to Confirmed/NotConfirmed only; the real inspector overrides it
    /// to surface InspectionFailed for ERROR/unrecognized/timeout/exception outcomes.
    /// See myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D16.1.
    /// </summary>
    async Task<IsoMarkerProbeResult> ProbeWindowsInstallWimAsync(
        string isoPath, CancellationToken ct = default)
    {
        var (found, _) = await ContainsWindowsInstallWimWithDiagnosticAsync(isoPath, ct)
            .ConfigureAwait(false);
        return found ? IsoMarkerProbeResult.Confirmed : IsoMarkerProbeResult.NotConfirmed;
    }

    /// <summary>
    /// Fail-closed (ISO-D16.1 / ISO-D32) Ubuntu probe: <see cref="IsoMarkerProbeResult.Confirmed"/>
    /// ONLY on a positive <c>UBUNTU_2404_OK</c>; a clean negative is
    /// <see cref="IsoMarkerProbeResult.NotConfirmed"/>; any inconclusive/failed outcome is
    /// <see cref="IsoMarkerProbeResult.InspectionFailed"/>. Both non-Confirmed states collapse to
    /// <c>Unsupported</c>, so an unprobeable Linux ISO can never reach the Ubuntu orchestrator.
    /// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D32.
    /// </summary>
    async Task<IsoMarkerProbeResult> ProbeCasperLayoutAsync(
        string isoPath, CancellationToken ct = default)
    {
        var (found, _) = await ContainsCasperLayoutWithDiagnosticAsync(isoPath, ct)
            .ConfigureAwait(false);
        return found ? IsoMarkerProbeResult.Confirmed : IsoMarkerProbeResult.NotConfirmed;
    }

    /// <summary>
    /// As <see cref="ProbeCasperLayoutAsync"/>, but also returns the media's <c>boot\grub\grub.cfg</c>
    /// text (null when it could not be read) so autoinstall capability is judged from the SAME mount
    /// recognition already performs — no second mount on any Ubuntu install call.
    /// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
    /// — UMD-D2.
    /// </summary>
    async Task<UbuntuMediaProbe> ProbeUbuntuMediaAsync(string isoPath, CancellationToken ct = default)
        => new(await ProbeCasperLayoutAsync(isoPath, ct).ConfigureAwait(false), null);
}

/// <summary>
/// One widened Ubuntu probe observation: the unchanged recognition verdict plus the raw GRUB
/// configuration text the capability check consumes, or null when it could not be read.
/// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
/// — UMD-D2.
/// </summary>
public readonly record struct UbuntuMediaProbe(
    IsoMarkerProbeResult Result,
    string? GrubConfiguration);

/// <summary>
/// PowerShell-backed <see cref="IIsoInspector"/> that uses
/// <c>Mount-DiskImage</c> / <c>Get-Volume</c> to inspect an ISO. Always
/// dismounts via <c>try/finally</c> in the generated PS script, so a failed
/// inspection cannot leave the ISO mounted — and, for the case where the
/// child PowerShell process is killed before its <c>finally</c> can run
/// (timeout / cancellation), an out-of-band cleanup runs in the .NET host.
/// </summary>
/// <remarks>
/// Why PowerShell rather than .NET volume reads: the manager already owns an
/// out-of-process PowerShell executor for every other Hyper-V interaction;
/// adding a second mount/dismount mechanism (e.g., <c>VirtualDiskApi</c>
/// P/Invoke) doubles the surface for ACL / lock / cleanup bugs. ISO-D16's
/// architect note Q1 budgeted ~1–2 s per inspection — well within the cost
/// envelope of a Sev2 fix and dwarfed by the rest of <c>vm_os_install</c>.
///
/// Issue #113 / out-of-band mount cleanup: see
/// /myplans/vm-management/iso-installation/iso-inspector-mount-cleanup-design.md
/// for the full design (decisions MC-D1..MC-D8). The flow is:
///   1. Pre-mount probe (PS #1) — capture <c>wasAlreadyMountedBeforeUs</c>.
///   2. Inspection (PS #2) — existing Mount/Test-Path/Dismount-in-finally.
///   3. If terminal output is unrecognized OR the call timed out OR threw
///      after dispatch — and the probe said the ISO was NOT already mounted
///      — fire a fresh out-of-band <c>Dismount-DiskImage</c> (PS #3) with
///      <c>CancellationToken.None</c> and a hardcoded ~30 s timeout.
/// </remarks>
public sealed class IsoInspector : IIsoInspector
{
    // Hardcoded cleanup timeout — kept short so cleanup itself cannot leak.
    // TODO: configurable if observed insufficient (per design MC-Q4).
    private const int CleanupTimeoutSeconds = 30;
    private const int ProbeTimeoutSeconds = 15;

    private readonly IPowerShellExecutor _psExecutor;
    private readonly ILogger<IsoInspector> _logger;

    public IsoInspector(IPowerShellExecutor psExecutor, ILogger<IsoInspector> logger)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Fail-closed Windows probe: the classifier must never mistake "could not probe" for "not
    /// Windows" in a way that could hand the ISO to a different target.
    /// See myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D16.1.
    /// </summary>
    public Task<IsoMarkerProbeResult> ProbeWindowsInstallWimAsync(
        string isoPath, CancellationToken ct = default)
        => ProbeAsync(isoPath, WindowsProbeSpec, ct);

    /// <summary>
    /// Fail-closed Ubuntu probe — the direct home of issue #208 (TC-L01): an unprobeable Linux ISO
    /// yields InspectionFailed → Unsupported → OS_NOT_SUPPORTED, never UbuntuServer2404.
    /// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D32.
    /// </summary>
    public Task<IsoMarkerProbeResult> ProbeCasperLayoutAsync(
        string isoPath, CancellationToken ct = default)
        => ProbeAsync(isoPath, CasperProbeSpec, ct);

    /// <inheritdoc />
    public async Task<UbuntuMediaProbe> ProbeUbuntuMediaAsync(
        string isoPath, CancellationToken ct = default)
    {
        var (result, _, grubConfiguration) =
            await RunProbeAsync(isoPath, CasperProbeSpec, ct).ConfigureAwait(false);
        return new UbuntuMediaProbe(result, grubConfiguration);
    }

    /// <inheritdoc />
    public async Task<(bool Found, string? Diagnostic)> ContainsWindowsInstallWimWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default)
    {
        var (result, diagnostic, _) = await RunProbeAsync(isoPath, WindowsProbeSpec, ct).ConfigureAwait(false);
        return (result == IsoMarkerProbeResult.Confirmed, diagnostic);
    }

    /// <inheritdoc />
    public async Task<(bool Found, string? Diagnostic)> ContainsCasperLayoutWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default)
    {
        var (result, diagnostic, _) = await RunProbeAsync(isoPath, CasperProbeSpec, ct).ConfigureAwait(false);
        return (result == IsoMarkerProbeResult.Confirmed, diagnostic);
    }

    private async Task<IsoMarkerProbeResult> ProbeAsync(
        string isoPath, ProbeSpec spec, CancellationToken ct)
    {
        var (result, _, _) = await RunProbeAsync(isoPath, spec, ct).ConfigureAwait(false);
        return result;
    }

    // A probe's per-target vocabulary: the inspection script and its recognized terminal tokens.
    private sealed record ProbeSpec(
        string PositiveToken,
        string[] NegativeTokens,
        string LogNoun,
        Func<string, string> BuildScript);

    private static readonly ProbeSpec WindowsProbeSpec = new(
        PositiveToken: "WIN_OK",
        NegativeTokens: new[] { "NO_WIM" },
        LogNoun: "ISO",
        BuildScript: BuildWindowsScript);

    private static readonly ProbeSpec CasperProbeSpec = new(
        PositiveToken: "UBUNTU_2404_OK",
        NegativeTokens: new[] { "NO_CASPER", "CASPER_NOT_2404" },
        LogNoun: "Ubuntu ISO",
        BuildScript: BuildCasperScript);

    /// <summary>
    /// Shared leak-safe inspection driver.
    /// See myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D16.1 (fail-closed
    /// tri-state) and iso-inspector-mount-cleanup-design.md (MC-D1..D8, out-of-band dismount).
    /// A timeout MUST take precedence over EVERY terminal token: a timed-out result can still carry a
    /// buffered positive token (WIN_OK / UBUNTU_2404_OK) in stdout, and returning that as Confirmed
    /// would break the fail-closed invariant AND skip the issue #113 out-of-band cleanup. So timeout
    /// is checked before any token match.
    /// </summary>
    private async Task<(IsoMarkerProbeResult Result, string? Diagnostic, string? GrubConfiguration)> RunProbeAsync(
        string isoPath, ProbeSpec spec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(isoPath))
            return (IsoMarkerProbeResult.InspectionFailed, "ISO path was empty.", null);

        // Single-quoted PowerShell literal — escape embedded apostrophes by doubling.
        var escapedPath = isoPath.Replace("'", "''");

        // Step 1 — pre-mount probe (MC-D3/D4) in the parent process; failures are non-fatal and
        // treated as "not already mounted" (the conservative choice — we only ever risk dismounting
        // OUR OWN mount on a leaked-mount path).
        var wasAlreadyMountedBeforeUs = await ProbeMountedAsync(escapedPath, isoPath, ct).ConfigureAwait(false);

        var script = spec.BuildScript(escapedPath);

        // Whether the inspection script was actually dispatched (per design 🟡 #3 / MC-Q3: out-of-band
        // cleanup only fires for failures AFTER dispatch — a synchronous arg-validation throw created
        // no mount).
        var dispatched = false;
        // Set once we've decided the script's own finally is trusted to have run (recognized terminal
        // output). When false on exit, and dispatched, and not already-mounted — fire out-of-band cleanup.
        var cleanupNeeded = false;

        try
        {
            dispatched = true;
            var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 60, ct).ConfigureAwait(false);
            var stdout = (result.Stdout ?? string.Empty).Trim();

            // Timeout precedence: a killed child PS can't be trusted to have run its in-script finally,
            // and its buffered stdout may hold a stale positive token. Resolve InspectionFailed BEFORE
            // any token match so a buffered WIN_OK/UBUNTU_2404_OK can never win, and arm the issue #113
            // out-of-band dismount.
            if (result.TimedOut)
            {
                cleanupNeeded = true;
                var timeoutDiagnostic = new StringBuilder();
                timeoutDiagnostic.Append(spec.LogNoun).Append(" inspection timed out");
                if (!string.IsNullOrEmpty(result.Stderr))
                    timeoutDiagnostic.Append("; stderr=").Append(result.Stderr.Trim());
                _logger.LogWarning(
                    "{Noun} inspection timed out for '{IsoPath}': stdout='{Stdout}' stderr='{Stderr}'",
                    spec.LogNoun, isoPath, stdout, result.Stderr);
                return (IsoMarkerProbeResult.InspectionFailed, timeoutDiagnostic.ToString(), null);
            }

            // Take the LAST non-empty line — defensive against any prefix banner. The terminal token
            // MUST therefore be the script's final line; any payload it emits precedes it.
            string? lastLine = null;
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) lastLine = trimmed;
            }
            lastLine ??= string.Empty;

            var grubConfiguration = ExtractGrubConfiguration(stdout);

            if (lastLine == spec.PositiveToken)
            {
                return (IsoMarkerProbeResult.Confirmed, null, grubConfiguration);
            }
            foreach (var negativeToken in spec.NegativeTokens)
            {
                if (lastLine == negativeToken)
                {
                    // A recognized negative proves the script's catch/return path ran and its finally
                    // dismounted — a clean, non-failed "marker absent".
                    return (IsoMarkerProbeResult.NotConfirmed, NegativeDiagnostic(negativeToken), grubConfiguration);
                }
            }
            if (lastLine.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                // An 'ERROR:' line is emitted by the script's catch block, which is unconditionally
                // followed by its finally — so the in-script Dismount already ran (no out-of-band
                // cleanup needed). Fail-closed: an inspection error is InspectionFailed, never a clean
                // negative.
                var message = lastLine.Substring("ERROR:".Length);
                _logger.LogWarning("{Noun} inspection reported error for '{IsoPath}': {Message}", spec.LogNoun, isoPath, message);
                return (IsoMarkerProbeResult.InspectionFailed, message, null);
            }

            // No recognized terminal line (non-zero exit, garbage) ⇒ cannot prove the finally ran ⇒
            // assume leak and fail closed.
            cleanupNeeded = true;
            var diagnostic = new StringBuilder();
            diagnostic.Append(spec.LogNoun).Append(" inspection produced no recognizable result");
            if (!string.IsNullOrEmpty(result.Stderr))
                diagnostic.Append("; stderr=").Append(result.Stderr.Trim());
            _logger.LogWarning(
                "{Noun} inspection unexpected output for '{IsoPath}': stdout='{Stdout}' stderr='{Stderr}'",
                spec.LogNoun, isoPath, stdout, result.Stderr);
            return (IsoMarkerProbeResult.InspectionFailed, diagnostic.ToString(), null);
        }
        catch (OperationCanceledException)
        {
            // MC-D5: cancellation killed the child PS mid-flight; its in-script finally could not run.
            // Fire out-of-band cleanup, then rethrow so the caller sees the original cancellation (MC-D6).
            cleanupNeeded = dispatched;
            throw;
        }
        catch (Exception ex)
        {
            // 🟡 #3 / MC-Q3: cleanup only for post-dispatch throws. A caught exception is fail-closed
            // InspectionFailed (ISO-D16.1), never a clean negative.
            cleanupNeeded = dispatched;
            _logger.LogWarning(ex, "{Noun} inspection threw for '{IsoPath}'", spec.LogNoun, isoPath);
            return (IsoMarkerProbeResult.InspectionFailed, ex.Message, null);
        }
        finally
        {
            if (cleanupNeeded && !wasAlreadyMountedBeforeUs)
            {
                // Fire-and-await out-of-band dismount on a fresh PS process (MC-D2 — uses
                // CancellationToken.None deliberately since ct may already be signaled).
                await TryOutOfBandDismountAsync(escapedPath, isoPath).ConfigureAwait(false);
            }
            else if (cleanupNeeded && wasAlreadyMountedBeforeUs)
            {
                _logger.LogDebug(
                    "Skipping out-of-band ISO dismount for '{IsoPath}': pre-existing mount detected before inspection.",
                    isoPath);
            }
        }
    }

    private const string GrubConfigurationPrefix = "GRUBCFG_B64:";

    /// <summary>
    /// Recovers the GRUB payload by scanning the lines BEFORE the terminal token, which is what
    /// lets the driver keep its unchanged last-non-empty-line contract. A payload that fails to
    /// decode is treated as absent, so a corrupt line degrades to Undeterminable rather than
    /// throwing on the shared recognition path.
    /// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
    /// — UMD-D2.
    /// </summary>
    private static string? ExtractGrubConfiguration(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(GrubConfigurationPrefix, StringComparison.Ordinal)) continue;

            var payload = trimmed.Substring(GrubConfigurationPrefix.Length);
            if (payload.Length == 0) return null;
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }

    private static string? NegativeDiagnostic(string negativeToken) => negativeToken switch
    {
        "NO_WIM" => "ISO does not contain sources\\install.wim.",
        "NO_CASPER" => "ISO does not contain a casper/ live-installer layout.",
        "CASPER_NOT_2404" => "ISO has a casper/ layout but is not Ubuntu Server 24.04.",
        _ => null,
    };

    private static string BuildWindowsScript(string escapedPath) => $@"
$ErrorActionPreference = 'Stop'
$path = '{escapedPath}'
$mounted = $null
try {{
    $mounted = Mount-DiskImage -ImagePath $path -PassThru -ErrorAction Stop
    # Re-fetch to ensure StorageType is populated post-mount.
    $img = Get-DiskImage -ImagePath $path -ErrorAction Stop
    $vol = $img | Get-Volume -ErrorAction Stop
    if (-not $vol -or -not $vol.DriveLetter) {{
        Write-Output 'ERROR:Mounted ISO has no drive letter (not a recognized volume).'
        return
    }}
    $wimPath = ('{{0}}:\sources\install.wim' -f $vol.DriveLetter)
    if (Test-Path -LiteralPath $wimPath -PathType Leaf) {{
        Write-Output 'WIN_OK'
    }} else {{
        Write-Output 'NO_WIM'
    }}
}} catch {{
    $msg = $_.Exception.Message -replace '[\r\n]+',' '
    Write-Output ('ERROR:' + $msg)
}} finally {{
    try {{
        if ($null -ne $mounted) {{
            Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue | Out-Null
        }}
    }} catch {{
        # Swallow — best-effort cleanup; surface only the original result.
    }}
}}
";

    private static string BuildCasperScript(string escapedPath) => $@"
$ErrorActionPreference = 'Stop'
$path = '{escapedPath}'
$mounted = $null
try {{
    $mounted = Mount-DiskImage -ImagePath $path -PassThru -ErrorAction Stop
    $img = Get-DiskImage -ImagePath $path -ErrorAction Stop
    $vol = $img | Get-Volume -ErrorAction Stop
    if (-not $vol -or -not $vol.DriveLetter) {{
        Write-Output 'ERROR:Mounted ISO has no drive letter (not a recognized volume).'
        return
    }}
    $casperDir = ('{{0}}:\casper' -f $vol.DriveLetter)
    if (-not (Test-Path -LiteralPath $casperDir -PathType Container)) {{
        Write-Output 'NO_CASPER'
        return
    }}
    # 24.04 assertion (OQ-U1): .disk/info holds a label like 'Ubuntu-Server 24.04 LTS ...'.
    # Absent/mismatched → treat as not-24.04.
    $diskInfo = ('{{0}}:\.disk\info' -f $vol.DriveLetter)
    $version = ''
    if (Test-Path -LiteralPath $diskInfo -PathType Leaf) {{
        $version = (Get-Content -LiteralPath $diskInfo -Raw -ErrorAction SilentlyContinue)
    }}
    if ($version -match '24\.04') {{
        # Emit the GRUB configuration BEFORE the terminal token: the driver compares the LAST
        # non-empty line for equality, so a payload after it would fail recognition for every
        # prepared Ubuntu ISO. The read is individually guarded because the body runs under
        # $ErrorActionPreference='Stop', where an unguarded failure would turn a readable-media
        # problem into OS_NOT_SUPPORTED.
        # See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
        # — UMD-D2.
        $grubPath = ('{{0}}:\boot\grub\grub.cfg' -f $vol.DriveLetter)
        $grubBytes = $null
        try {{
            if (Test-Path -LiteralPath $grubPath -PathType Leaf) {{
                $grubBytes = [System.IO.File]::ReadAllBytes($grubPath)
            }}
        }} catch {{
            $grubBytes = $null
        }}
        if ($null -ne $grubBytes -and $grubBytes.Length -gt 0) {{
            Write-Output ('GRUBCFG_B64:' + [Convert]::ToBase64String($grubBytes))
        }}
        Write-Output 'UBUNTU_2404_OK'
    }} else {{
        Write-Output 'CASPER_NOT_2404'
    }}
}} catch {{
    $msg = $_.Exception.Message -replace '[\r\n]+',' '
    Write-Output ('ERROR:' + $msg)
}} finally {{
    try {{
        if ($null -ne $mounted) {{
            Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue | Out-Null
        }}
    }} catch {{
        # Swallow — best-effort cleanup; surface only the original result.
    }}
}}
";

    /// <summary>
    /// PS #1 — pre-mount probe. Returns <c>true</c> iff <c>Get-DiskImage</c>
    /// reports <c>Attached -eq $true</c> for the given ISO path before we
    /// touch it. Failure modes (probe timeout, executor exception, garbled
    /// output) all return <c>false</c> per design — the conservative choice
    /// is to assume "we created the mount" and accept the (vanishingly small)
    /// risk of dismounting our own mount on a failure path.
    /// </summary>
    private async Task<bool> ProbeMountedAsync(string escapedPath, string isoPath, CancellationToken ct)
    {
        // Output a single canonical line: 'MOUNTED' or 'NOT_MOUNTED'.
        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
    $img = Get-DiskImage -ImagePath '{escapedPath}' -ErrorAction Stop
    if ($img.Attached) {{ Write-Output 'MOUNTED' }} else {{ Write-Output 'NOT_MOUNTED' }}
}} catch {{
    Write-Output 'NOT_MOUNTED'
}}
";
        try
        {
            var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: ProbeTimeoutSeconds, ct).ConfigureAwait(false);
            if (result.TimedOut)
            {
                _logger.LogDebug("Pre-mount probe timed out for '{IsoPath}'; assuming not pre-mounted.", isoPath);
                return false;
            }
            var stdout = (result.Stdout ?? string.Empty).Trim();
            // Last non-empty line again — same defense as the inspection path.
            string? lastLine = null;
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) lastLine = trimmed;
            }
            return string.Equals(lastLine, "MOUNTED", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled before we even started inspection — propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-mount probe threw for '{IsoPath}'; assuming not pre-mounted.", isoPath);
            return false;
        }
    }

    /// <summary>
    /// PS #3 — out-of-band best-effort dismount (MC-D2 / MC-D5 / MC-D6).
    /// Runs on a fresh PowerShell invocation with <see cref="CancellationToken.None"/>
    /// and a hardcoded short timeout. Failures are logged at <c>Warning</c>
    /// and never propagated; this method must not change the outcome of the
    /// caller's primary inspection result.
    /// </summary>
    private async Task TryOutOfBandDismountAsync(string escapedPath, string isoPath)
    {
        // -ErrorAction SilentlyContinue: dismounting an already-dismounted
        // ISO is harmless; we can't always tell whether the original
        // finally actually ran (assumption #3 in the design doc).
        var script = $@"
$ErrorActionPreference = 'Continue'
try {{
    Dismount-DiskImage -ImagePath '{escapedPath}' -ErrorAction SilentlyContinue | Out-Null
    Write-Output 'DISMOUNTED'
}} catch {{
    $msg = $_.Exception.Message -replace '[\r\n]+',' '
    Write-Output ('DISMOUNT_FAILED:' + $msg)
}}
";
        try
        {
            // CancellationToken.None is intentional (MC-D2): the original ct
            // is already signaled on the cancel path; reusing it would skip
            // cleanup and defeat the entire feature.
            var result = await _psExecutor
                .ExecuteAsync(script, timeoutSeconds: CleanupTimeoutSeconds, CancellationToken.None)
                .ConfigureAwait(false);

            var stdout = (result.Stdout ?? string.Empty).Trim();
            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "Out-of-band ISO dismount timed out for '{IsoPath}' after {Seconds}s.",
                    isoPath, CleanupTimeoutSeconds);
                return;
            }
            if (stdout.Contains("DISMOUNTED", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Out-of-band ISO dismount completed for '{IsoPath}'.", isoPath);
                return;
            }
            _logger.LogWarning(
                "Out-of-band ISO dismount produced no success token for '{IsoPath}': stdout='{Stdout}' stderr='{Stderr}'.",
                isoPath, stdout, result.Stderr);
        }
        catch (Exception ex)
        {
            // Never propagate — best-effort recovery (MC-D6).
            _logger.LogWarning(ex,
                "Out-of-band ISO dismount threw for '{IsoPath}'.", isoPath);
        }
    }
}
