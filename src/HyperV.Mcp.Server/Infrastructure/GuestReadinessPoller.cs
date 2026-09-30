using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Outcome of the password-bearing readiness poll.
/// See internal documentation — VCAP-D11 / VCAP-D12.
/// </summary>
public enum GuestReadinessOutcome
{
    /// <summary>The guest accepted the supplied credential and the answer file was deleted.</summary>
    LoginReadyAndScrubbed,

    /// <summary>The guest accepted the credential but deletion of the answer file was not confirmed.</summary>
    LoginReadyScrubUnconfirmed,

    /// <summary>The guest never became login-ready within the readiness limit.</summary>
    DeadlineReached,

    /// <summary>The guest actively rejected the credential; further polling cannot succeed.</summary>
    CredentialRejected,

    /// <summary>A non-transport failure aborted the poll before any verdict could be reached.</summary>
    PollFailed,
}

/// <summary>
/// Bounded readiness poll: repeatedly attempts a trivial PowerShell-Direct login with the supplied
/// administrator password until it succeeds or the deadline elapses.
/// See internal documentation — VCAP-D11.
/// </summary>
public interface IGuestReadinessPoller
{
    Task<GuestReadinessOutcome> WaitForLoginReadyAsync(
        string vmName, string adminPassword, int readinessLimitSeconds, CancellationToken ct = default);
}

/// <summary>
/// PowerShell-backed <see cref="IGuestReadinessPoller"/>.
///
/// <para>Not-yet-ready and wrong-password are told apart by exception type rather than waited out
/// blindly: a transport-family failure (<c>PSRemotingDataStructureException</c> and friends) means
/// the guest is still booting, so polling continues; a <c>PSDirectException</c> means the guest
/// refused this credential and always will, so the poll stops instead of burning the budget.
/// See internal documentation — VCAP-D12.</para>
///
/// <para>On success the answer file is deleted through the session that just proved the credential
/// works, so no terminal outcome is reached with the artifact still readable.
/// See internal documentation — VCAP-D18.</para>
/// </summary>
public sealed class GuestReadinessPoller : IGuestReadinessPoller
{
    internal const string ReadyToken = "READY";
    internal const string ReadyScrubFailedToken = "READY_SCRUB_UNCONFIRMED";
    internal const string CredentialRejectedToken = "CREDENTIAL_REJECTED";
    internal const string DeadlineToken = "DEADLINE";
    internal const string FailedToken = "POLL_FAILED";

    /// <summary>Grace added to the PowerShell budget so the script reports its own deadline verdict.</summary>
    private const int ScriptBudgetGraceSeconds = 30;

    private readonly IPowerShellExecutor _psExecutor;
    private readonly ILogger<GuestReadinessPoller> _logger;

