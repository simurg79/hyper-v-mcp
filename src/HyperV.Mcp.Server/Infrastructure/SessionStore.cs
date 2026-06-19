using System.Collections.Concurrent;
using System.Management.Automation;
using System.Security;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Real lifecycle owner of persistent <c>PSSession</c> objects targeting Hyper-V guest VMs.
/// Sessions are created via <c>New-PSSession -VMId</c> inside the singleton
/// <see cref="IPowerShellHost"/> runspace and stashed in the runspace-global
/// hashtable <c>$global:__HvMcpSessions</c>, keyed by session name.
/// </summary>
/// <remarks>
/// <para>See <c>/myplans/remoting/session-management/session-management-design.md</c> — SM-D1, SM-D2, SM-D6.</para>
/// <para>SM-D3 (idle-eviction sweeper) is deliberately NOT implemented — eviction is on-demand only via
/// <see cref="EvictAsync"/> (called by the channel on broken-session retry, or by <c>ToolDispatcher</c>
/// before a destructive VM operation).</para>
/// </remarks>
public sealed class SessionStore : ISessionStore, IDisposable
{
    private readonly IPowerShellHost _host;
    private readonly ILogger<SessionStore> _logger;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    // TODO(SM-D3 follow-up): semaphore eviction synchronized with sweeper.
    // Per-(hostId,vmId) entries are added on first access and never removed —
    // an acceptable leak in current design. The deferred SM-D3 idle sweeper
    // (and a coordinated removal protocol with awaiting callers) will own this
    // cleanup. Disposing semaphores here while any caller is awaiting WaitAsync
    // races into ObjectDisposedException, so we leave it for the sweeper.
    // (Issue #52, Gate 6 Fix #5.)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="SessionStore"/> bound to the supplied PowerShell host.
    /// </summary>
    /// <param name="host">Singleton in-process PowerShell host that owns the runspace.</param>
    /// <param name="logger">Logger.</param>
    public SessionStore(IPowerShellHost host, ILogger<SessionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        _host = host;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SessionHandle> GetOrCreateAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var key = BuildKey(hostId, vmId);
        var gate = GetLock(key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                // SM-D6: mandatory health check on cache hit.
                if (await IsAliveCoreAsync(existing.SessionName, ct).ConfigureAwait(false))
                {
                    existing.LastUsedUtc = DateTime.UtcNow;
                    return new SessionHandle(hostId, vmId, existing.SessionName);
                }

                _logger.LogInformation(
                    "SessionStore: cached session {SessionName} for {HostId}/{VmId} failed health check, recreating",
                    existing.SessionName, hostId, vmId);
                await TryRemoveSessionInRunspaceAsync(existing.SessionName, ct).ConfigureAwait(false);
                _sessions.TryRemove(key, out _);
            }

            var sessionName = BuildSessionName(hostId, vmId);
            await CreateSessionInRunspaceAsync(sessionName, vmId, username, password, ct).ConfigureAwait(false);

            var entry = new SessionEntry
            {
                HostId = hostId,
                VmId = vmId,
                SessionName = sessionName,
                CreatedUtc = DateTime.UtcNow,
                LastUsedUtc = DateTime.UtcNow,
            };
            _sessions[key] = entry;
            return new SessionHandle(hostId, vmId, sessionName);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAliveAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);

        var key = BuildKey(hostId, vmId);
        if (!_sessions.TryGetValue(key, out var entry))
        {
            return false;
        }

        return await IsAliveCoreAsync(entry.SessionName, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EvictAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);

