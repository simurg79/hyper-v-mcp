using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Tri-state verdict of the base-image generalization preflight.
/// Only <see cref="Generalized"/> permits a password-bearing <c>vm_create</c> to proceed;
/// both other states collapse to the not-generalized rejection family.
/// See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D7.
/// </summary>
public enum BaseImageGeneralizationState
{
    /// <summary>The offline image state positively read as IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE.</summary>
    Generalized,

    /// <summary>The offline image state was read and is not a generalized (resealed) state.</summary>
    NotGeneralized,

    /// <summary>No verdict: mount/registry-load error, unreadable state, timeout, or exception.</summary>
    Indeterminate,
}

/// <summary>
/// Reads the offline Windows Setup <c>ImageState</c> of a base VHDX so a password-bearing
/// <c>vm_create</c> can reject a non-generalized base before any artifact is created.
/// See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D6.
/// </summary>
public interface IBaseImageGeneralizationProbe
{
    Task<BaseImageGeneralizationState> ProbeAsync(string baseVhdxPath, CancellationToken ct = default);
}

/// <summary>
/// PowerShell-backed generalization probe. The mount is <b>read-only</b> so the base image is
/// never mutated.
///
/// <para>Leak safety mirrors the issue #113 <see cref="IsoInspector"/> shape: a pre-mount probe so
/// a pre-existing mount is never dismounted, an in-script <c>finally</c> dismount, and an
/// out-of-band dismount on a fresh PowerShell process when the child is killed before its
/// <c>finally</c> can run. A leaked VHDX mount holds a lock on the image.
/// See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D8 / VCAP-D9.</para>
/// </summary>
public sealed class BaseImageGeneralizationProbe : IBaseImageGeneralizationProbe
{
    private const int ProbeTimeoutSeconds = 120;
    private const int PreMountProbeTimeoutSeconds = 15;
    private const int CleanupTimeoutSeconds = 30;

    private const string GeneralizedToken = "GENERALIZED";
    private const string NotGeneralizedToken = "NOT_GENERALIZED";


    private readonly IPowerShellExecutor _psExecutor;
    private readonly ILogger<BaseImageGeneralizationProbe> _logger;

