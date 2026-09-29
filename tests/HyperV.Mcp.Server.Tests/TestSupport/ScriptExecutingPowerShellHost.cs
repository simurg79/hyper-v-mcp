using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using HyperV.Mcp.Server.Infrastructure;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>Run real scripts in a private runspace with stubbed connection cmdlets: canned success would hide deleted readiness scripts.
/// Reuse the runspace so session variables survive for cleanup; clear argument variables after each call as production does.</summary>
public sealed class ScriptExecutingPowerShellHost : IPowerShellHost, IDisposable
{
    private const string StubDefinitions = @"
class PSDirectException : System.Exception {
    PSDirectException([string]$message) : base($message) {}
}

function New-PSSession {
    [CmdletBinding()]
    param([string]$VMName, [object]$Credential, [object]$ComputerName, [object]$Id)
    $plain = $Credential.GetNetworkCredential().Password
    $global:__TestLog.Add(""AUTH vm=$VMName user=$($Credential.UserName) pass=$plain"") | Out-Null
    switch ($global:__AuthMode) {
        'reject'    { throw [PSDirectException]::new('The credential is invalid.') }
        'noisy' {
            Write-Error 'authentication reported an error' -ErrorAction Continue
            return [pscustomobject]@{ SessionId = 1; State = 'Opened' }
        }
        'transport' { throw [System.IO.IOException]::new('transport not ready') }
        'notopened' { return [pscustomobject]@{ SessionId = 1; State = 'Broken' } }
        default     { return [pscustomobject]@{ SessionId = 1; State = 'Opened' } }
    }
}

function Invoke-Command {
    [CmdletBinding()]
    param([object]$Session, [scriptblock]$ScriptBlock)
    $global:__TestLog.Add(""CONFIRM session=$($Session.SessionId) script=$ScriptBlock"") | Out-Null
    switch ($global:__ConfirmMode) {
        'fault'       { throw [System.IO.IOException]::new('confirmation transport failure') }
        'empty'       { return }
        'nonsentinel' { return 'HVMCP_NOT_THE_SENTINEL' }
        default       { return & $ScriptBlock }
    }
}

function Remove-PSSession {
    [CmdletBinding()]
    param([object]$Session)
    $global:__TestLog.Add(""CLOSE session=$($Session.SessionId)"") | Out-Null
    if ($global:__CleanupMode -eq 'fail') { throw [System.InvalidOperationException]::new('close failed') }
}
";

    private readonly Runspace _runspace;
    private readonly ArrayList _log = ArrayList.Synchronized(new ArrayList());
    private readonly List<string> _scripts = new();
    private readonly object _gate = new();

    public ScriptExecutingPowerShellHost()
    {
        _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
        _runspace.Open();
        _runspace.SessionStateProxy.SetVariable("__TestLog", _log);
        SetMode("__AuthMode", "ok");
        SetMode("__ConfirmMode", "ok");
        SetMode("__CleanupMode", "ok");
        using var setup = PowerShell.Create();
        setup.Runspace = _runspace;
        setup.AddScript(StubDefinitions);
        setup.Invoke();
        if (setup.HadErrors)
            throw new InvalidOperationException("Stub definition failed: " + string.Join("; ", setup.Streams.Error));
    }

    public PowerShellEdition Edition => PowerShellEdition.PowerShell7;

    /// <summary>Every script text this host was asked to run, in order.</summary>
    public IReadOnlyList<string> InvokedScripts { get { lock (_gate) { return _scripts.ToArray(); } } }

    /// <summary>Stub-recorded connection events, in order.</summary>
    public IReadOnlyList<string> Events => _log.Cast<object>().Select(entry => entry?.ToString() ?? string.Empty).ToArray();

    public int AuthenticationCount => Events.Count(entry => entry.StartsWith("AUTH ", StringComparison.Ordinal));
    public int ConfirmationCount => Events.Count(entry => entry.StartsWith("CONFIRM ", StringComparison.Ordinal));
    public int CloseCount => Events.Count(entry => entry.StartsWith("CLOSE ", StringComparison.Ordinal));