        var key = BuildKey(hostId, vmId);
        var gate = GetLock(key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _logger.LogDebug(
                    "SessionStore: evicting session {SessionName} for {HostId}/{VmId}",
                    entry.SessionName, hostId, vmId);
                await TryRemoveSessionInRunspaceAsync(entry.SessionName, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EvictAllAsync(string hostId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        var snapshot = _sessions
            .Where(kvp => string.Equals(kvp.Value.HostId, hostId, StringComparison.Ordinal))
            .Select(kvp => kvp.Value)
            .ToList();

        foreach (var entry in snapshot)
        {
            await EvictAsync(entry.HostId, entry.VmId, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public bool HasSession(string hostId, string vmId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        return _sessions.ContainsKey(BuildKey(hostId, vmId));
    }

    /// <summary>
    /// Disposes the store: best-effort removes every PSSession from the runspace
    /// and disposes per-key semaphores. The host's runspace cleanup will reclaim
    /// any remaining sessions on process exit if Dispose is missed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var entries = _sessions.Values.ToList();
        foreach (var entry in entries)
        {
            try
            {
                // Best-effort synchronous cleanup. The host runspace will free
                // anything left behind on process exit anyway.
                TryRemoveSessionInRunspaceAsync(entry.SessionName, CancellationToken.None)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SessionStore.Dispose: best-effort removal of session {SessionName} failed",
                    entry.SessionName);
            }
        }

        foreach (var sem in _locks.Values)
        {
            try { sem.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SessionStore.Dispose: SemaphoreSlim disposal failed");
            }
        }

        _sessions.Clear();
        _locks.Clear();
    }

    // ---------------- internal helpers ----------------

    /// <summary>
    /// Builds the deterministic, sanitized session name used as the key into
    /// <c>$global:__HvMcpSessions</c>. Preserved from the prior implementation so that
    /// callers (e.g. <c>CommandExecutor</c>, <c>FileTransferService</c>) producing
    /// the same name out-of-band continue to align with the store.
    /// </summary>
    internal static string BuildSessionName(string hostId, string vmId)
    {
        var sanitizedHost = SanitizeForSessionName(hostId);
        var sanitizedVm = SanitizeForSessionName(vmId);
        return $"hyperv-mcp-{sanitizedHost}-{sanitizedVm}";
    }

    /// <summary>Compound dictionary key per SM-D2.</summary>
    private static string BuildKey(string hostId, string vmId) => $"{hostId}::{vmId}";

    /// <summary>Replace non-alphanumeric characters (except hyphen) with hyphen.</summary>
    private static string SanitizeForSessionName(string input)
    {
        var chars = input.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        return new string(chars);
    }

    private SemaphoreSlim GetLock(string key) =>
        _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Creates the underlying <c>PSSession</c> via <c>New-PSSession -VMId</c> inside the
    /// host runspace and stashes it in <c>$global:__HvMcpSessions[$sessionName]</c>.
    /// </summary>
    private async Task CreateSessionInRunspaceAsync(
        string sessionName,
        string vmId,
        string username,
        string password,
        CancellationToken ct)
    {
        // RC-10.1 (Issue #52 Phase 2): wrap New-PSSession in a PowerShell-level
        // try/catch so the underlying exception detail is forced into the error
        // stream BEFORE the terminating `throw` re-surfaces it. Without this
        // wrapper, a terminating exception under `-ErrorAction Stop` was
        // bypassing `ps.Streams.Error` in production, leaving result.Stderr
        // empty and the C# catch path producing the opaque message
        // "Failed to create PSSession 'hyperv-mcp-...': " (RC-10 symptom).
        // The Write-Error -ErrorAction Continue call deliberately writes a
        // non-terminating record so the populated text reaches the host's
        // error stream regardless of how PS handles the subsequent throw.
        // This is a DIAGNOSTIC patch — the underlying root cause (likely the
        // `-VMId` parameter binding, but unconfirmed) is intentionally NOT
        // changed here; RC-10.2 will use the surfaced detail to write the fix.
        // RC-10.3a Layer 2: extend the RC-10.1 try/catch to inspect ALL the
        // ErrorRecord facets that PowerShell may use to carry the real
        // failure text. RC-10.3 production showed that the underlying
        // Exception.Message can be EMPTY while ErrorDetails.Message,
        // FullyQualifiedErrorId, and CategoryInfo carry the real signal.
        // Also emit a pre-call discovery snapshot so we can confirm whether
        // New-PSSession is even resolvable at the moment we invoke it.
        // All emissions are tagged with the RC103a: prefix so the
        // diagnostic block is greppable in stderr / logs.
        // RC-10.3b Layer A: build PSCredential in C# instead of inside the
        // PS script. The previous in-script `ConvertTo-SecureString` +
        // `New-Object System.Management.Automation.PSCredential` pair forced
        // PowerShell to auto-load the bundled (app-local, unsigned)
        // Microsoft.PowerShell.Security module under
        //   runtimes\win\lib\net8.0\Modules\Microsoft.PowerShell.Security
        // because Microsoft.PowerShell.SDK re-injects that path into
        // $env:PSModulePath after our RC-10.2 strip runs. Under Code
        // Integrity, AuthorizationManager.PassesPolicyCheck() then rejects
        // the unsigned Security.types.ps1xml, surfacing as
        //   CommandNotFoundException: ConvertTo-SecureString
        //   FQEID=CouldNotAutoloadMatchingModule
        // (RC-10.3-meta smoking gun captured in %TEMP%\rc103-meta.log).
        // Building the credential in C# eliminates the auto-load entirely.
        const string script = @"
$ErrorActionPreference = 'Stop'

# RC-11.10: $PSDefaultParameterValues injection — the definitive LF-D7 cure.
#
# Smoke probe #7 evidence (full 6KB stderr captured 2026-04-30):
#   --- error[0] ---
#   Get-VM : Value cannot be null. Parameter name: name
#   At line:1 char:1
#   + Get-VM -Name $args
#
# This `Get-VM -Name $args` at line 1 char 1 (no file) is NOT in our script.
# It is INTERNALLY SYNTHESIZED by `New-PSSession -VMName/-VMId`'s parameter
# resolver to look up the VM's WMI socket address. It runs WITHOUT
# -ComputerName, so it hits the LF-D7 bug
#   Server.GetServer(name=null) -> ArgumentNullException
# wrapped as Microsoft.HyperV.PowerShell.VirtualizationException.
#
# RC-11.4 (-ComputerName localhost on our explicit Get-VM call) does NOT
# cover this internal invocation — we never see it as a parameter we control.
# RC-11.9 (-VMId -> -VMName switch) was a no-op: both routes go through the
# same broken Hyper-V SDK Server.GetServer(name=null) path.
#
# $PSDefaultParameterValues is a PowerShell preference variable that injects
# parameter values into ALL invocations of a cmdlet, INCLUDING synthesized
# internal calls. By setting Get-VM:ComputerName='localhost' here, the
# implicit Get-VM inside New-PSSession's parameter resolver inherits the
# LF-D7 workaround.
#
# Validated empirically via the OOP harness relocated to the roo-vault at
# myscripts/archive/harness-rc11-oop (not tracked in this repo) with 10/10 probes
# succeeding under ServerRemoteHost (MCP-identical hosting model:
# PowerShellProcessInstance + RunspaceFactory.CreateOutOfProcessRunspace).
#
# Additive form: preserves any defaults set upstream in the runspace
# (e.g. by Ps51InitializationScript) instead of replacing the table.
if (-not $PSDefaultParameterValues) { $PSDefaultParameterValues = @{} }
$PSDefaultParameterValues['Get-VM:ComputerName']       = 'localhost'
$PSDefaultParameterValues['New-PSSession:ComputerName'] = 'localhost'

# RC-11.5-diag: phase-timing instrumentation. The stopwatch is
# created at the very top of the script so EVERY Write-Information
# marker below carries a millisecond offset from script entry. The
# C# host (PowerShellHost.InvokeWithTimeoutAsync) subscribes to the
# Information stream and mirrors each '[RC11.5:T+...ms]' record into
# the server's Debug log in real time, so even when the pipeline is
# forcibly stopped at the outer 60s budget we will see the LAST
# phase that fired before the kill. Pure observability — does not
# change control flow, error handling, or cmdlet shapes.
$__rc115Sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] SCRIPT-ENTER"" -InformationAction Continue

# RC-11.2: Force-import Hyper-V module so its proxy for the
# legacy by-id call site shadows the bare Microsoft.PowerShell.Core
# cmdlet. Without this, in long-lived runspaces (e.g. Roo MCP
# server's persistent host process), the Hyper-V proxy may not be
# in scope when the cmdlet is resolved — auto-discovery falls
# through to Core's bare implementation, the Hyper-V parameter
# resolver hooks then call Get-VM internally from an empty
# context, producing
#   Microsoft.HyperV.PowerShell.VirtualizationException:
#     Value cannot be null. Parameter name: name
# (smoke probe #7 RC-11 evidence — RC103a:Discovery showed
#  NewPSSessionSource=Microsoft.PowerShell.Core, NOT Hyper-V).
# -Force ensures the proxy wins over any cached-but-incomplete
# auto-import that may have happened earlier in this runspace.
# (RC-11.3 superseded the by-id call site itself with a pipeline
#  form; the import is still required so Get-VM and the -VM
#  pipeline parameter set are bound here.)
Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] PRE-IMPORT-MODULE"" -InformationAction Continue
Import-Module Hyper-V -Force -ErrorAction Stop
Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] POST-IMPORT-MODULE"" -InformationAction Continue

if (-not (Get-Variable -Scope Global -Name '__HvMcpSessions' -ErrorAction SilentlyContinue)) {
    $global:__HvMcpSessions = @{}
}

# RC-10.3b Layer A: $cred is bound directly from C# as a PSCredential
# session variable. PS-side credential allocation has been removed to
# prevent the bundled (unsigned) Security module from being auto-loaded.

# RC10_DIAG / RC103a: pre-call discovery snapshot — capture whether the
# Hyper-V module is visible AND whether New-PSSession is resolvable. We
# emit this to the information stream (Write-Verbose visibility is
# session-dependent) only on failure, by stashing it in a script-scoped
# variable and surfacing it from the catch block.
$rc103aDiscovery = @()
try {
    $hvModules = @(Get-Module -ListAvailable -Name 'Hyper-V' -ErrorAction SilentlyContinue)
    $rc103aDiscovery += ""RC103a:Discovery HyperVModuleCount=$($hvModules.Count)""
    if ($hvModules.Count -gt 0) {
        $rc103aDiscovery += ""RC103a:Discovery HyperVModuleVersions=$([string]::Join(',', ($hvModules | ForEach-Object { $_.Version.ToString() })))""
    }
    $npsCmd = Get-Command -Name 'New-PSSession' -ErrorAction SilentlyContinue
    if ($npsCmd) {
        $rc103aDiscovery += ""RC103a:Discovery NewPSSessionSource=$($npsCmd.Source) ModuleName=$($npsCmd.ModuleName) CommandType=$($npsCmd.CommandType)""
    } else {
        $rc103aDiscovery += ""RC103a:Discovery NewPSSession=NOT_RESOLVABLE""
    }
} catch {
    $rc103aDiscovery += ""RC103a:Discovery probe failed: $($_.Exception.Message)""
}

try {
    # RC-11.1: explicit [Guid] coercion retained. PS5.1's Hyper-V cmdlets
    # demand a real [Guid] for the -Id family of parameters; passing a
    # [String] causes the internal binder to silently null-propagate,
    # producing
    #   VirtualizationException: Value cannot be null. Parameter name: name
    # (smoke probe #7 RC-11 evidence). The separate $vmIdGuid local
    # preserves the original $vmId string for the catch-block error
    # envelope below. The cast was originally introduced for the now-
    # superseded RC-11.7 New-PSSession -VMId form (replaced by RC-11.9
    # -VMName), and is still required because Get-VM -Id also takes a [Guid].
    #
    # RC-11.3: pipeline form bypasses PS5.1's poisoned parameter binder
    # cache. Build #12 differential test (out-of-Roo PASS, in-Roo FAIL
    # after vm_list invoked Get-VM) proved that PS5.1's command-table
    # cache binds Microsoft.PowerShell.Core's bare cmdlet on first use
    # WITHOUT the Hyper-V module's parameter set extension, and that
    # subsequent Import-Module Hyper-V -Force does NOT invalidate that
    # metadata cache. Routing through a by-id lookup and then the
    # pipeline uses the -VM (object) parameter set provided by the
    # Hyper-V module's pipeline binder, which is unaffected by the cache
    # poisoning of the by-id parameter set. RC-11.2 Import-Module is
    # retained so the by-id lookup cmdlet and the -VM pipeline parameter
    # set are both bound in scope.
    Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] PRE-GET-VM"" -InformationAction Continue
    [Guid] $vmIdGuid = [Guid]::Parse($vmId)
    # RC-11.4: -ComputerName localhost is the LF-D7 WMI null-name workaround.
    # In long-lived PS5.1 runspaces (the Roo MCP server), Get-VM's default-server
    # resolution sporadically returns $null, causing the Hyper-V module's internal
    # Microsoft.Virtualization.Client.Management.Server.GetServer(name=null) to
    # throw VirtualizationException 'Value cannot be null. Parameter name: name'.
    # Every Get-VM call in HyperVManager.cs, CheckpointManager.cs, and
    # PowerShellHost.cs already uses this exact workaround. RC-11.3 missed it.
    # Smoke probe #7 against Build #13 (line 78 stack trace) confirmed this is
    # the actual root cause underlying RC-11.
    $vm = Get-VM -Id $vmIdGuid -ComputerName localhost -ErrorAction Stop
    Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] POST-GET-VM"" -InformationAction Continue

    Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] PRE-NEW-PSSESSION"" -InformationAction Continue
    # RC-11.9: -VMId hits the LF-D7 null-name bug in New-PSSession's internal Get-VM
    # resolver even under STA (Build #19 proved STA alone doesn't help: the apartment
    # took effect via DIAG-APARTMENT=STA but the smoke probe still fast-failed in 29ms
    # with the byte-identical 'Get-VM : Value cannot be null. Parameter name: name' at
    # `<No file>: line 1` — the script `Get-VM -Id $args[0]` internally injected by
    # `New-PSSession -VMId`'s parameter resolver).
    # -VMName uses a different resolver that bypasses Server.GetServer(name=null).
    # Out-of-Roo harness (formerly scripts/harness-rc117-newpssession-variants.ps1;
    # removed in Phase E — recoverable from git history) proved -VMName
    # succeeds in 2,451ms vs -VMId failing instantly inside the MCP-hosted runspace.
    # We have $vm.Name from RC-11.4's Get-VM -Id $vmIdGuid -ComputerName localhost
    # call (verified: $vm.Name = 'win11-mcp-test' from Build #18's DIAG-VM-NAME marker).
    $session = New-PSSession -VMName $vm.Name -Credential $cred -Name $sessionName -ErrorAction Stop
    Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] POST-NEW-PSSESSION"" -InformationAction Continue
    $global:__HvMcpSessions[$sessionName] = $session
    $session
    Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] SCRIPT-EXIT-NORMAL"" -InformationAction Continue
}
catch {
    Write-Information ""[RC11.5:T+$($__rc115Sw.ElapsedMilliseconds)ms] SCRIPT-EXIT-CAUGHT"" -InformationAction Continue
    $errLines = @()
    $errLines += ""RC103a: New-PSSession failed for VMId=$vmId Name=$sessionName""

    # Pre-call discovery snapshot — surface whether the cmdlet was even
    # resolvable at call time so RC-10.3b triage can rule it in/out.
    foreach ($d in $rc103aDiscovery) { $errLines += $d }

    # Exception facets — Exception.Message is FREQUENTLY empty (RC-10.3
    # smoking gun); ErrorDetails.Message overrides it for display so it
    # is the FIRST place to look when the bare message is empty.
    $errLines += ""RC103a:ExceptionType=$($_.Exception.GetType().FullName)""
    $errLines += ""RC103a:Exception.Message=$($_.Exception.Message)""
    if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
        $errLines += ""RC103a:ErrorDetails.Message=$($_.ErrorDetails.Message)""
    }
    if ($_.ErrorDetails -and $_.ErrorDetails.RecommendedAction) {
        $errLines += ""RC103a:ErrorDetails.RecommendedAction=$($_.ErrorDetails.RecommendedAction)""
    }
    $errLines += ""RC103a:FullyQualifiedErrorId=$($_.FullyQualifiedErrorId)""
    $errLines += ""RC103a:CategoryInfo=$([string]$_.CategoryInfo)""
    if ($null -ne $_.TargetObject) {
        $errLines += ""RC103a:TargetObject=$([string]$_.TargetObject)""
    }
    $errLines += ""RC103a:ScriptStackTrace=$($_.ScriptStackTrace)""

    # Full inner-exception chain via Exception.ToString().
    $errLines += ""RC103a:Exception.ToString()=$($_.Exception.ToString())""

    if ($_.Exception.InnerException) {
        $errLines += ""RC103a:InnerExceptionType=$($_.Exception.InnerException.GetType().FullName)""
        $errLines += ""RC103a:InnerException.Message=$($_.Exception.InnerException.Message)""
    }

    # Stacked errors — $error[0..2] enumerated via Out-String. Multiple
    # errors may have piled up before the catch fired; enumerate the most
    # recent few so we can see the stack of contributing failures.
    if ($error.Count -gt 0) {
        $errLines += ""RC103a:errorCount=$($error.Count)""
        $errSlice = @()
        for ($i = 0; $i -lt [Math]::Min(3, $error.Count); $i++) {
            $errSlice += ""--- error[$i] ---""
            $errSlice += ($error[$i] | Out-String).TrimEnd()
        }
        $errLines += ""RC103a:errorSlice=`n"" + ($errSlice -join ""`n"")
    }

    Write-Error -Message ($errLines -join ""`n"") -ErrorAction Continue
    throw
}
";

        // RC-10.3b Layer A: build PSCredential from a SecureString in C# so
        // the PS script never has to invoke ConvertTo-SecureString. The
        // SecureString is wrapped in a try/finally and Disposed after
        // _host.InvokeAsync completes — PSCredential retains its own
        // internal SecureString reference, so the early dispose only
        // releases the local one we just built.
        var secure = new SecureString();
        foreach (var ch in password)
        {
            secure.AppendChar(ch);
        }
        secure.MakeReadOnly();
        var psCred = new PSCredential(username, secure);

        var args = new Dictionary<string, object?>
        {
            ["sessionName"] = sessionName,
            ["vmId"] = vmId,
            ["cred"] = psCred,
        };

        _logger.LogDebug(
            "SessionStore.CreateSessionInRunspaceAsync ENTER: session={SessionName} vmId={VmId} user={User}",
            sessionName, vmId, username);

        PowerShellHostResult result;
        try
        {
            result = await _host.InvokeAsync(script, args, ct).ConfigureAwait(false);
        }
        finally
        {
            // PSCredential keeps its own SecureString reference, so
            // disposing the local copy here is safe and prevents the
            // plaintext-derived buffer from lingering in memory longer
            // than necessary.
            try { secure.Dispose(); } catch { /* best-effort */ }
        }

        // DIAG-D6 (#59) + Code Review Gate 6 Blocker #2: ALL stderr written to disk
        // (the spill file) MUST be redaction-passed first. Compute the redacted
        // payload exactly once and derive both the spill content and the preview
        // substring (and reported length) from that redacted payload.
        var redactedStderr = CredentialResolver.RedactPassword(result.Stderr, password);
        var redactedPreview = redactedStderr.Substring(0, System.Math.Min(500, redactedStderr.Length));
        if (!result.Success && redactedStderr.Length > 0)
        {
            var spillSummary = StderrSpillHelper.Spill(redactedStderr);
            _logger.LogDebug(
                "SessionStore InvokeAsync returned: success={Success} stderrLen={Len} outputCount={N} spillSummary={Summary} preview={Preview}",
                result.Success, redactedStderr.Length, result.Output.Count, spillSummary, redactedPreview);
        }
        else
        {
            _logger.LogDebug(
                "SessionStore InvokeAsync returned: success={Success} stderrLen={Len} outputCount={N} spillSummary={Summary} preview={Preview}",
                result.Success, redactedStderr.Length, result.Output.Count, "(none)", redactedPreview);
        }

        if (!result.Success)
        {
            _logger.LogError(
                "SessionStore: failed to create PSSession {SessionName} (stderrLength={StderrLength}): {Error}",
                sessionName, redactedStderr.Length, redactedStderr);

            // Issue #209 (sub-finding) / VC-SO-D2: throw the typed
            // SessionOpenFailedException so ErrorMapper classifies this as
            // SESSION_FAILED (not FILE_NOT_FOUND via the path-not-found
            // substring arm that previously caught Linux PSDirect failures
            // whose stderr contained "cannot find path"). Derives from
            // InvalidOperationException to preserve backward compat with
            // SessionStoreTests.GetOrCreateAsync_NewPSSessionThrows_* /
            // _EmptyExceptionMessage_* which assert InvalidOperationException
            // (C5 backward-compat lock). The composed message preserves the
            // existing payload semantics (redactedStderr) so those tests
            // continue to find their marker substrings.
            throw new SessionOpenFailedException(
                sessionName,
                vmId,
                $"Failed to create PSSession '{sessionName}': {redactedStderr}");
        }

        _logger.LogInformation(
            "SessionStore: created session {SessionName} (vmId={VmId}, user={User})",
            sessionName, vmId, username);
    }

    /// <summary>
    /// Probes the named PSSession in the runspace with a trivial <c>1</c> invocation.
    /// Returns <c>true</c> only when the session exists, is in <c>Opened</c> state, and
    /// the probe round-trips successfully.
    /// </summary>
    private async Task<bool> IsAliveCoreAsync(string sessionName, CancellationToken ct)
    {
        // Issue #58: Defensive guard — if the runspace was recycled between init and
        // this lookup, $global:__HvMcpSessions may be $null. Re-initialize on demand
        // so the indexer below cannot raise "Cannot index into a null array", which
        // masks the real reason the retry was needed.
        const string script = @"
$ErrorActionPreference = 'Stop'
if (-not $global:__HvMcpSessions) { $global:__HvMcpSessions = @{} }
$s = $global:__HvMcpSessions[$sessionName]
if (-not $s) { throw 'no session' }
if ($s.State -ne 'Opened') { throw ""state=$($s.State)"" }
Invoke-Command -Session $s -ScriptBlock { 1 } | Out-Null
";

        try
        {
            var result = await _host.InvokeAsync(
                script,
                new Dictionary<string, object?> { ["sessionName"] = sessionName },
                ct).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SessionStore: health probe for {SessionName} threw; treating as unhealthy",
                sessionName);
            return false;
        }
    }

    /// <summary>
    /// Best-effort removal of a session from the runspace. Never throws.
    /// </summary>
    private async Task TryRemoveSessionInRunspaceAsync(string sessionName, CancellationToken ct)
    {
        // Issue #58: Defensive guard — same reasoning as in IsAliveCoreAsync. After a
        // runspace recycle the hashtable is gone; without this, the indexer would
        // throw "Cannot index into a null array" inside a best-effort cleanup path.
        const string script = @"
if (-not $global:__HvMcpSessions) { $global:__HvMcpSessions = @{} }
$s = $global:__HvMcpSessions[$sessionName]
if ($s) {
    try { Remove-PSSession -Session $s -ErrorAction SilentlyContinue } catch {}
    $global:__HvMcpSessions.Remove($sessionName) | Out-Null
}
";
        try
        {
            var result = await _host.InvokeAsync(
                script,
                new Dictionary<string, object?> { ["sessionName"] = sessionName },
                ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "SessionStore: best-effort Remove-PSSession for {SessionName} reported failure: {Stderr}",
                    sessionName, result.Stderr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SessionStore: best-effort Remove-PSSession for {SessionName} threw",
                sessionName);
        }
    }

    /// <summary>
    /// In-memory metadata for a single PSSession owned by the store.
    /// </summary>
    internal sealed class SessionEntry
    {
        public required string HostId { get; init; }
        public required string VmId { get; init; }
        public required string SessionName { get; init; }
        public DateTime CreatedUtc { get; init; }
        public DateTime LastUsedUtc { get; set; }
    }
}
