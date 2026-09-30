using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Writes and removes the guest-delivery answer file (<c>\Windows\Panther\unattend.xml</c>) inside
/// a freshly-created differencing VHDX, by offline-mounting it before first boot.
///
/// <para>The seam that tests record at is <see cref="IPowerShellExecutor"/>, NOT this interface:
/// correctness assertions run the production seeder over a recording executor and inspect the
/// composed script. See
/// internal documentation — VCAP-D21 / VCAP-D24.</para>
/// </summary>
public interface IUnattendSeeder
{
    /// <summary>
    /// Offline-mounts <paramref name="diffVhdxPath"/> and writes the rendered answer file to
    /// <c>\Windows\Panther\unattend.xml</c>, overwriting any file inherited from the base image.
    /// The VM MUST NOT have been started yet.
    /// </summary>
    Task SeedAsync(string diffVhdxPath, string vmName, string adminPassword, string locale,
        CancellationToken ct = default);

    /// <summary>
    /// Offline-mounts <paramref name="diffVhdxPath"/> and deletes the answer file. Requires the VM
    /// to be Off. Returns <c>true</c> only when absence of the file is confirmed.
    /// </summary>
    Task<bool> ScrubOfflineAsync(string diffVhdxPath, CancellationToken ct = default);
}

/// <summary>
/// PowerShell-backed <see cref="IUnattendSeeder"/>.
///
/// <para>The answer file is written directly to the canonical Panther path because an inherited
/// <c>\Windows\Panther\unattend.xml</c> silently beats an attached DVD answer file — a live spike
/// showed that failure mode is indistinguishable from the original bug. Overwrite is therefore
/// unconditional. The file is UTF-8 <b>without BOM</b>: the Windows Setup specialize parser
/// rejects a BOM.
/// See internal documentation — VCAP-D1 / VCAP-D3 / VCAP-D10.</para>
/// </summary>
public sealed class UnattendSeeder : IUnattendSeeder
{
    private const int SeedTimeoutSeconds = 180;
    private const int PreMountProbeTimeoutSeconds = 15;
    private const int CleanupTimeoutSeconds = 30;

    internal const string SeededToken = "SEEDED";
    internal const string ScrubbedToken = "SCRUBBED";

    /// <summary>Canonical guest path of the answer file consumed on first boot.</summary>
    internal const string GuestPantherRelativePath = @"Windows\Panther\unattend.xml";

    private readonly IPowerShellExecutor _psExecutor;
    private readonly ILogger<UnattendSeeder> _logger;

    public UnattendSeeder(IPowerShellExecutor psExecutor, ILogger<UnattendSeeder> logger)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SeedAsync(string diffVhdxPath, string vmName, string adminPassword, string locale,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(diffVhdxPath))
            throw new ArgumentException("The differencing VHDX path is required.", nameof(diffVhdxPath));
        // Whitespace-only is rejected here too, matching InputValidation.ValidateAdminPassword:
        // a blank password is accepted by neither Windows Setup nor a later logon.
        if (string.IsNullOrWhiteSpace(adminPassword))
            throw new ArgumentException("The administrator password is required.", nameof(adminPassword));

        var unattendXml = RenderPantherUnattend(vmName, adminPassword, locale);
        var escapedDiffPath = InputValidation.EscapePowerShellString(diffVhdxPath);

        // The rendered answer file carries the plaintext password, so it goes through the child
        // process environment block: interpolating it would materialize it in a host temp .ps1.
        // See internal documentation — FR-14 / FR-16.
        var (succeeded, diagnostic) = await RunMountedScriptAsync(
            escapedDiffPath, diffVhdxPath,
            nonce => BuildSeedScript(escapedDiffPath, nonce),
            SeededToken, adminPassword,
            new Dictionary<string, string> { [UnattendXmlEnvVar] = unattendXml },
            ct).ConfigureAwait(false);

