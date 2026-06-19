using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Remoting;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Default implementation of <see cref="IPowerShellDirectChannel"/>.
/// <para>
/// Owns per-(hostId,vmId) <see cref="SemaphoreSlim"/> serialization (PSD-D7), evict-and-retry-once
/// on broken-session signals, and centralized credential redaction (PSD-D8). Every operation
/// flows through <see cref="IPowerShellHost.InvokeAsync"/> — the channel never holds a
/// <c>Runspace</c> directly.
/// </para>
/// See /myplans/remoting/powershell-direct/powershell-direct-design.md — PSD-D6.
/// </summary>
public sealed class PowerShellDirectChannel : IPowerShellDirectChannel
{
    private readonly IPowerShellHost _host;
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<PowerShellDirectChannel> _logger;

    // TODO(SM-D3 follow-up): semaphore eviction synchronized with sweeper.
    // Per-(hostId,vmId) entries are added on first access and never removed —
    // an acceptable leak in current design (one small SemaphoreSlim per VM ever
    // touched). The deferred SM-D3 idle sweeper will own this cleanup.
    // (Issue #52, Gate 6 Fix #5.)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vmLocks = new();

    private static string LockKey(string hostId, string vmId) => $"{hostId}::{vmId}";

    private SemaphoreSlim GetVmLock(string hostId, string vmId)
        => _vmLocks.GetOrAdd(LockKey(hostId, vmId), _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Creates a new <see cref="PowerShellDirectChannel"/>.
    /// </summary>
    /// <param name="host">In-process PowerShell host used for every invocation.</param>
    /// <param name="sessionStore">Persistent PSSession store keyed by (hostId, vmId).</param>
    /// <param name="logger">Logger; messages must never include passwords or PII.</param>
    public PowerShellDirectChannel(
        IPowerShellHost host,
        ISessionStore sessionStore,
        ILogger<PowerShellDirectChannel> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<PowerShellHostResult> InvokeScriptAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string script,
        IDictionary<string, object?>? args = null,
        CancellationToken ct = default)
        => InvokeScriptWithTimeoutAsync(
            hostId, vmId, username, password, script, args, timeoutSeconds: 0, ct);

    /// <inheritdoc />
    public Task<PowerShellHostResult> InvokeScriptWithTimeoutAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string script,
        IDictionary<string, object?>? args,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        // Defensive: ensure the session hashtable exists before indexing. If the runspace
        // was recycled (issue #58) the global may be unset; surface a clear error rather
        // than letting the indexer throw "Cannot index into a null array". The retry/evict
        // path will then re-create the session on the next attempt.
        const string wrapper = @"
if (-not $global:__HvMcpSessions) { $global:__HvMcpSessions = @{} }
$s = $global:__HvMcpSessions[$__sessionName]
if (-not $s) { throw ""no session for $__sessionName"" }
$argList = @($__userArgs)
Invoke-Command -Session $s -ScriptBlock ([ScriptBlock]::Create($__userScript)) -ArgumentList $argList -ErrorAction Stop
";

        var argValues = MaterializeArgValues(args);

        return ExecuteWithRetryAsync(
            hostId, vmId, username, password,
            handle =>
            {
                var bound = new Dictionary<string, object?>
                {
                    ["__sessionName"] = handle.SessionName,
                    ["__userScript"] = script,
                    ["__userArgs"] = argValues,
                };
                return timeoutSeconds > 0
                    ? _host.InvokeWithTimeoutAsync(wrapper, bound, timeoutSeconds, ct)
                    : _host.InvokeAsync(wrapper, bound, ct);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<PowerShellHostResult> CopyToSessionAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string localSourcePath,
        string guestDestinationPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(localSourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestDestinationPath);

        // Defensive hashtable guard (issue #58 runspace-recycle safety) +
        // -LiteralPath on the source so paths containing wildcard metacharacters
        // ('[', ']', '*', '?') are treated as exact paths rather than patterns.
        const string wrapper = @"
if (-not $global:__HvMcpSessions) { $global:__HvMcpSessions = @{} }
$s = $global:__HvMcpSessions[$__sessionName]
if (-not $s) { throw ""no session for $__sessionName"" }
Copy-Item -LiteralPath $__src -Destination $__dst -ToSession $s -Force -ErrorAction Stop
";

        return ExecuteWithRetryAsync(
            hostId, vmId, username, password,
            handle =>
            {
                var bound = new Dictionary<string, object?>
                {
                    ["__sessionName"] = handle.SessionName,
                    ["__src"] = localSourcePath,
                    ["__dst"] = guestDestinationPath,
                };
                return _host.InvokeAsync(wrapper, bound, ct);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<PowerShellHostResult> CopyFromSessionAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string guestSourcePath,
        string localDestinationPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestSourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDestinationPath);

        // Defensive hashtable guard (issue #58 runspace-recycle safety).
        // Note: the source path is on the guest (remote session); -LiteralPath semantics
        // for the remote side are handled by the remote PowerShell. We keep -Path here
        // because Copy-Item -FromSession resolves $__src in the guest runspace.
        const string wrapper = @"
if (-not $global:__HvMcpSessions) { $global:__HvMcpSessions = @{} }
$s = $global:__HvMcpSessions[$__sessionName]
if (-not $s) { throw ""no session for $__sessionName"" }
Copy-Item -Path $__src -Destination $__dst -FromSession $s -Force -ErrorAction Stop
";

        return ExecuteWithRetryAsync(
            hostId, vmId, username, password,
            handle =>
            {
                var bound = new Dictionary<string, object?>
                {
                    ["__sessionName"] = handle.SessionName,
                    ["__src"] = guestSourcePath,
                    ["__dst"] = localDestinationPath,
                };
                return _host.InvokeAsync(wrapper, bound, ct);
            },
            ct);
    }

    /// <inheritdoc />
    public async Task EvictSessionAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);

        var gate = GetVmLock(hostId, vmId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogDebug(
                "PowerShellDirectChannel: evicting session for {HostId}/{VmId}",
                hostId, vmId);
            await _sessionStore.EvictAsync(hostId, vmId, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Acquires the per-VM lock, gets/creates a session, runs the operation, and on a
    /// broken-session signal — whether surfaced as a failed
    /// <see cref="PowerShellHostResult"/> or as a thrown
    /// <see cref="PSRemotingTransportException"/> / <see cref="PSRemotingDataStructureException"/>
    /// / broken-session <see cref="RuntimeException"/> — evicts and retries exactly once.
    /// (Issue #52, Gate 6 Fix #3.)
    /// <para>
    /// Outer message is redacted via <see cref="CredentialResolver.RedactPassword"/> before
    /// wrapping. The original inner exception is preserved AS-IS (concrete type intact) so
    /// <c>ErrorMapper</c> can perform type-driven classification. The inner's
    /// <c>Message</c> may still contain credentials — that is acceptable because
    /// <c>ErrorMapper.MapException</c> replaces the recursed error text with the wrapper's
    /// redacted top-level message before surfacing to MCP. Downstream loggers must NOT pass
    /// the wrapper directly to <c>LogError(ex, ...)</c> / <c>LogWarning(ex, ...)</c> — log
    /// only <c>ex.GetType().Name</c> + <c>ex.Message</c> (the redacted top-level).
    /// (Issue #52, Gate 6 Fix #4.)
    /// </para>
    /// </summary>
    private async Task<PowerShellHostResult> ExecuteWithRetryAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        Func<SessionHandle, Task<PowerShellHostResult>> runOperation,
        CancellationToken ct)
    {
        var gate = GetVmLock(hostId, vmId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                var handle = await _sessionStore
                    .GetOrCreateAsync(hostId, vmId, username, password, ct)
                    .ConfigureAwait(false);

                PowerShellHostResult result;
                try
                {
                    result = await runOperation(handle).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsBrokenSessionException(ex))
                {
                    // First-attempt broken-session exception: evict and retry exactly once.
                    _logger.LogInformation(
                        "PowerShellDirectChannel: detected broken-session exception ({ExType}) for {HostId}/{VmId}, evicting and retrying once",
                        ex.GetType().Name, hostId, vmId);
                    await _sessionStore.EvictAsync(hostId, vmId, ct).ConfigureAwait(false);
                    var retryHandle = await _sessionStore
                        .GetOrCreateAsync(hostId, vmId, username, password, ct)
                        .ConfigureAwait(false);
                    // NOTE: any exception from the retry is intentionally NOT caught here —
                    // it surfaces directly via the outer redaction wrapper below.
                    result = await runOperation(retryHandle).ConfigureAwait(false);
                }

                if (IsBrokenSessionFailure(result))
                {
                    _logger.LogInformation(
                        "PowerShellDirectChannel: detected broken session for {HostId}/{VmId}, evicting and retrying once",
                        hostId, vmId);
                    await _sessionStore.EvictAsync(hostId, vmId, ct).ConfigureAwait(false);
                    var retryHandle = await _sessionStore
                        .GetOrCreateAsync(hostId, vmId, username, password, ct)
                        .ConfigureAwait(false);
                    result = await runOperation(retryHandle).ConfigureAwait(false);
                }

                return Redact(result, password);
            }
            // ── Gate 6 Fix #4: outer credential-redaction guard ─────────────────
            // Cancellation / timeout exceptions carry no credentials by definition —
            // let them through unmodified so callers can map them to the correct
            // error code (CANCELLED / COMMAND_TIMEOUT).
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException) { throw; }
            catch (Exception ex)
            {
                // Strategy choice (Issue #52, Gate 6 re-verification fix): wrap in a
                // dedicated PowerShellDirectChannelException whose top-level Message is
                // credential-redacted (this is the message ErrorMapper surfaces to MCP),
                // and whose InnerException is the ORIGINAL exception with its concrete
                // type preserved. Preserving the type is essential — ErrorMapper's
                // PowerShellDirectChannelException unwrap branch recurses on the inner
                // and pattern-matches on type (PSRemotingTransportException →
                // SESSION_FAILED, RuntimeException-with-auth-text → AUTH_FAILED, etc.).
                // If we wrapped the inner in another PowerShellDirectChannelException
                // (the previous behavior of RedactExceptionTree), the type would be lost
                // and the typed mapper branches would never fire in production.
                //
                // Credential safety: the inner exception's Message may contain the
                // password verbatim, but ErrorMapper's unwrap branch overwrites the
                // recursed Error with this wrapper's already-redacted top-level Message
                // (see ErrorMapper.MapException PowerShellDirectChannelException branch),
                // so the credential never escapes via MCP. The inner is retained only
                // for type-driven classification and for stack-trace fidelity in logs
                // (which run server-side and are out of MCP scope).
                throw new PowerShellDirectChannelException(
                    RedactMessage(ex.Message, password),
                    ex);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Heuristic detection of WinRM/PSRemoting broken-session failures based on stderr text.
    /// </summary>
    private static bool IsBrokenSessionFailure(PowerShellHostResult result)
    {
        if (result.Success) return false;
        var stderr = result.Stderr ?? string.Empty;
        return ContainsBrokenSessionSignature(stderr);
    }

    /// <summary>
    /// True when <paramref name="ex"/> represents a broken-session transport failure
    /// raised by the PowerShell SDK, mirroring the stderr-based signals in
    /// <see cref="IsBrokenSessionFailure"/>. Narrowly scoped — does NOT match arbitrary
    /// <see cref="Exception"/> types. (Issue #52, Gate 6 Fix #3.)
    /// </summary>
    internal static bool IsBrokenSessionException(Exception ex)
    {
        switch (ex)
        {
            case PSRemotingTransportException:
            case PSRemotingDataStructureException:
                return true;
            case RuntimeException re when ContainsBrokenSessionSignature(re.Message):
                return true;
            default:
                // Walk inner-exception chain for wrapped transport exceptions.
                var inner = ex.InnerException;
                while (inner is not null)
                {
                    if (inner is PSRemotingTransportException or PSRemotingDataStructureException)
                        return true;
                    if (inner is RuntimeException ire && ContainsBrokenSessionSignature(ire.Message))
                        return true;
                    inner = inner.InnerException;
                }
                return false;
        }
    }

    private static bool ContainsBrokenSessionSignature(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("session is broken", StringComparison.OrdinalIgnoreCase)
            || text.Contains("PSRemotingTransportException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("The session state is Broken", StringComparison.OrdinalIgnoreCase)
            || text.Contains("session has been disconnected", StringComparison.OrdinalIgnoreCase)
            || text.Contains("session is not in the Opened state", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a copy of <paramref name="r"/> with any literal occurrence of
    /// <paramref name="password"/> in <c>Stderr</c> replaced by <c>***REDACTED***</c>.
    /// </summary>
    private static PowerShellHostResult Redact(PowerShellHostResult r, string password)
        => r with { Stderr = CredentialResolver.RedactPassword(r.Stderr ?? string.Empty, password) };

    private static string RedactMessage(string? message, string password)
        => CredentialResolver.RedactPassword(message ?? string.Empty, password);

    /// <summary>
    /// Materializes <paramref name="args"/> values in insertion order as an <c>object?[]</c>,
    /// or an empty array when null/empty. The remote script is responsible for declaring a
    /// matching <c>param(...)</c> block.
    /// </summary>
    private static object?[] MaterializeArgValues(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return Array.Empty<object?>();
        var values = new object?[args.Count];
        var i = 0;
        foreach (var kvp in args)
        {
            values[i++] = kvp.Value;
        }
        return values;
    }
}

/// <summary>
/// Exception thrown by <see cref="PowerShellDirectChannel"/> when an underlying
/// invocation fails with a non-cancellation, non-timeout exception. The message is
/// always credential-redacted before the exception is thrown. See PSD-D8 / Gate 6 Fix #4.
/// </summary>
public sealed class PowerShellDirectChannelException : Exception
{
    public PowerShellDirectChannelException(string message)
        : base(message) { }

    public PowerShellDirectChannelException(string message, Exception? innerException)
        : base(message, innerException) { }
}