    public GuestReadinessPoller(IPowerShellExecutor psExecutor, ILogger<GuestReadinessPoller> logger)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<GuestReadinessOutcome> WaitForLoginReadyAsync(
        string vmName, string adminPassword, int readinessLimitSeconds, CancellationToken ct = default)
    {
        var script = BuildPollScript(
            InputValidation.EscapePowerShellString(vmName), readinessLimitSeconds);

        PowerShellResult result;
        try
        {
            // The credential goes to the child process out-of-band: interpolating it into the
            // script text would land it in the host temp .ps1 file.
            // See internal documentation — FR-14 / FR-16.
            result = await _psExecutor
                .ExecuteWithSecretsAsync(
                    script,
                    new Dictionary<string, string> { [AdminPasswordEnvVar] = adminPassword },
                    timeoutSeconds: readinessLimitSeconds + ScriptBudgetGraceSeconds,
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Readiness poll threw for VM '{VmName}': {ExType}", vmName, ex.GetType().Name);
            return GuestReadinessOutcome.PollFailed;
        }

        // Caller cancellation is cancellation, not a readiness deadline.
        if (result.Cancelled)
        {
            ct.ThrowIfCancellationRequested();
            return GuestReadinessOutcome.PollFailed;
        }

        var lastLine = BaseImageGeneralizationProbe.LastNonEmptyLine(result.Stdout);

        // The script owns its deadline and emits DEADLINE, so the executor killing it means no
        // verdict was reached — a poll failure, not a guest that was too slow.
        if (result.TimedOut)
        {
            _logger.LogWarning(
                "Readiness poll process for VM '{VmName}' was killed before it produced a verdict.", vmName);
            return GuestReadinessOutcome.PollFailed;
        }
        if (lastLine.StartsWith(FailedToken + ":", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Readiness poll for VM '{VmName}' hit a non-transport failure: {Detail}",
                vmName,
                CredentialResolver.RedactPasswordRepresentations(
                    lastLine.Substring(FailedToken.Length + 1).Trim(), adminPassword));
            return GuestReadinessOutcome.PollFailed;
        }

        switch (lastLine)
        {
            case ReadyToken:
                return GuestReadinessOutcome.LoginReadyAndScrubbed;
            case ReadyScrubFailedToken:
                return GuestReadinessOutcome.LoginReadyScrubUnconfirmed;
            case CredentialRejectedToken:
                return GuestReadinessOutcome.CredentialRejected;
            case DeadlineToken:
                return GuestReadinessOutcome.DeadlineReached;
        }

        // No recognized verdict: the process failed to launch or died before the loop. Calling
        // that a readiness deadline would blame the guest for a host-side failure.
        _logger.LogWarning(
            "Readiness poll for VM '{VmName}' produced no recognizable verdict (exitCode={ExitCode}).",
            vmName, result.ExitCode);
        return GuestReadinessOutcome.PollFailed;
    }

    /// <summary>
    /// Child-process environment variable carrying the administrator password. Kept out of the
    /// script text so it never reaches the host temp <c>.ps1</c> file.
    /// </summary>
    internal const string AdminPasswordEnvVar = "HYPERVMCP_ADMIN_PASSWORD";

    // Bounded-deadline loop (Get-Date + AddSeconds) rather than a blind sleep, matching the house
    // settle-poll shape. The credential is materialized in the child process only.
    internal static string BuildPollScript(string escapedVmName, int readinessLimitSeconds) => $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
$vmName = '{escapedVmName}'
$plainPassword = $env:{AdminPasswordEnvVar}
if ([string]::IsNullOrEmpty($plainPassword)) {{
    Write-Output '{FailedToken}:The administrator password was not supplied to the readiness poll.'
    return
}}
$securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential('Administrator', $securePassword)
$deadline = (Get-Date).AddSeconds({readinessLimitSeconds})
$backoffSeconds = 2
while ((Get-Date) -lt $deadline) {{
    $session = $null
    try {{
        $session = New-PSSession -VMName $vmName -Credential $credential -ErrorAction Stop
        $scrubbed = Invoke-Command -Session $session -ScriptBlock {{
            $unattendPath = Join-Path $env:SystemRoot 'Panther\unattend.xml'
            if (Test-Path -LiteralPath $unattendPath -PathType Leaf) {{
                Remove-Item -LiteralPath $unattendPath -Force -ErrorAction SilentlyContinue
            }}
            -not (Test-Path -LiteralPath $unattendPath)
        }}
        if ($scrubbed) {{ Write-Output '{ReadyToken}' }} else {{ Write-Output '{ReadyScrubFailedToken}' }}
        return
    }} catch {{
        # PSDirectException means the guest actively refused this credential — retrying cannot
        # change that, so it is matched FIRST and MUST NOT fold into the transport family below
        # (which shares its namespace). Anything else outside the transport family is a real
        # failure that MUST surface now, not be polled out as a readiness timeout.
        # See internal documentation — VCAP-D12.
        $failure = $_.Exception
        $isCredentialRejection = $false
        $isTransport = $false
        while ($null -ne $failure) {{
            $failureTypeName = $failure.GetType().FullName
            if ($failureTypeName -eq 'System.Management.Automation.Remoting.PSDirectException') {{
                $isCredentialRejection = $true
                break
            }}
            if ($failureTypeName -eq 'System.Management.Automation.Remoting.PSRemotingTransportException'
                -or $failureTypeName -eq 'System.Management.Automation.Remoting.PSRemotingDataStructureException'
                -or $failureTypeName -eq 'System.Management.Automation.PSRemotingDataStructureException') {{
                $isTransport = $true
                break
            }}
            $failure = $failure.InnerException
        }}
        if ($isCredentialRejection) {{
            Write-Output '{CredentialRejectedToken}'
            return
        }}
        if (-not $isTransport) {{
            $message = $_.Exception.Message -replace '[\r\n]+',' '
            Write-Output ('{FailedToken}:' + $message)
            return
        }}
        Start-Sleep -Seconds $backoffSeconds
        if ($backoffSeconds -lt 10) {{ $backoffSeconds = $backoffSeconds + 2 }}
    }} finally {{
        if ($null -ne $session) {{ Remove-PSSession -Session $session -ErrorAction SilentlyContinue }}
    }}
}}
Write-Output '{DeadlineToken}'
";
}