        if (!succeeded)
        {
            // Fixed text plus an already-redacted diagnostic: no raw script or stderr may reach
            // this message. See internal documentation — FR-16.
            throw new InvalidOperationException(
                "Failed to write the administrator-password answer file into the new VM's disk. " + diagnostic);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ScrubOfflineAsync(string diffVhdxPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(diffVhdxPath))
        {
            return false;
        }

        var escapedDiffPath = InputValidation.EscapePowerShellString(diffVhdxPath);
        var (succeeded, _) = await RunMountedScriptAsync(
            escapedDiffPath, diffVhdxPath,
            nonce => BuildScrubScript(escapedDiffPath, nonce),
            ScrubbedToken, password: null, secretEnvironment: null, ct).ConfigureAwait(false);
        return succeeded;
    }

    /// <summary>
    /// Renders the shared Panther template. Reuses the <c>vm_os_install</c> escaping pattern rather
    /// than introducing a second answer-file dialect.
    /// See internal documentation — VCAP-D5.
    /// </summary>
    internal static string RenderPantherUnattend(string vmName, string adminPassword, string locale)
    {
        // NetBIOS computer names are capped at 15 characters, matching the OS-install path.
        var computerName = vmName.Length > 15 ? vmName.Substring(0, 15) : vmName;
        return HyperVManager.PantherUnattendTemplate
            .Replace("{locale}", System.Security.SecurityElement.Escape(locale))
            .Replace("{adminPassword}", System.Security.SecurityElement.Escape(adminPassword))
            .Replace("{vmName}", System.Security.SecurityElement.Escape(computerName));
    }

    /// <summary>
    /// Leak-safe driver: pre-mount probe, mount/act/dismount script, then an out-of-band dismount
    /// whenever the in-script <c>finally</c> cannot be proven to have run. A leaked mount holds a
    /// write lock on the diff disk and would block <c>Start-VM</c>.
    /// </summary>
    private async Task<(bool Succeeded, string Diagnostic)> RunMountedScriptAsync(
        string escapedDiffPath, string diffVhdxPath, Func<string, string> buildScript, string successToken,
        string? password, IReadOnlyDictionary<string, string>? secretEnvironment, CancellationToken ct)
    {
        // Refuse before dispatch, not downgrade afterwards: anything but a positive NOT_MOUNTED
        // means someone else may own the mount, so we may neither use nor dismount it.
        // See internal documentation — VCAP-D9.
        if (await ProbeMountedAsync(escapedDiffPath, diffVhdxPath, ct).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Refusing the offline disk operation for '{DiffVhdxPath}': the disk is already mounted or its mount state could not be determined.",
                diffVhdxPath);
            return (false, "The disk was already mounted or its mount state could not be determined.");
        }

