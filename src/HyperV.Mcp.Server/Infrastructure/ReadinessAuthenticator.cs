using System.Management.Automation;
using System.Security;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

public sealed class ReadinessAuthenticator
{
    private readonly IPowerShellHost? _host;
    private readonly ISshExecClientFactory _sshFactory;
    private readonly ILogger _logger;

    public ReadinessAuthenticator(IPowerShellHost? host, ISshExecClientFactory sshFactory, ILogger logger)
    {
        _host = host;
        _sshFactory = sshFactory;
        _logger = logger;
    }

    public async Task<ReadinessVerdict> ConfirmWindowsAsync(
        string vmId, string vmName, string username, string password, ReadinessBudget budget, CancellationToken ct)
    {
        if (_host is null)
            return new(ReadinessVerdictKind.Terminal, "Observation failed: PowerShell guest access is unavailable.");

        var sessionVariable = "__HvMcpReadiness_" + Guid.NewGuid().ToString("N");
        using var securePassword = new SecureString();
        foreach (var character in password)
            securePassword.AppendChar(character);
        securePassword.MakeReadOnly();
        var arguments = new Dictionary<string, object?>
        {
            ["__readinessVmName"] = vmName,
            ["__readinessCredential"] = new PSCredential(username, securePassword)
        };

        // The retained session MUST NOT be an argument: the host clears argument variables after each invocation.
        var authenticate = $@"
$ErrorActionPreference = 'Stop'
try {{
    ${sessionVariable} = New-PSSession -VMName $__readinessVmName -Credential $__readinessCredential -ErrorAction Stop
    if ($null -eq ${sessionVariable} -or ${sessionVariable}.State -ne 'Opened') {{ throw 'Session not opened' }}
    Write-Output 'HVMCP_AUTHENTICATED'
}} catch {{
    $failure = $_.Exception
    $rejected = $false
    while ($null -ne $failure) {{
        if ($failure.GetType().Name -eq 'PSDirectException') {{ $rejected = $true }}
        $failure = $failure.InnerException
    }}
    if ($rejected) {{ Write-Error 'HVMCP_CREDENTIAL_REJECTED' -ErrorAction Continue }}
    else {{ Write-Error 'HVMCP_AUTHENTICATION_UNCONFIRMED' -ErrorAction Continue }}
}}";
        var confirm = $@"
$ErrorActionPreference = 'Stop'
try {{
    Invoke-Command -Session ${sessionVariable} -ScriptBlock {{ Write-Output 'HVMCP_READY' }} -ErrorAction Stop
}} catch {{ Write-Error 'HVMCP_CONFIRMATION_FAILED' -ErrorAction Continue }}";
        var cleanup = $@"
try {{
    if ($null -ne ${sessionVariable}) {{ Remove-PSSession -Session ${sessionVariable} -ErrorAction Stop }}
}} catch {{ Write-Error 'HVMCP_READINESS_CLOSE_FAILED' -ErrorAction Continue }}
finally {{ Remove-Variable -Name '{sessionVariable}' -Scope Local -Force -ErrorAction SilentlyContinue }}";

        budget.Check("authentication", vmId, ct);
        try
        {
            var authentication = await _host.InvokeAsync(authenticate, arguments, ct).ConfigureAwait(false);
            budget.Record("authentication-completed");
            ct.ThrowIfCancellationRequested();
            if (!authentication.Success || authentication.ExitCode != 0
                || !string.IsNullOrWhiteSpace(authentication.Stderr)
                || !TokenMatcher.ContainsToken(OutputText(authentication), "HVMCP_AUTHENTICATED"))
            {
                return ReadinessVerdict.Evaluate(false, true, null, authentication.Stderr);
            }

            budget.LastObservation = "Fresh authentication completed; the guest operation is not yet confirmed.";
            budget.Check("confirmation", vmId, ct);
            var result = await _host.InvokeAsync(confirm, null, ct).ConfigureAwait(false);
            budget.Record("confirmation-completed");
            ct.ThrowIfCancellationRequested();
            return ReadinessVerdict.Evaluate(
                result.Success && result.ExitCode == 0,
                !string.IsNullOrWhiteSpace(result.Stderr), OutputText(result), result.Stderr);
        }
        catch (ReadinessNotReachedException) { throw; }
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            return ReadinessVerdict.Evaluate(false, true, null, fault: failure);
        }
        finally
        {
            budget.Record("cleanup");
            try
            {
                var closed = await _host.InvokeAsync(cleanup, null, CancellationToken.None).ConfigureAwait(false);
                if (!closed.Success)
                    _logger.LogWarning("Readiness session close could not be confirmed.");
            }
            catch (Exception)
            {
                _logger.LogWarning("Readiness session close could not be confirmed.");
            }
            budget.Record("cleanup-completed");
        }
    }

    public async Task<ReadinessVerdict> ConfirmLinuxAsync(
        string vmId, string sshHost, int sshPort, string username, string password,
        ReadinessBudget budget, CancellationToken ct)
    {
        ISshExecClient? client = null;
        budget.Check("authentication", vmId, ct);
        try
        {
            client = await _sshFactory.ConnectAsync(sshHost, sshPort, username, password, ct).ConfigureAwait(false);
            budget.Record("authentication-completed");
            budget.LastObservation = "Fresh authentication completed; the guest operation is not yet confirmed.";
            budget.Check("confirmation", vmId, ct);
            var result = await client.ExecuteAsync("echo HVMCP_READY", ct).ConfigureAwait(false);
            budget.Record("confirmation-completed");
            ct.ThrowIfCancellationRequested();
            return ReadinessVerdict.Evaluate(result.ExitStatus == 0,
                !string.IsNullOrWhiteSpace(result.Stderr), result.Stdout, result.Stderr);
        }
        catch (ReadinessNotReachedException) { throw; }
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            return ReadinessVerdict.Evaluate(false, true, null, fault: failure);
        }
        finally
        {
            budget.Record("cleanup");
            try { client?.Dispose(); }
            catch (Exception)
            {
                _logger.LogWarning("Readiness SSH connection close could not be confirmed.");
            }
            budget.Record("cleanup-completed");
        }
    }

    private static string OutputText(PowerShellHostResult result) =>
        string.Join(Environment.NewLine, result.Output.Select(value => value?.ToString()));
}