    public BaseImageGeneralizationProbe(
        IPowerShellExecutor psExecutor,
        ILogger<BaseImageGeneralizationProbe> logger)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<BaseImageGeneralizationState> ProbeAsync(
        string baseVhdxPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseVhdxPath))
        {
            return BaseImageGeneralizationState.Indeterminate;
        }

        var escapedPath = InputValidation.EscapePowerShellString(baseVhdxPath);

        // Refuse before dispatch, not downgrade afterwards: anything but a positive NOT_MOUNTED
        // means someone else may own the mount, so we may neither use nor dismount it.
        // See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D9.
        if (await ProbeMountedAsync(escapedPath, baseVhdxPath, ct).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Refusing the base-image generalization probe for '{BaseVhdxPath}': the image is already mounted or its mount state could not be determined.",
                baseVhdxPath);
            return BaseImageGeneralizationState.Indeterminate;
        }

        var cleanNonce = VhdxMountCleanup.NewCleanNonce();
        var dispatched = false;
        var cleanupNeeded = false;
        var mountLeaked = false;
        var state = BaseImageGeneralizationState.Indeterminate;
        try
        {
            dispatched = true;
            // Every executor call on this path suppresses script dumps, even credential-free ones.
            // See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D16.
            var result = await _psExecutor
                .ExecuteAsync(BuildProbeScript(escapedPath, cleanNonce), timeoutSeconds: ProbeTimeoutSeconds,
                    ct: ct, allowDump: false)
                .ConfigureAwait(false);

            // A killed child may still have a positive token buffered in stdout, so the timeout is
            // resolved before any token match: fail-closed holds and out-of-band dismount is armed.
            if (result.TimedOut || result.Cancelled)
            {
                cleanupNeeded = true;
                _logger.LogWarning(
                    "Base-image generalization probe did not complete for '{BaseVhdxPath}' (timedOut={TimedOut}, cancelled={Cancelled}).",
                    baseVhdxPath, result.TimedOut, result.Cancelled);
            }
            else
            {
                // The verdict does not prove the hive unloaded or the disk dismounted — the
                // script's finally suppresses both failures. Only the nonce-bearing CLEAN suffix
                // lets us skip the out-of-band dismount; text ending in "|CLEAN" cannot forge it.
                // See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D8 / VCAP-D9.
                var (lastLine, dismountProven) =
                    VhdxMountCleanup.SplitCleanTerminalLine(LastNonEmptyLine(result.Stdout), cleanNonce);
                if (!dismountProven)
                {
                    cleanupNeeded = true;
                }

                if (lastLine == GeneralizedToken)
                {
                    state = BaseImageGeneralizationState.Generalized;
                }
                else if (lastLine == NotGeneralizedToken)
                {
                    state = BaseImageGeneralizationState.NotGeneralized;
                }
                else if (lastLine.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Base-image generalization probe reported an error for '{BaseVhdxPath}': {Detail}",
                        baseVhdxPath, lastLine.Substring("ERROR:".Length));
                }
                else
                {
                    // No recognized terminal line ⇒ cannot prove the in-script finally ran.
                    cleanupNeeded = true;
                    _logger.LogWarning(
                        "Base-image generalization probe produced no recognizable verdict for '{BaseVhdxPath}': stdout='{Stdout}' stderr='{Stderr}'.",
                        baseVhdxPath, result.Stdout, result.Stderr);
                }
            }
        }
        catch (OperationCanceledException)
        {
            cleanupNeeded = dispatched;
            throw;
        }
        catch (Exception ex)
        {
            cleanupNeeded = dispatched;
            _logger.LogWarning(ex, "Base-image generalization probe threw for '{BaseVhdxPath}'.", baseVhdxPath);
        }
        finally
        {
            if (cleanupNeeded)
            {
                mountLeaked = !await VhdxMountCleanup
                    .TryOutOfBandDismountAsync(_psExecutor, escapedPath, baseVhdxPath, CleanupTimeoutSeconds, _logger)
                    .ConfigureAwait(false);
            }
        }

        // A surviving mount locks the base image, so a verdict we could neither prove dismounted
        // nor dismount ourselves is not a usable preflight answer.
        if (mountLeaked)
        {
            _logger.LogWarning(
                "Base-image generalization probe could not confirm the base image was dismounted for '{BaseVhdxPath}'; downgrading the verdict.",
                baseVhdxPath);
            return BaseImageGeneralizationState.Indeterminate;
        }
        return state;
    }

    // Fail-closed: only a positive NOT_MOUNTED proves a mount would be ours to dismount. Anything
    // else, including a probe failure, counts as pre-existing so we never dismount another owner's.
    // See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D9.
    private async Task<bool> ProbeMountedAsync(string escapedPath, string baseVhdxPath, CancellationToken ct)
    {
        try
        {
            var result = await _psExecutor
                .ExecuteAsync(VhdxMountCleanup.BuildAttachedProbeScript(escapedPath),
                    timeoutSeconds: PreMountProbeTimeoutSeconds, ct: ct, allowDump: false)
                .ConfigureAwait(false);
            if (result.TimedOut || result.Cancelled)
            {
                _logger.LogWarning(
                    "Pre-mount probe did not complete for '{BaseVhdxPath}'; treating the disk as pre-mounted.",
                    baseVhdxPath);
                return true;
            }
            return !string.Equals(
                LastNonEmptyLine(result.Stdout), "NOT_MOUNTED", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-mount probe threw for '{BaseVhdxPath}'; treating the disk as pre-mounted.", baseVhdxPath);
            return true;
        }
    }

    internal static string LastNonEmptyLine(string? stdout)
    {
        string? lastLine = null;
        foreach (var line in (stdout ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) lastLine = trimmed;
        }
        return lastLine ?? string.Empty;
    }

    // [gc]::Collect() before `reg unload` is required: reg.exe fails while the .NET registry
    // handle is still alive.
    //
    // Volume resolution walks every access path, not just DriveLetter: an offline-mounted VHD
    // frequently exposes only a volume GUID path, and resolving solely by drive letter would
    // fail to find the Windows volume and surface a spurious not-generalized rejection. This
    // mirrors the seeder's proven approach.
    // See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D6.
    internal static string BuildProbeScript(string escapedPath, string cleanNonce) => $@"
$ErrorActionPreference = 'Stop'
$path = '{escapedPath}'
$mountAttempted = $false
$hiveKey = 'HYPERVMCP_IMAGESTATE'
$hiveLoaded = $false
$verdict = $null
try {{
    $mountAttempted = $true
    $mounted = Mount-VHD -Path $path -ReadOnly -NoDriveLetter -PassThru -ErrorAction Stop
    $windowsRoot = $null
    foreach ($partition in (Get-Partition -DiskNumber $mounted.DiskNumber -ErrorAction SilentlyContinue)) {{
        $candidates = @()
        $guid = $partition.AccessPaths | Where-Object {{ $_ -like '\\?\Volume*' }} | Select-Object -First 1
        if ($guid) {{ $candidates += $guid }}
        if ($partition.DriveLetter) {{ $candidates += ($partition.DriveLetter + ':\') }}
        foreach ($candidate in $candidates) {{
            if (Test-Path -LiteralPath (Join-Path $candidate 'Windows\System32\config\SOFTWARE')) {{
                $windowsRoot = $candidate
                break
            }}
        }}
        if ($windowsRoot) {{ break }}
    }}
    if (-not $windowsRoot) {{
        $verdict = 'ERROR:No Windows volume with an offline SOFTWARE hive was found on the base image.'
        return
    }}
    $hivePath = Join-Path $windowsRoot 'Windows\System32\config\SOFTWARE'
    $regOutput = & reg.exe load ""HKLM\$hiveKey"" ""$hivePath"" 2>&1
    if ($LASTEXITCODE -ne 0) {{
        $verdict = 'ERROR:Could not load the offline SOFTWARE hive: ' + ($regOutput -join ' ')
        return
    }}
    $hiveLoaded = $true
    $imageState = (Get-ItemProperty -Path ""HKLM:\$hiveKey\Microsoft\Windows NT\CurrentVersion\Setup\State"" -Name 'ImageState' -ErrorAction Stop).ImageState
    if ($imageState -eq 'IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE') {{
        $verdict = '{GeneralizedToken}'
    }} else {{
        $verdict = '{NotGeneralizedToken}'
    }}
}} catch {{
    $message = $_.Exception.Message -replace '[\r\n]+',' '
    $verdict = 'ERROR:' + $message
}} finally {{
    $hiveUnloaded = -not $hiveLoaded
    try {{
        if ($hiveLoaded) {{
            [gc]::Collect()
            [gc]::WaitForPendingFinalizers()
            & reg.exe unload ""HKLM\$hiveKey"" 2>&1 | Out-Null
            $hiveUnloaded = ($LASTEXITCODE -eq 0)
        }}
    }} catch {{ $hiveUnloaded = $false }}
{VhdxMountCleanup.DismountVerificationFragment}
    if ($null -ne $verdict) {{
        # The nonce-bearing CLEAN suffix is the host's only proof the hive unloaded AND the disk
        # dismounted; without it the host arms an out-of-band dismount.
        if ($hiveUnloaded -and $dismounted) {{
            Write-Output ($verdict + '{VhdxMountCleanup.CleanTerminalPrefix}{cleanNonce}')
        }} else {{
            Write-Output $verdict
        }}
    }}
}}
";
}

/// <summary>
/// Shared leak-safe VHDX mount helpers for the password-seeding path.
/// See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D9.
/// </summary>
internal static class VhdxMountCleanup
{
    /// <summary>
    /// Separator introducing the dismount-proof nonce on a terminal line.
    /// </summary>
    internal const string CleanTerminalPrefix = "|CLEAN:";

    /// <summary>A fresh dismount-proof nonce; only the script we launched can echo it back.</summary>
    internal static string NewCleanNonce() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Splits a terminal line into its verdict and whether dismount was proven. Proof requires the
    /// exact nonce this call issued, so arbitrary script or error text cannot forge it.
    /// </summary>
    internal static (string Verdict, bool DismountProven) SplitCleanTerminalLine(string lastLine, string cleanNonce)
    {
        var suffix = CleanTerminalPrefix + cleanNonce;
        return lastLine.EndsWith(suffix, StringComparison.Ordinal)
            ? (lastLine.Substring(0, lastLine.Length - suffix.Length), true)
            : (lastLine, false);
    }

    /// <summary>
    /// In-script dismount. It starts from "not dismounted" and runs whenever a mount was even
    /// attempted (an exception thrown after the disk attached still leaves it attached), and
    /// confirms detachment with Get-VHD rather than trusting the cmdlet's silence.
    /// Callers MUST set <c>$mountAttempted</c> before calling Mount-VHD.
    /// </summary>
    internal const string DismountVerificationFragment = @"    $dismounted = $false
    try {
        if ($mountAttempted) {
            Dismount-VHD -Path $path -ErrorAction SilentlyContinue | Out-Null
            $dismounted = -not (Get-VHD -Path $path -ErrorAction Stop).Attached
        } else {
            $dismounted = $true
        }
    } catch { $dismounted = $false }";

    // Fail-closed: a probe failure MUST NOT read as 'nobody has this mounted', because the caller
    // uses that reading to authorize dismounting an existing mount that may belong to another
    // operation. See myplans/vm-management/vm-create/vm-create-admin-password-design.md — VCAP-D9.
    internal static string BuildAttachedProbeScript(string escapedPath) => $@"
$ErrorActionPreference = 'Stop'
try {{
    $vhd = Get-VHD -Path '{escapedPath}' -ErrorAction Stop
    if ($vhd.Attached) {{ Write-Output 'MOUNTED' }} else {{ Write-Output 'NOT_MOUNTED' }}
}} catch {{
    Write-Output 'PROBE_FAILED'
}}
";

    /// <summary>
    /// Out-of-band dismount on a fresh PowerShell process. <see cref="CancellationToken.None"/> is
    /// deliberate: the caller's token is already signalled on the paths that need this cleanup, so
    /// reusing it would skip it. Never propagates; returns whether detachment was confirmed —
    /// callers MUST NOT report success when it was not.
    /// </summary>
    internal static async Task<bool> TryOutOfBandDismountAsync(
        IPowerShellExecutor psExecutor,
        string escapedPath,
        string vhdxPath,
        int cleanupTimeoutSeconds,
        ILogger logger)
    {
        // Dismount-VHD staying silent is not evidence; the disk is re-read to confirm detachment.
        var script = $@"
$ErrorActionPreference = 'Continue'
try {{
    Dismount-VHD -Path '{escapedPath}' -ErrorAction SilentlyContinue | Out-Null
    if ((Get-VHD -Path '{escapedPath}' -ErrorAction Stop).Attached) {{
        Write-Output 'DISMOUNT_FAILED'
    }} else {{
        Write-Output 'DISMOUNTED'
    }}
}} catch {{
    Write-Output 'DISMOUNT_FAILED'
}}
";
        try
        {
            var result = await psExecutor
                .ExecuteAsync(script, timeoutSeconds: cleanupTimeoutSeconds,
                    ct: CancellationToken.None, allowDump: false)
                .ConfigureAwait(false);
            if (result.TimedOut || result.Cancelled
                || !string.Equals(
                    BaseImageGeneralizationProbe.LastNonEmptyLine(result.Stdout), "DISMOUNTED", StringComparison.Ordinal))
            {
                logger.LogWarning("Out-of-band VHDX dismount did not confirm for '{VhdxPath}'.", vhdxPath);
                return false;
            }
            logger.LogInformation("Out-of-band VHDX dismount completed for '{VhdxPath}'.", vhdxPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Out-of-band VHDX dismount threw for '{VhdxPath}'.", vhdxPath);
            return false;
        }
    }
}