        var cleanNonce = VhdxMountCleanup.NewCleanNonce();
        var script = buildScript(cleanNonce);
        var dispatched = false;
        var cleanupNeeded = false;
        var mountLeaked = false;
        (bool Succeeded, string Diagnostic) outcome;
        try
        {
            dispatched = true;
            // Both entry points suppress script dumps; the seeding path also keeps the secret out
            // of the script text. See internal documentation — VCAP-D16.
            var result = secretEnvironment is null
                ? await _psExecutor
                    .ExecuteAsync(script, timeoutSeconds: SeedTimeoutSeconds, ct: ct, allowDump: false)
                    .ConfigureAwait(false)
                : await _psExecutor
                    .ExecuteWithSecretsAsync(script, secretEnvironment, timeoutSeconds: SeedTimeoutSeconds, ct: ct)
                    .ConfigureAwait(false);

            if (result.TimedOut || result.Cancelled)
            {
                cleanupNeeded = true;
                outcome = (false, "The offline disk operation did not complete within its budget.");
            }
            else
            {
                // A terminal token is not evidence of dismount — the script's finally suppresses
                // dismount failures. Only the nonce-bearing CLEAN suffix proves it; anything else
                // arms the out-of-band dismount so no mount leaks and blocks Start-VM.
                // See internal documentation — VCAP-D9.
                var (lastLine, dismountProven) = VhdxMountCleanup.SplitCleanTerminalLine(
                    BaseImageGeneralizationProbe.LastNonEmptyLine(result.Stdout), cleanNonce);
                if (!dismountProven)
                {
                    cleanupNeeded = true;
                }

                if (lastLine == successToken)
                {
                    outcome = (true, string.Empty);
                }
                else if (lastLine.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    outcome = (false, Redact(lastLine.Substring("ERROR:".Length).Trim(), password));
                }
                else
                {
                    cleanupNeeded = true;
                    outcome = (false, "The offline disk operation produced no recognizable result.");
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
            _logger.LogWarning("Offline disk operation threw for '{DiffVhdxPath}': {ExType}", diffVhdxPath, ex.GetType().Name);
            outcome = (false, Redact(ex.Message, password));
        }
        finally
        {
            if (cleanupNeeded)
            {
                mountLeaked = !await VhdxMountCleanup
                    .TryOutOfBandDismountAsync(_psExecutor, escapedDiffPath, diffVhdxPath, CleanupTimeoutSeconds, _logger)
                    .ConfigureAwait(false);
            }
        }

        // A surviving mount holds a write lock and would block Start-VM, so an unverifiable
        // cleanup MUST NOT be reported as success even when the script emitted its success token.
        if (outcome.Succeeded && mountLeaked)
        {
            return (false, "The offline disk operation could not be confirmed to have released the disk.");
        }
        return outcome;
    }

    private static string Redact(string text, string? password)
        => password is null ? text : CredentialResolver.RedactPasswordRepresentations(text, password);

    // Fail-closed: only a positive NOT_MOUNTED proves a mount would be ours to dismount. Anything
    // else, including a probe failure, counts as pre-existing so we never dismount another owner's.
    // See internal documentation — VCAP-D9.
    private async Task<bool> ProbeMountedAsync(string escapedDiffPath, string diffVhdxPath, CancellationToken ct)
    {
        try
        {
            var result = await _psExecutor
                .ExecuteAsync(VhdxMountCleanup.BuildAttachedProbeScript(escapedDiffPath),
                    timeoutSeconds: PreMountProbeTimeoutSeconds, ct: ct, allowDump: false)
                .ConfigureAwait(false);
            if (result.TimedOut || result.Cancelled)
            {
                _logger.LogWarning(
                    "Pre-mount probe did not complete for '{DiffVhdxPath}'; treating the disk as pre-mounted.",
                    diffVhdxPath);
                return true;
            }
            return !string.Equals(
                BaseImageGeneralizationProbe.LastNonEmptyLine(result.Stdout), "NOT_MOUNTED", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-mount probe threw for '{DiffVhdxPath}'; treating the disk as pre-mounted.", diffVhdxPath);
            return true;
        }
    }

    /// <summary>
    /// Child-process environment variable that carries the rendered answer file. The value never
    /// appears in the script text, so it never reaches the host temp <c>.ps1</c> file.
    /// </summary>
    internal const string UnattendXmlEnvVar = "HYPERVMCP_UNATTEND_XML";

    // -NoDriveLetter avoids a transient drive-letter race; the Windows volume is found by
    // partition. UTF8Encoding($false) is load-bearing: Set-Content -Encoding UTF8 emits a BOM,
    // which the specialize parser rejects.
    internal static string BuildSeedScript(string escapedDiffPath, string cleanNonce) => $@"
$ErrorActionPreference = 'Stop'
$path = '{escapedDiffPath}'
$diffPath = $path
$unattendXml = $env:{UnattendXmlEnvVar}
$mountAttempted = $false
$verdict = $null
try {{
    if ([string]::IsNullOrEmpty($unattendXml)) {{
        $verdict = 'ERROR:The answer file content was not supplied to the offline disk operation.'
        return
    }}
    $mountAttempted = $true
    $mounted = Mount-VHD -Path $diffPath -NoDriveLetter -Passthru -ErrorAction Stop
    $windowsRoot = $null
    foreach ($partition in (Get-Partition -DiskNumber $mounted.DiskNumber -ErrorAction SilentlyContinue)) {{
        $guid = $partition.AccessPaths | Where-Object {{ $_ -like '\\?\Volume*' }} | Select-Object -First 1
        if (-not $guid) {{ continue }}
        if (Test-Path -LiteralPath (Join-Path $guid 'Windows\System32\config\SOFTWARE')) {{
            $windowsRoot = $guid
            break
        }}
    }}
    if (-not $windowsRoot) {{
        $verdict = 'ERROR:No Windows volume was found on the new disk.'
        return
    }}
    $pantherDir = Join-Path $windowsRoot 'Windows\Panther'
    if (-not (Test-Path -LiteralPath $pantherDir)) {{
        New-Item -ItemType Directory -Path $pantherDir -Force | Out-Null
    }}
    $unattendPath = Join-Path $pantherDir 'unattend.xml'
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($unattendPath, $unattendXml, $utf8NoBom)
    if (-not (Test-Path -LiteralPath $unattendPath -PathType Leaf)) {{
        $verdict = 'ERROR:The answer file was not present after writing.'
        return
    }}
    $verdict = '{SeededToken}'
}} catch {{
    $message = $_.Exception.Message -replace '[\r\n]+',' '
    $verdict = 'ERROR:' + $message
}} finally {{
{VhdxMountCleanup.DismountVerificationFragment}
    if ($null -ne $verdict) {{
        # The nonce-bearing CLEAN suffix is the host's only proof the dismount succeeded.
        if ($dismounted) {{
            Write-Output ($verdict + '{VhdxMountCleanup.CleanTerminalPrefix}{cleanNonce}')
        }} else {{
            Write-Output $verdict
        }}
    }}
}}
";

    internal static string BuildScrubScript(string escapedDiffPath, string cleanNonce) => $@"
$ErrorActionPreference = 'Stop'
$path = '{escapedDiffPath}'
$diffPath = $path
$mountAttempted = $false
$verdict = $null
try {{
    $mountAttempted = $true
    $mounted = Mount-VHD -Path $diffPath -NoDriveLetter -Passthru -ErrorAction Stop
    $windowsRoot = $null
    foreach ($partition in (Get-Partition -DiskNumber $mounted.DiskNumber -ErrorAction SilentlyContinue)) {{
        $guid = $partition.AccessPaths | Where-Object {{ $_ -like '\\?\Volume*' }} | Select-Object -First 1
        if (-not $guid) {{ continue }}
        if (Test-Path -LiteralPath (Join-Path $guid 'Windows\System32\config\SOFTWARE')) {{
            $windowsRoot = $guid
            break
        }}
    }}
    if (-not $windowsRoot) {{
        $verdict = 'ERROR:No Windows volume was found on the disk.'
        return
    }}
    $unattendPath = Join-Path $windowsRoot '{GuestPantherRelativePath}'
    if (Test-Path -LiteralPath $unattendPath -PathType Leaf) {{
        Remove-Item -LiteralPath $unattendPath -Force -ErrorAction Stop
    }}
    if (Test-Path -LiteralPath $unattendPath) {{
        $verdict = 'ERROR:The answer file is still present after deletion.'
        return
    }}
    $verdict = '{ScrubbedToken}'
}} catch {{
    $message = $_.Exception.Message -replace '[\r\n]+',' '
    $verdict = 'ERROR:' + $message
}} finally {{
{VhdxMountCleanup.DismountVerificationFragment}
    if ($null -ne $verdict) {{
        if ($dismounted) {{
            Write-Output ($verdict + '{VhdxMountCleanup.CleanTerminalPrefix}{cleanNonce}')
        }} else {{
            Write-Output $verdict
        }}
    }}
}}
";
}
