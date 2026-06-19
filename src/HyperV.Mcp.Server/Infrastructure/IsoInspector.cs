using System.Text;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

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
}

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

    /// <inheritdoc />
    public async Task<(bool Found, string? Diagnostic)> ContainsWindowsInstallWimWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(isoPath))
            return (false, "ISO path was empty.");

        // Single-quoted PowerShell literal — escape embedded apostrophes by doubling.
        var escapedPath = isoPath.Replace("'", "''");

        // -----------------------------------------------------------------
        // Step 1 — Pre-mount probe (MC-D3 / MC-D4).
        // Runs in the parent .NET process before the inspection script so
        // the resulting boolean lives in a process that *cannot* be killed
        // by the inspection-script timeout. Probe failures are non-fatal:
        // we proceed treating the ISO as "not already mounted by someone
        // else", which is the conservative choice (we only ever risk
        // dismounting OUR OWN mount on a leaked-mount path).
        //
        // Concurrency note (MC-Q2): if two callers inspect the same ISO,
        // the SECOND caller's probe sees Attached=true and will skip
        // out-of-band cleanup; the FIRST caller still owns its own
        // cleanup. This is safe by construction.
        // -----------------------------------------------------------------
        var wasAlreadyMountedBeforeUs = await ProbeMountedAsync(escapedPath, isoPath, ct).ConfigureAwait(false);

        // The script ALWAYS dismounts in the finally block, even on error, so a
        // failed inspection cannot leave the ISO mounted (ISO-D16 acceptance criterion)
        // — UNLESS the host kills this child process between Mount-DiskImage and
        // the finally block (issue #113). The .NET-side cleanup below covers that race.
        // Output: a single 'WIN_OK' / 'NO_WIM' / 'ERROR:<msg>' line on stdout.
        var script = $@"
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

        // Tracks whether the inspection script was actually dispatched to the
        // executor. Per design review 🟡 #3 (tightening MC-Q3), out-of-band
        // cleanup must only fire for failures occurring AFTER dispatch — if
        // ExecuteAsync threw synchronously during argument validation, no
        // mount could have happened.
        var dispatched = false;
        // Set to true once we've decided the script's own finally is trusted
        // to have run (recognized terminal output). When false on exit, and
        // the script was dispatched, and the ISO wasn't already mounted by
        // someone else — we fire out-of-band cleanup.
        var cleanupNeeded = false;

        try
        {
            // Short timeout: mount + Get-Volume + Test-Path is a few seconds at most.
            // 60 s is generous defense against a stuck mount.
            dispatched = true;
            var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 60, ct).ConfigureAwait(false);
            var stdout = (result.Stdout ?? string.Empty).Trim();
            // Take the LAST non-empty line — defensive against any prefix banner.
            string? lastLine = null;
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) lastLine = trimmed;
            }
            lastLine ??= string.Empty;

            if (lastLine == "WIN_OK")
            {
                return (true, null);
            }
            if (lastLine == "NO_WIM")
            {
                return (false, "ISO does not contain sources\\install.wim.");
            }
            if (lastLine.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                // 🟡 #2 (Gate 2 review): an 'ERROR:' line is emitted by the
                // script's catch block, which is followed unconditionally by
                // the script's own finally — so reaching this branch proves
                // the in-script Dismount-DiskImage already ran. No leak; no
                // out-of-band cleanup needed.
                var msg = lastLine.Substring("ERROR:".Length);
                _logger.LogWarning("ISO inspection reported error for '{IsoPath}': {Message}", isoPath, msg);
                return (false, msg);
            }

            // Unexpected output (timeout, non-zero exit, etc.) — treat as not-Windows.
            // No recognized terminal line means we cannot prove the script's
            // finally ran ⇒ MC-D1 says assume leak.
            cleanupNeeded = true;
            var diag = new StringBuilder();
            diag.Append("ISO inspection produced no recognizable result");
            if (result.TimedOut) diag.Append(" (timed out)");
            if (!string.IsNullOrEmpty(result.Stderr))
                diag.Append("; stderr=").Append(result.Stderr.Trim());
            _logger.LogWarning(
                "ISO inspection unexpected output for '{IsoPath}': stdout='{Stdout}' stderr='{Stderr}' timedOut={TimedOut}",
                isoPath, stdout, result.Stderr, result.TimedOut);
            return (false, diag.ToString());
        }
        catch (OperationCanceledException)
        {
            // MC-D5: cancellation while the inspection script was running
            // means the child PS was killed mid-flight; the in-script finally
            // could not run. Fire out-of-band cleanup, then rethrow so the
            // caller sees the original cancellation unchanged (MC-D6).
            cleanupNeeded = dispatched;
            throw;
        }
        catch (Exception ex)
        {
            // 🟡 #3 / MC-Q3: cleanup only fires for exceptions that occurred
            // AFTER dispatch. If the executor threw synchronously before we
            // set `dispatched = true`, no mount could have been created.
            cleanupNeeded = dispatched;
            _logger.LogWarning(ex, "ISO inspection threw for '{IsoPath}'", isoPath);
            return (false, ex.Message);
        }
        finally
        {
            if (cleanupNeeded && !wasAlreadyMountedBeforeUs)
            {
                // Fire-and-await out-of-band dismount on a fresh PS process.
                // Uses CancellationToken.None deliberately (MC-D2): the ct we
                // were called with is already signaled in the cancel path.
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