    public void SetMode(string variableName, string value)
        => _runspace.SessionStateProxy.SetVariable(variableName, value);

    public void SetAuthMode(string value) => SetMode("__AuthMode", value);
    public void SetConfirmMode(string value) => SetMode("__ConfirmMode", value);
    public void SetCleanupMode(string value) => SetMode("__CleanupMode", value);

    /// <summary>Seeds the shared-session table so its survival across a readiness call is observable.</summary>
    public void SeedSharedSessionCache(Hashtable table)
        => _runspace.SessionStateProxy.SetVariable("__HvMcpSessions", table);

    public Hashtable? ReadSharedSessionCache()
        => _runspace.SessionStateProxy.GetVariable("__HvMcpSessions") as Hashtable;

    /// <summary>Names of variables still defined in the runspace — an orphan session is visible here.</summary>
    public IReadOnlyList<string> ReadinessVariableNames()
    {
        using var probe = PowerShell.Create();
        probe.Runspace = _runspace;
        probe.AddScript("Get-Variable -Scope Global -Name '__HvMcpReadiness_*' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }");
        return probe.Invoke().Select(item => item?.ToString() ?? string.Empty).ToList();
    }

    /// <summary>Cancels <paramref name="source"/> once the given 1-based invocation has run.</summary>
    public void CancelAfterInvocation(int invocationNumber, CancellationTokenSource source)
    {
        _cancelAfterInvocation = invocationNumber;
        _cancellationSource = source;
    }

    /// <summary>Runs <paramref name="expire"/> after the given 1-based invocation, to age the clock mid-call.</summary>
    public void ExpireAfterInvocation(int invocationNumber, Action expire)
    {
        _expireAfterInvocation = invocationNumber;
        _expireAction = expire;
    }

    private int _cancelAfterInvocation = -1;
    private CancellationTokenSource? _cancellationSource;
    private int _expireAfterInvocation = -1;
    private Action? _expireAction;

    public Task EnsureInitializedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<PowerShellHostResult> InvokeAsync(
        string script, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _scripts.Add(script);
        }

        ct.ThrowIfCancellationRequested();
        using var shell = PowerShell.Create();
        shell.Runspace = _runspace;
        if (args is not null)
        {
            foreach (var pair in args)
                _runspace.SessionStateProxy.SetVariable(pair.Key, pair.Value);
        }

        shell.AddScript(script);
        var output = shell.Invoke();
        var stderr = string.Join("\n", shell.Streams.Error.Select(record => record.ToString()));
        shell.Streams.Error.Clear();

        if (args is not null)
        {
            // Clear argument variables as production does; retaining them would hide dependencies on previous calls.
            foreach (var pair in args)
                _runspace.SessionStateProxy.SetVariable(pair.Key, null);
        }

        int invocationNumber;
        lock (_gate)
        {
            invocationNumber = _scripts.Count;
        }
        if (invocationNumber == _cancelAfterInvocation)
            _cancellationSource?.Cancel();
        if (invocationNumber == _expireAfterInvocation)
            _expireAction?.Invoke();

        var success = string.IsNullOrEmpty(stderr);
        return Task.FromResult(new PowerShellHostResult(
            success, output.Select(item => (object?)item?.BaseObject).ToList(), stderr, success ? 0 : 1));
    }

    public Task<PowerShellHostResult> InvokeWithTimeoutAsync(
        string script, IDictionary<string, object?>? args, int? timeoutSeconds, CancellationToken ct = default)
        => InvokeAsync(script, args, ct);

    public Task<string> GetVmStateAsync(string hostId, string vmId, CancellationToken ct = default)
        => Task.FromResult("Running");

    public PowerShellHostInitDiagnostics GetInitDiagnostics()
        => new(true, PowerShellEdition.PowerShell7, null, null, null, null);

    public void Dispose() => _runspace.Dispose();
}
