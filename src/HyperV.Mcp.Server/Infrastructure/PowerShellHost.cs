using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// In-process PowerShell host backed by <c>Microsoft.PowerShell.SDK</c>. Singleton lifetime —
/// one runspace is created on first use and reused for the remainder of the process.
/// Falls back from PowerShell 7 to Windows PowerShell 5.1 when PS7 cannot load Hyper-V
/// (known "Value cannot be null" non-interactive bug).
/// See /myplans/remoting/powershell-direct/powershell-direct-design.md — PSD-D5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency / serialization model (Issue #52, Gate 6 Fix #1):</b>
/// PowerShell SDK <see cref="Runspace"/> instances are <b>not</b> thread-safe.
/// <see cref="InvokeAsync"/> mutates runspace-global state via
/// <c>SessionStateProxy.SetVariable</c> and creates pipelines against the shared runspace.
/// To prevent concurrent calls — even ones targeting different VMs — from clobbering each
/// other's bound variables (which include credentials), every invocation acquires a
/// <b>runspace-global</b> <see cref="SemaphoreSlim"/>. The per-(hostId,vmId) gates owned by
/// <see cref="SessionStore"/> and <see cref="PowerShellDirectChannel"/> only serialize
/// same-VM access; the lock here is what keeps cross-VM calls safe.
/// </para>
/// <para>
/// <b>Known limitation:</b> this design serializes ALL guest-targeted invocations through
/// a single runspace. Throughput is therefore bounded by sequential PowerShell execution.
/// Migrating to a runspace pool is a future enhancement tracked under SM-D3 follow-up
/// work (a runspace pool would allow per-pipeline isolation and concurrent execution).
/// </para>
/// </remarks>
public class PowerShellHost : IPowerShellHost, IDisposable
{
    /// <summary>
    /// Drain the PowerShell Information stream looking for records whose payload
    /// starts with the <c>[RC11.5:</c> phase-marker prefix and mirror each one
    /// into the structured logger. Used as a safety net from the POST-Invoke
    /// paths in <see cref="InvokeWithTimeoutAsync"/> so even if the real-time
    /// DataAdded subscription missed records (e.g. queued before subscription,
    /// or fired after pipeline kill), the full phase-timing trace still reaches
    /// the server's Debug log. The filter is unchanged, but each matching record
    /// is now written to <see cref="ILogger.LogDebug(string, object?[])"/>
    /// instead of the retired <c>%TEMP%\rc103-meta.log</c> channel. Wrapped in
    /// try/catch because logging itself must NEVER throw out of a production
    /// path.
    /// </summary>
    internal void DrainInitMarkers(PowerShell ps, string phase)
    {
        if (ps is null) return;
        try
        {
            int count = ps.Streams.Information.Count;
            for (int i = 0; i < count; i++)
            {
                InformationRecord? rec = ps.Streams.Information[i];
                if (rec?.MessageData?.ToString() is string msg
                    && msg.StartsWith("[RC11.5", StringComparison.Ordinal))
                {
                    _logger.LogDebug("Init-marker drain[{Phase}][{Index}]: {Message}", phase, i, msg);
                }
            }
        }
        catch { /* never let logging itself throw */ }
    }

    /// <summary>
    /// Windows PowerShell module roots that MUST be present on <c>$env:PSModulePath</c>
    /// for either PS7 in-proc or PS5.1 OOP runspaces to resolve the Hyper-V module.
    /// <para>
    /// RC-6 (Issue #52 Phase 2 Gate 3 Loopback #3): <c>Microsoft.PowerShell.SDK</c>
    /// (PS7 in-proc) deliberately strips the System32 WindowsPowerShell modules path
    /// from <c>PSModulePath</c> when constructing an <see cref="InitialSessionState"/>
    /// via <see cref="InitialSessionState.CreateDefault2"/>. The Hyper-V module on
    /// Windows 11 lives exclusively under
    /// <c>C:\Windows\System32\WindowsPowerShell\v1.0\Modules\Hyper-V</c>, so without
    /// re-prepending these roots the module is invisible to the runspace and
    /// <c>Import-Module Hyper-V</c> fails with an opaque
    /// <c>ArgumentNullException: Parameter name: name</c>.
    /// </para>
    /// </summary>
    internal static readonly string[] WindowsPowerShellModuleRoots = new[]
    {
        // System32 path — where the Hyper-V module physically lives on every
        // supported Windows host.
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "Modules"),
        // System-wide WindowsPowerShell modules (admin-installed PS modules).
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsPowerShell", "Modules"),
    };

    private readonly ILogger<PowerShellHost> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Runspace-global serialization gate. Acquired by <see cref="InvokeAsync"/> before any
    /// <c>SessionStateProxy.SetVariable</c> / <c>PowerShell.Create()</c> work and released
    /// in <c>finally</c>. See class remarks (Gate 6 Fix #1).
    /// </summary>
    private readonly SemaphoreSlim _runspaceLock = new(1, 1);

    private Runspace? _runspace;
    private PowerShellEdition _edition;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// RC-8: per-edition init attempt details captured during the most recent
    /// probe sequence. Surfaced via <see cref="GetInitDiagnostics"/> so
    /// <c>vm_diag.phase2Host</c> shows WHICH edition failed at WHICH pipeline
    /// stage with the FULL inner-exception chain. <c>null</c> when that edition
    /// path was never entered.
    /// </summary>
    private PowerShellEditionAttempt? _ps7Attempt;
    private PowerShellEditionAttempt? _ps51Attempt;

    /// <summary>
    /// Test seam (RC-8): allow subclasses to inject simulated per-edition attempt
    /// records when overriding <see cref="ProbeAndOpenRunspaceAsync"/>. Production
    /// code paths populate these fields directly inside
    /// <see cref="TryOpenPowerShell7"/> / <c>OpenWindowsPowerShell51</c>.
    /// </summary>
    protected void SetEditionAttemptsForTesting(
        PowerShellEditionAttempt? ps7,
        PowerShellEditionAttempt? ps51)
    {
        _ps7Attempt = ps7;
        _ps51Attempt = ps51;
    }

    /// <summary>
    /// RC-9 test seam: allow subclasses to overwrite ONLY the PS7 attempt record
    /// without touching the PS5.1 record. Used by tests that drive the
    /// orchestration logic in <see cref="ProbeAndOpenRunspaceAsync"/> via the
    /// per-edition open seams (<see cref="TryOpenPowerShell7ForTesting"/> /
    /// <see cref="OpenWindowsPowerShell51ForTesting"/>) so each seam can record
    /// its own attempt independently.
    /// </summary>
    protected void SetPs7AttemptForTesting(PowerShellEditionAttempt? ps7) => _ps7Attempt = ps7;

    /// <summary>
    /// RC-9 test seam: companion to <see cref="SetPs7AttemptForTesting"/> for the
    /// PS5.1 attempt record.
    /// </summary>
    protected void SetPs51AttemptForTesting(PowerShellEditionAttempt? ps51) => _ps51Attempt = ps51;

    /// <summary>
    /// Cached initialization failure (Issue #52, Phase 2 Gate 3 RC-4).
    /// When set, <see cref="EnsureInitializedAsync"/> short-circuits without re-running the
    /// expensive PS7 + PS5.1 probe sequence (~2-6s per failed attempt). The failure is
    /// considered <b>permanent for the lifetime of the process</b> — the only way to clear
    /// it is to restart the MCP server. This is the desired fail-fast behavior so callers
    /// see a deterministic error precedence between init failures and downstream checks.
    /// </summary>
    private Exception? _initFailure;

    /// <summary>
    /// Creates a new <see cref="PowerShellHost"/>. The runspace is NOT opened here —
    /// initialization is deferred to the first call to <see cref="EnsureInitializedAsync"/>
    /// or <see cref="InvokeAsync"/>.
    /// </summary>
    public PowerShellHost(ILogger<PowerShellHost> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public PowerShellEdition Edition
    {
        get
        {
            ThrowIfDisposed();
            return _edition;
        }
    }

    /// <inheritdoc />
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_initialized)
        {
            return;
        }

        // RC-4: short-circuit on cached init failure BEFORE acquiring the lock.
        // Re-probing would waste 2-6s per call AND make error precedence non-deterministic
        // between this and the dispatcher's pre-flight checks.
        if (_initFailure is not null)
        {
            throw BuildCachedFailureException(_initFailure);
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            // Re-check inside the lock — another waiter may have just stored a failure.
            if (_initFailure is not null)
            {
                throw BuildCachedFailureException(_initFailure);
            }

            try
            {
                // Test seam (RC-4-fix-C): the probe sequence is delegated to a virtual
                // method so tests can deterministically force success or failure without
                // touching the real PowerShell SDK / Hyper-V module. Production callers
                // get the default PS7 → PS5.1 fallback implementation below.
                (Runspace runspace, PowerShellEdition edition) = await ProbeAndOpenRunspaceAsync(ct).ConfigureAwait(false);
                _runspace = runspace;
                _edition = edition;
                _initialized = true;
            }
            catch (Exception ex)
            {
                // Issue #52 Phase 2 diag: log the FULL exception chain at WARN before
                // caching, so the underlying root cause is visible in production logs
                // even if no caller ever surfaces the cached InvalidOperationException.
                LogInitFailureChain(ex);

                // RC-4: cache the failure BEFORE rethrowing so subsequent calls fail-fast.
                _initFailure = ex;
                throw;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Build the cached-failure rethrow exception. Issue #52 Phase 2 diag: surface the
    /// underlying exception's type AND message in the wrapper's <see cref="Exception.Message"/>
    /// so the cached failure is no longer opaque ("PowerShell host previously failed to
    /// initialize and the failure is cached..."). The original cached exception is still
    /// preserved as <see cref="Exception.InnerException"/> so error mapping / logging can
    /// walk the chain. Defensive credential redaction is applied to the message string
    /// even though init failures aren't expected to embed credentials.
    /// </summary>
    private static InvalidOperationException BuildCachedFailureException(Exception cached)
    {
        string detail = $"{cached.GetType().FullName}: {cached.Message}";
        string redacted = RedactCredentialsDefensively(detail);

        // RC-8.5: append the FULL inner-exception chain (via ex.ToString()) so even
        // a single error response surfaces the smoking-gun stack trace instead of
        // requiring a separate vm_diag round-trip. Defensively redacted.
        string innerChain = string.Empty;
        if (cached.InnerException is not null)
        {
            innerChain = "\nInner chain:\n" +
                RedactCredentialsDefensively(cached.InnerException.ToString());
        }

        string msg =
            $"PowerShell host previously failed to initialize: {redacted}. " +
            "The failure is cached for the process lifetime — restart the MCP server " +
            "to retry initialization." + innerChain;
        return new InvalidOperationException(msg, cached);
    }

    /// <summary>
    /// Defensively redact the configured VM password (if any) from a diagnostic string.
    /// Init-failure messages are not expected to embed credentials, but we apply this
    /// at every diagnostic boundary as defense-in-depth (Issue #20).
    /// </summary>
    private static string RedactCredentialsDefensively(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string? pw = Environment.GetEnvironmentVariable(CredentialResolver.EnvVarPassword);
        if (string.IsNullOrEmpty(pw)) return text;
        return CredentialResolver.RedactPassword(text, pw);
    }

    /// <summary>
    /// Walk the inner-exception chain and emit a WARN-level structured log line per
    /// level. Issue #52 Phase 2 diag — without this, the only surface for an init
    /// failure is the (previously generic) cached-rethrow message.
    /// </summary>
    private void LogInitFailureChain(Exception ex)
    {
        StringBuilder chain = new();
        Exception? cursor = ex;
        int depth = 0;
        while (cursor is not null && depth < 16)
        {
            if (depth > 0) chain.Append("\n  -> ");
            chain.Append(cursor.GetType().FullName).Append(": ")
                 .Append(RedactCredentialsDefensively(cursor.Message ?? string.Empty));
            cursor = cursor.InnerException;
            depth++;
        }

        _logger.LogWarning(
            ex,
            "PowerShellHost initialization failed. Exception chain: {ExceptionChain}",
            RedactCredentialsDefensively(chain.ToString()));
    }

    /// <summary>
    /// Test seam (RC-4-fix-C): performs the actual PS7 → PS5.1 probe sequence and returns
    /// an opened runspace plus the edition that succeeded. Production callers MUST use
    /// the default implementation; tests may override this method to force deterministic
    /// success/failure without spinning up the real PowerShell SDK or requiring Hyper-V
    /// to be installed. The caller (<see cref="EnsureInitializedAsync"/>) handles the
    /// failure-cache logic — overrides only need to throw on failure.
    /// </summary>
    protected virtual Task<(Runspace Runspace, PowerShellEdition Edition)> ProbeAndOpenRunspaceAsync(CancellationToken ct)
    {
        // Issue #52 Phase 2 diag — pre-probe environment snapshot. INFO so it shows in
        // production logs by default (we are flying blind without it; see RETRY #4 smoke
        // test analysis).
        string hostPsModulePath = Environment.GetEnvironmentVariable("PSModulePath") ?? "(null)";
        _logger.LogInformation(
            "PowerShellHost probe starting. dotnet host PSModulePath: {PSModulePath}",
            hostPsModulePath);
        _logger.LogInformation(
            "PowerShellHost runtime: OS={OS}, ProcessArch={Arch}, .NET={DotNet}",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture,
            Environment.Version);

        // RC-8: reset per-edition attempt records at the start of every probe.
        _ps7Attempt = null;
        _ps51Attempt = null;

        // Phase 1: try PowerShell 7 in-process (via test-overridable seam).
        Runspace? ps7Runspace = TryOpenPowerShell7ForTesting(out string? ps7Failure);
        if (ps7Runspace is not null)
        {
            // Post-open Hyper-V availability snapshot inside the actual runspace —
            // confirms what the in-proc PS7 runspace can SEE for Hyper-V, even on success.
            LogRunspaceHyperVSnapshot(ps7Runspace, "PS7");
            _logger.LogInformation("PowerShellHost initialized using in-process PowerShell 7.");
            return Task.FromResult((ps7Runspace, PowerShellEdition.PowerShell7));
        }

        _logger.LogWarning(
            "PowerShell 7 probe failed ({Reason}). Falling back to Windows PowerShell 5.1.",
            ps7Failure ?? "unknown");

        // Issue #52 Phase 2 diag — pre-fallback snapshot of what we're about to spawn.
        _logger.LogInformation(
            "PowerShell 5.1 fallback init script (will execute inside spawned powershell.exe): {InitScript}",
            Ps51InitializationScript);

        // Phase 2: fall back to Windows PowerShell 5.1 out-of-process (via seam).
        // RC-9 (Loopback #5): if OpenWindowsPowerShell51 SUCCEEDS, we ADOPT the
        // returned runspace unconditionally. The PS5.1 init script
        // (Ps51InitializationScript) already verifies Hyper-V module presence
        // via `Get-Module -ListAvailable -Name 'Hyper-V'` and `Import-Module
        // -ErrorAction Stop` BEFORE the runspace is considered open — a
        // post-open Get-VMHost gate here was redundant AND was throwing the
        // aggregate "neither PS7 nor PS5.1" failure even when ps51Attempt
        // already had Succeeded=true (Tester smoke probe #5). We keep
        // LogRunspaceHyperVSnapshot for observability but no longer gate
        // adoption on its result.
        Runspace ps51Runspace;
        try
        {
            ps51Runspace = OpenWindowsPowerShell51ForTesting();
        }
        catch (Exception ex)
        {
            // Both editions failed — build the aggregate failure exception
            // preserving the canonical "neither PS7 nor PS5.1" wording for
            // log/test consumer compatibility AND attaching the PS5.1 open
            // exception as the inner exception so post-mortem triage retains
            // the full chain.
            LogExceptionChain(ex, "Windows PowerShell 5.1 fallback open failed");

            string ps7Detail = _ps7Attempt?.ExceptionMessage
                ?? _ps7Attempt?.FullExceptionToString
                ?? ps7Failure
                ?? "unknown PS7 failure";
            throw new InvalidOperationException(
                "Failed to initialize PowerShell host: neither PowerShell 7 nor " +
                "Windows PowerShell 5.1 could load the Hyper-V module. " +
                $"PS7: {ps7Detail}. PS5.1: {ex.Message}",
                ex);
        }

        // PS5.1 OPEN SUCCEEDED — adopt it. (RC-9 fix.)
        LogRunspaceHyperVSnapshot(ps51Runspace, "PS5.1");
        _logger.LogInformation("PowerShellHost initialized using Windows PowerShell 5.1 (out-of-process).");
        return Task.FromResult((ps51Runspace, PowerShellEdition.WindowsPowerShell51));
    }

    /// <summary>
    /// RC-9 test seam: thin proxy around <see cref="TryOpenPowerShell7"/>.
    /// Production callers use the default implementation; tests override this
    /// to drive the <see cref="ProbeAndOpenRunspaceAsync"/> orchestration logic
    /// without spinning up a real PS7 in-proc runspace.
    /// </summary>
    protected virtual Runspace? TryOpenPowerShell7ForTesting(out string? failureReason)
        => TryOpenPowerShell7(out failureReason);

    /// <summary>
    /// RC-9 test seam: thin proxy around <see cref="OpenWindowsPowerShell51"/>.
    /// Production callers use the default implementation; tests override this
    /// to drive the <see cref="ProbeAndOpenRunspaceAsync"/> orchestration logic
    /// without spawning a real PS5.1 child process.
    /// </summary>
    protected virtual Runspace OpenWindowsPowerShell51ForTesting()
        => OpenWindowsPowerShell51(_logger);

    /// <summary>
    /// Issue #52 Phase 2 diag: best-effort runspace introspection — runs
    /// <c>$env:PSModulePath; Get-Module -ListAvailable Hyper-V</c> inside the supplied
    /// runspace and logs the result at INFO. Failures are swallowed and logged at DEBUG
    /// because this is observability-only and must never break init.
    /// </summary>
    private void LogRunspaceHyperVSnapshot(Runspace runspace, string editionLabel)
    {
        try
        {
            using PowerShell probe = PowerShell.Create();
            probe.Runspace = runspace;
            probe.AddScript(
                "$env:PSModulePath; " +
                "'---'; " +
                "Get-Module -ListAvailable Hyper-V | " +
                "Select-Object Name, Version, Path | ConvertTo-Json -Depth 3");
            Collection<PSObject> output = probe.Invoke();

            StringBuilder snapshot = new();
            foreach (PSObject item in output)
            {
                if (snapshot.Length > 0) snapshot.Append('\n');
                snapshot.Append(item?.BaseObject?.ToString() ?? "(null)");
            }

            _logger.LogInformation(
                "{Edition} runspace Hyper-V snapshot:\n{Snapshot}",
                editionLabel,
                snapshot.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "{Edition} runspace Hyper-V snapshot failed (non-fatal, observability only).",
                editionLabel);
        }
    }

    /// <summary>
    /// Walk the inner-exception chain and log a WARN line containing every level's
    /// type and message. Issue #52 Phase 2 diag.
    /// </summary>
    private void LogExceptionChain(Exception ex, string context)
    {
        StringBuilder chain = new();
        Exception? cursor = ex;
        int depth = 0;
        while (cursor is not null && depth < 16)
        {
            if (depth > 0) chain.Append("\n  -> ");
            chain.Append(cursor.GetType().FullName).Append(": ")
                 .Append(RedactCredentialsDefensively(cursor.Message ?? string.Empty));
            cursor = cursor.InnerException;
            depth++;
        }

        _logger.LogWarning(
            ex,
            "{Context}. Exception chain: {ExceptionChain}",
            context,
            chain.ToString());
    }

    /// <summary>
    /// RC-10.3a Layer 1: walk an inner-exception chain (starting at
    /// <paramref name="cursor"/>) and append each level to
    /// <paramref name="dest"/> on its own line, prefixed with a stable
    /// indent and the [RC103a:Inner] frame so the chain is greppable.
    /// Capped at 16 levels of nesting as a defensive cycle break.
    /// </summary>
    private static void AppendExceptionChain(StringBuilder dest, Exception? cursor, string indent)
    {
        int depth = 0;
        while (cursor is not null && depth < 16)
        {
            dest.Append('\n').Append(indent).Append("[RC103a:Inner] ")
                .Append(cursor.GetType().FullName)
                .Append(": ")
                .Append(cursor.Message ?? string.Empty);
            cursor = cursor.InnerException;
            depth++;
        }
    }

    /// <inheritdoc />
    public Task<PowerShellHostResult> InvokeAsync(
        string script,
        IDictionary<string, object?>? args = null,
        CancellationToken ct = default)
        => InvokeWithTimeoutAsync(script, args, timeoutSeconds: null, ct);

    /// <inheritdoc />
    public async Task<PowerShellHostResult> InvokeWithTimeoutAsync(
        string script,
        IDictionary<string, object?>? args,
        int? timeoutSeconds,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (script is null)
        {
            throw new ArgumentNullException(nameof(script));
        }

        _logger.LogDebug(
            "InvokeWithTimeoutAsync ENTER: scriptLen={Len} argCount={N} timeoutSec={Sec} ctCanceled={Canceled}",
            script?.Length ?? -1,
            args?.Count ?? -1,
            timeoutSeconds?.ToString() ?? "null",
            ct.IsCancellationRequested);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Post-EnsureInitialized: edition={Edition} runspaceState={State}",
            _edition,
            _runspace?.RunspaceStateInfo.State.ToString() ?? "null");

        Runspace runspace = _runspace
            ?? throw new InvalidOperationException("PowerShellHost runspace was null after initialization.");

        // Gate 6 Fix #1: runspace-global serialization. Acquired BEFORE any state mutation
        // so concurrent cross-VM callers cannot clobber each other's bound variables.
        await _runspaceLock.WaitAsync(ct).ConfigureAwait(false);

        _logger.LogDebug("runspaceLock acquired");

        // Gate 6 Fix #2: link caller token with optional per-invocation timeout. We do this
        // AFTER acquiring the runspace lock so the timeout window measures actual execution
        // time, not queue-wait.
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedCts = null;
        CancellationToken effectiveCt = ct;
        bool timeoutEnabled = timeoutSeconds is > 0;
        if (timeoutEnabled)
        {
            timeoutCts = new CancellationTokenSource();
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds!.Value));
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            effectiveCt = linkedCts.Token;
        }

        try
        {
            return await Task.Run(() =>
            {
                effectiveCt.ThrowIfCancellationRequested();

                using PowerShell ps = PowerShell.Create();
                ps.Runspace = runspace;

                // Bind args -> session variables.
                if (args is not null)
                {
                    foreach (KeyValuePair<string, object?> kvp in args)
                    {
                        runspace.SessionStateProxy.SetVariable(kvp.Key, kvp.Value);

                        // Log each variable bound (type, IsString, IsCred) so we
                        // can see whether SetVariable silently coerced to
                        // something unexpected. NEVER logs the value itself —
                        // only type metadata — preserving the secrets-never-on-
                        // disk invariant.
                        var value = kvp.Value;
                        _logger.LogDebug(
                            "SetVariable: name={Key} valueType={Type} isString={IsString} isCred={IsCred}",
                            kvp.Key,
                            value?.GetType().FullName ?? "null",
                            value is string,
                            value is System.Management.Automation.PSCredential);
                    }
                }

                ps.AddScript(script);

                // Subscribe to the Information stream so each [RC11.5:T+...ms]
                // phase marker emitted by the SessionStore CreateSession script
                // is mirrored into _logger.LogDebug in real time. The filter on
                // the [RC11.5 prefix (without the trailing colon) accepts the
                // original [RC11.5: phase markers, while still excluding
                // arbitrary Hyper-V module verbose chatter. Without this
                // real-time mirror, a 60s pipeline kill would leave us with NO
                // record of which phase was executing at kill time.
                ps.Streams.Information.DataAdded += (_, e) =>
                {
                    try
                    {
                        var rec = ps.Streams.Information[e.Index];
                        if (rec?.MessageData?.ToString() is string msg
                            && msg.StartsWith("[RC11.5", StringComparison.Ordinal))
                        {
                            _logger.LogDebug("Init-marker: {Message}", msg);
                        }
                    }
                    catch { /* never let logging itself throw */ }
                };

                using CancellationTokenRegistration reg = effectiveCt.Register(() =>
                {
                    try { ps.Stop(); } catch { /* best-effort */ }
                });

                try
                {
                    // RC-10.3a Layer 1: capture both `Streams.Error` records AND
                    // any CLR exception thrown out of `ps.Invoke()` (other than
                    // cancellation) into a single `result.Stderr` payload tagged
                    // with the [RC103a:...] frames so logs are greppable.
                    //
                    // Pre-RC-10.3a, only `PipelineStoppedException` was caught
                    // and the post-Invoke drain was skipped entirely on any
                    // other exception — leaving `result.Stderr` empty and the
                    // failure surfaced to the caller as a raw exception with
                    // no diagnostic context. That is the gap RC-10 hit when
                    // `New-PSSession -VMId` raised an `ActionPreferenceStop`-
                    // style failure: the rich `ErrorRecord` data sitting in
                    // `ps.Streams.Error` was never read.
                    Collection<PSObject> output = new();
                    Exception? invokeException = null;

                    _logger.LogDebug(
                        "PRE-Invoke: psState={PsState} runspaceState={RunspaceState} effectiveCtCanceled={Canceled}",
                        ps.InvocationStateInfo.State,
                        runspace.RunspaceStateInfo.State,
                        effectiveCt.IsCancellationRequested);

                    try
                    {
                        output = ps.Invoke();

                        _logger.LogDebug(
                            "POST-Invoke-NORMAL: psState={PsState} hadErrors={HadErrors} errorCount={ErrorCount} infoCount={InfoCount} warnCount={WarnCount} verboseCount={VerboseCount} debugCount={DebugCount} outputCount={OutputCount}",
                            ps.InvocationStateInfo.State,
                            ps.HadErrors,
                            ps.Streams.Error.Count,
                            ps.Streams.Information.Count,
                            ps.Streams.Warning.Count,
                            ps.Streams.Verbose.Count,
                            ps.Streams.Debug.Count,
                            output.Count);

                        // Promote per-warning records via _logger.LogWarning —
                        // Hyper-V module warnings often carry actionable text.
                        foreach (var w in ps.Streams.Warning)
                        {
                            _logger.LogWarning("PS warning: {Warning}", w?.ToString() ?? "<null>");
                        }

                        // Drain remaining init markers before propagation
                        // (cf. DrainInitMarkers).
                        DrainInitMarkers(ps, "POST-Invoke-NORMAL");
                    }
                    catch (PipelineStoppedException pse) when (effectiveCt.IsCancellationRequested)
                    {
                        _logger.LogDebug(
                            pse,
                            "PipelineStoppedException → cancel: message={Message}",
                            pse.Message);

                        // Drain remaining init markers before propagation
                        // (cf. DrainInitMarkers).
                        DrainInitMarkers(ps, "POST-Invoke-CAUGHT-cancel");

                        // Pipeline stopped because of our linked cancellation.
                        // Re-throw as the appropriate cancellation/timeout exception below.
                        throw new OperationCanceledException(effectiveCt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "POST-Invoke-CAUGHT: pipelineExceptionType={Type}",
                            ex.GetType().FullName);

                        // Drain remaining init markers before propagation
                        // (cf. DrainInitMarkers).
                        DrainInitMarkers(ps, "POST-Invoke-CAUGHT-generic");

                        // Any other terminating exception (e.g.
                        // ActionPreferenceStopException, RuntimeException,
                        // ParseException) — capture for stderr assembly
                        // instead of letting it escape uninterpreted.
                        invokeException = ex;
                    }

                    StringBuilder stderrBuilder = new();
                    bool first = true;

                    // RC-10.3a Layer 1: drain Streams.Error EVEN when
                    // ps.Invoke() threw. ps.Streams.Error is populated by
                    // PowerShell before terminating exceptions propagate, so
                    // it carries the rich ErrorRecord chain we need.
                    foreach (ErrorRecord errorRecord in ps.Streams.Error)
                    {
                        if (!first)
                        {
                            stderrBuilder.Append('\n');
                        }
                        stderrBuilder.Append("[RC103a:Stream] ");
                        stderrBuilder.Append(errorRecord.ToString());

                        // Append richer ErrorRecord facets that
                        // ErrorRecord.ToString() does NOT always include
                        // (FullyQualifiedErrorId, CategoryInfo, the inner
                        // Exception type chain) — these are the smoking
                        // guns RC-10.3 was missing.
                        if (!string.IsNullOrEmpty(errorRecord.FullyQualifiedErrorId))
                        {
                            stderrBuilder.Append(" | FQEID=")
                                .Append(errorRecord.FullyQualifiedErrorId);
                        }
                        if (errorRecord.CategoryInfo is not null)
                        {
                            stderrBuilder.Append(" | Category=")
                                .Append(errorRecord.CategoryInfo);
                        }
                        if (errorRecord.Exception is not null)
                        {
                            AppendExceptionChain(stderrBuilder, errorRecord.Exception, "  ");
                        }
                        first = false;
                    }

                    // RC-10.3a Layer 1: flatten the caught CLR exception
                    // chain so the ROOT cause (typically the inner-most
                    // RuntimeException / ANE) is visible in stderr.
                    if (invokeException is not null)
                    {
                        if (!first)
                        {
                            stderrBuilder.Append('\n');
                        }
                        stderrBuilder.Append("[RC103a:Exception] ")
                            .Append(invokeException.GetType().FullName)
                            .Append(": ")
                            .Append(invokeException.Message ?? string.Empty);
                        AppendExceptionChain(stderrBuilder, invokeException.InnerException, "  ");

                        // For PowerShell RuntimeException wrappers, surface
                        // the embedded ErrorRecord too — it carries the
                        // ScriptStackTrace + FullyQualifiedErrorId that the
                        // wrapping CLR exception swallows.
                        if (invokeException is IContainsErrorRecord cer && cer.ErrorRecord is not null)
                        {
                            stderrBuilder.Append("\n[RC103a:Exception.ErrorRecord] ")
                                .Append(cer.ErrorRecord.ToString());
                            if (!string.IsNullOrEmpty(cer.ErrorRecord.FullyQualifiedErrorId))
                            {
                                stderrBuilder.Append(" | FQEID=")
                                    .Append(cer.ErrorRecord.FullyQualifiedErrorId);
                            }
                            if (!string.IsNullOrEmpty(cer.ErrorRecord.ScriptStackTrace))
                            {
                                stderrBuilder.Append(" | ScriptStackTrace=")
                                    .Append(cer.ErrorRecord.ScriptStackTrace);
                            }
                        }
                    }

                    // DIAG-D6 (#59) + Code Review Gate 6 Blocker #2: ALL stderr written
                    // to disk (the spill file) must be redaction-passed first. Compute
                    // the redacted payload exactly once via
                    // StderrSpillHelper.RedactDefensively and derive BOTH the spill
                    // content and the preview substring / length from that redacted
                    // payload. The spill helper would re-run redaction internally on the
                    // raw input — passing the already-redacted text is idempotent.
                    bool postDrainSuccess = invokeException is null && !ps.HadErrors;
                    string redactedStderr = StderrSpillHelper.RedactDefensively(stderrBuilder.ToString());
                    string postDrainPreview = redactedStderr.Substring(0, Math.Min(300, redactedStderr.Length));
                    if (!postDrainSuccess && redactedStderr.Length > 0)
                    {
                        var spillSummary = StderrSpillHelper.Spill(redactedStderr);
                        _logger.LogDebug(
                            "Post-drain: stderrLen={Len} spillSummary={Summary} preview={Preview}",
                            redactedStderr.Length,
                            spillSummary,
                            postDrainPreview);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Post-drain: stderrLen={Len} spillSummary={Summary} preview={Preview}",
                            redactedStderr.Length,
                            "(none)",
                            postDrainPreview);
                    }

                    List<object?> outputList = output.Select(o => o?.BaseObject).ToList();
                    bool success = postDrainSuccess;

                    _logger.LogDebug(
                        "InvokeWithTimeoutAsync RETURN: success={Success} stderrLen={Len} outputCount={N}",
                        success,
                        stderrBuilder.Length,
                        outputList.Count);

                    return new PowerShellHostResult(
                        Success: success,
                        Output: outputList,
                        Stderr: stderrBuilder.ToString(),
                        ExitCode: success ? 0 : 1);
                }
                finally
                {
                    // Best-effort cleanup: clear bound variables so they don't leak across calls.
                    if (args is not null)
                    {
                        foreach (KeyValuePair<string, object?> kvp in args)
                        {
                            try { runspace.SessionStateProxy.SetVariable(kvp.Key, null); }
                            catch { /* swallow cleanup errors */ }
                        }
                    }
                }
            }, effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutEnabled
            && timeoutCts is not null
            && timeoutCts.IsCancellationRequested
            && !ct.IsCancellationRequested)
        {
            // Timeout fired (and not caller cancellation) — surface a TimeoutException
            // so the channel/executor can map this to COMMAND_TIMEOUT instead of a
            // generic command failure or caller-cancel envelope. (Gate 6 Fix #2)
            throw new TimeoutException(
                $"PowerShell invocation exceeded the {timeoutSeconds}s timeout.");
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
            _runspaceLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> GetVmStateAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vmId))
        {
            throw new ArgumentException("VM id must be non-empty.", nameof(vmId));
        }

        // Issue #52 Phase 2 Gate 3 RC-1: route VM-state pre-flight through the in-process
        // host instead of the legacy out-of-process PowerShellExecutor (PSD-D5/D6
        // single-facade rule for guest-targeted tools).
        //
        // NOTE: Get-VM -Id runs against the local Hyper-V host — it does NOT require a
        // per-VM PSSession. Routing it here is purely about which PS edition / runspace
        // executes the cmdlet, not about creating sessions.
        //
        // RC-1-fix-A (🔴): mirror the legacy HyperVManager.GetVmStatusAsync command shape
        // (HyperVManager.cs:556-562) — `-ComputerName localhost` is the WMI workaround
        // (LF-D7) for the null-name WMI bug on Windows 11 build 26200+; without it every
        // guest-targeted tool can fail at pre-flight on those OS builds. `-ErrorAction Stop`
        // ensures the cmdlet failure surfaces as a terminating error.
        const string script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vm = Get-VM -Id $vmId -ComputerName localhost -ErrorAction Stop; " +
            "$vm.State.ToString()";

        var args = new Dictionary<string, object?> { ["vmId"] = vmId };

        // RC-1-fix-B (🟡): use the timeout-aware overload with the same 30s budget as the
        // legacy path (HyperVManager.cs:567). A stalled Hyper-V provider call would
        // otherwise hold the runspace-global lock indefinitely. On timeout this throws
        // TimeoutException, which ErrorMapper maps to COMMAND_TIMEOUT.
        const int VmStateTimeoutSeconds = 30;

        PowerShellHostResult result;
        try
        {
            result = await InvokeWithTimeoutAsync(script, args, VmStateTimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   && ex is not TimeoutException
                                   && IsVmNotFoundError(ex.Message))
        {
            throw new VmNotFoundException(hostId, vmId);
        }

        if (!result.Success)
        {
            if (IsVmNotFoundError(result.Stderr))
            {
                throw new VmNotFoundException(hostId, vmId);
            }
            throw new InvalidOperationException(
                $"Failed to query VM state for '{vmId}' on host '{hostId}': {result.Stderr}");
        }

        if (result.Output.Count == 0 || result.Output[0] is null)
        {
            throw new VmNotFoundException(hostId, vmId);
        }

        // State may surface as enum (Microsoft.HyperV.PowerShell.VMState) or string —
        // ToString() handles both safely.
        return result.Output[0]!.ToString() ?? string.Empty;
    }

    private static bool IsVmNotFoundError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        // Get-VM emits e.g. "Hyper-V was unable to find a virtual machine with id ..." or
        // "ObjectNotFound" when the id has no match. Match on stable substrings.
        return message.Contains("unable to find", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ObjectNotFound", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Augment the supplied <c>PSModulePath</c> by prepending the Windows PowerShell
    /// module roots (see <see cref="WindowsPowerShellModuleRoots"/>) when they are
    /// missing. Deduplication is case-insensitive (Windows paths are case-insensitive).
    /// </summary>
    /// <remarks>
    /// RC-6 (Issue #52 Phase 2 Gate 3 Loopback #3). Used by both the PS7 in-proc path
    /// and the PS5.1 OOP path to guarantee the Hyper-V module is resolvable. Returns a
    /// non-empty, semicolon-separated path; returns the joined module roots when
    /// <paramref name="current"/> is null/empty.
    /// </remarks>
    /// <summary>
    /// RC-10.2: matches the Microsoft.PowerShell.SDK bin-local module subtree shape
    /// <c>...\runtimes\win\lib\net{version}\Modules[\]</c>. The SDK NuGet package
    /// auto-injects this path into the process <c>PSModulePath</c> at static init
    /// time. PS7 then resolves intrinsic modules (e.g.
    /// <c>Microsoft.PowerShell.Security</c>) from the bin path first, but
    /// <c>AuthorizationManager.PassesPolicyCheck()</c> rejects the unsigned
    /// <c>Security.types.ps1xml</c> there under the local Code Integrity / catalog
    /// signing policy — which surfaces as a <c>ConvertTo-SecureString could not be
    /// loaded</c> failure during <c>New-PSSession -Credential</c> binding.
    /// Pattern is intentionally narrow: must contain literal segments
    /// <c>\runtimes\win\lib\net</c>, then a version like <c>8.0</c> or
    /// <c>10.0-windows</c>, then <c>\Modules</c> at end-of-string (optionally
    /// trailing slash). Case-insensitive.
    /// </summary>
    private static readonly Regex SdkBinLocalModuleSubtreeRegex = new(
        @"\\runtimes\\win\\lib\\net\d+(\.\d+)?(-[a-z]+)?\\Modules\\?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// RC-10.2: returns true when <paramref name="entry"/> is the bin-local PS7 SDK
    /// module subtree shipped by <c>Microsoft.PowerShell.SDK</c> (see
    /// <see cref="SdkBinLocalModuleSubtreeRegex"/> for the exact shape).
    /// </summary>
    internal static bool IsBinLocalSdkModuleSubtree(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        return SdkBinLocalModuleSubtreeRegex.IsMatch(entry);
    }

    internal static string AugmentPsModulePath(string? current)
    {
        // Existing entries (preserve order, drop empties).
        // RC-10.2: exclude bin-local PS7 SDK module subtree (runtimes\win\lib\net*\Modules)
        // — fails AuthorizationManager catalog signing under Code Integrity policy,
        // shadows system Microsoft.PowerShell.Security.
        List<string> existing = string.IsNullOrEmpty(current)
            ? new List<string>()
            : current!.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Where(p => !IsBinLocalSdkModuleSubtree(p))
                .ToList();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = new();

        // Prepend the Windows PowerShell module roots (only those not already present).
        foreach (string root in WindowsPowerShellModuleRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (seen.Add(root))
            {
                result.Add(root);
            }
        }

        // Append the existing entries, deduplicated case-insensitively.
        foreach (string entry in existing)
        {
            if (seen.Add(entry))
            {
                result.Add(entry);
            }
        }

        return string.Join(";", result);
    }

    /// <summary>
    /// Try to open an in-process PowerShell 7 runspace and probe Hyper-V availability.
    /// Returns the opened runspace on success, or null with a failure reason on failure.
    /// </summary>
    /// <remarks>
    /// RC-2b: explicitly imports the Hyper-V module via
    /// <see cref="InitialSessionState.ImportPSModule(string[])"/> instead of relying on
    /// PS7 cmdlet autoload — which deterministically triggers the PS7 non-interactive
    /// "Value cannot be null" Hyper-V bug. If explicit import also fails, the caller
    /// falls back to PS5.1 (now working per RC-2).
    /// <para>
    /// RC-6: prior to creating the runspace, the process-level <c>PSModulePath</c> env
    /// var is augmented (via <see cref="AugmentPsModulePath"/>) so the in-proc PS7
    /// runspace inherits it. <c>Microsoft.PowerShell.SDK</c> does not expose
    /// <c>iss.EnvironmentVariables</c>; the supported mechanism is to mutate the
    /// process env BEFORE <see cref="RunspaceFactory.CreateRunspace(InitialSessionState)"/>
    /// because new runspaces snapshot the host process env into their session-scope
    /// <c>$env:</c> drive at open time.
    /// </para>
    /// </remarks>
    private Runspace? TryOpenPowerShell7(out string? failureReason)
    {
        // RC-8: per-stage instrumentation. Build up the attempt as we walk the
        // pipeline so that on failure we know EXACTLY which stage threw, AND we
        // capture the full outer + inner exception chain (with stack traces) for
        // post-mortem triage via vm_diag.
        const string EditionLabel = "PowerShell7";
        var attempt = new PowerShellEditionAttemptBuilder { Attempted = true };
        Runspace? runspace = null;
        try
        {
            // Stage 1: env mutation (RC-6 — PSModulePath augmentation).
            attempt.FailureStage = "env.SetEnvironmentVariable(PSModulePath)";
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            string? originalPsModulePath = Environment.GetEnvironmentVariable("PSModulePath");
            string augmentedPsModulePath = AugmentPsModulePath(originalPsModulePath);
            if (!string.Equals(originalPsModulePath, augmentedPsModulePath, StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("PSModulePath", augmentedPsModulePath);
                _logger.LogInformation(
                    "RC-6: PSModulePath augmented for PS7 in-proc runspace. Augmented value: {PSModulePath}",
                    augmentedPsModulePath);
            }
            else
            {
                _logger.LogInformation(
                    "RC-6: PSModulePath already contains Windows PowerShell module roots; no augmentation needed. Value: {PSModulePath}",
                    augmentedPsModulePath);
            }

            // Stage 2: InitialSessionState construction.
            attempt.FailureStage = "InitialSessionState.CreateDefault2";
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            InitialSessionState iss = InitialSessionState.CreateDefault2();
            iss.ThreadOptions = PSThreadOptions.UseCurrentThread;

            // Stage 3: ImportPSModule registration (RC-2b + RC-7 literal-name guard).
            attempt.FailureStage = "iss.ImportPSModule(Hyper-V)";
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            const string HyperVModuleName = "Hyper-V";
            if (string.IsNullOrWhiteSpace(HyperVModuleName))
            {
                throw new InvalidOperationException(
                    "RC-7: Hyper-V module name must be non-empty before ImportPSModule.");
            }
            iss.ImportPSModule(new[] { HyperVModuleName });

            // Stage 4: RunspaceFactory.
            attempt.FailureStage = "RunspaceFactory.CreateRunspace";
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            runspace = RunspaceFactory.CreateRunspace(iss);

            // Stage 5: runspace.Open().
            attempt.FailureStage = "runspace.Open";
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            runspace.Open();

            // Stage 6: post-open Hyper-V verification.
            attempt.FailureStage = "post-open.ProbeHyperV(Get-VMHost)";
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            string? probeFailure = ProbeHyperV(runspace, out Exception? probeOriginatingException);
            if (probeFailure is not null)
            {
                // RC-9 secondary fix: when the probe captured an originating exception
                // (e.g. the PS7 non-interactive ANE flowing through ErrorRecord.Exception),
                // PRESERVE it via InvalidOperationException(message, ex) and let the
                // outer catch record it via RecordException — which populates
                // InnerExceptionType / InnerExceptionStackTrace / FullExceptionToString.
                // Tester probe #5 saw all three as null because the previous code path
                // called RecordFailureMessage(probeFailure) which discarded the original.
                if (probeOriginatingException is not null)
                {
                    try { runspace.Dispose(); } catch { /* swallow */ }
                    throw new InvalidOperationException(probeFailure, probeOriginatingException);
                }

                attempt.RecordFailureMessage(probeFailure);
                _ps7Attempt = attempt.Build();
                failureReason = probeFailure;
                runspace.Dispose();
                return null;
            }

            attempt.Succeeded = true;
            attempt.FailureStage = null;
            _ps7Attempt = attempt.Build();
            failureReason = null;
            return runspace;
        }
        catch (Exception ex)
        {
            attempt.RecordException(ex, RedactCredentialsDefensively);
            _ps7Attempt = attempt.Build();
            _logger.LogError(
                ex,
                "RC-8 stage failed: {Stage} (edition={Edition}) | full chain: {FullChain}",
                attempt.FailureStage,
                EditionLabel,
                RedactCredentialsDefensively(ex.ToString()));
            failureReason = ex.Message;
            try { runspace?.Dispose(); } catch { /* swallow */ }
            return null;
        }
    }

    /// <summary>
    /// Open a Windows PowerShell 5.1 out-of-process runspace.
    /// </summary>
    /// <remarks>
    /// RC-2: the spawned <c>powershell.exe</c> child process inherits the dotnet host's
    /// <c>$env:PSModulePath</c> — which on PS7 hosts points at the PS7 module directory
    /// and does NOT include the Windows-PowerShell System32 path where the Hyper-V module
    /// actually lives. Without an explicit prefix, PS5.1 autoload cannot find Hyper-V and
    /// the probe fails with errors that look superficially like the PS7 "Value cannot be
    /// null" bug. We therefore inject an initialization script that:
    /// <list type="number">
    ///   <item><description>Prepends the System32 WindowsPowerShell modules path to <c>$env:PSModulePath</c>.</description></item>
    ///   <item><description>Imports Hyper-V explicitly with <c>-ErrorAction Stop</c> so any failure surfaces immediately
    ///     instead of being silently masked.</description></item>
    /// </list>
    /// We also log the resolved <c>$env:PSModulePath</c> so live diagnostics show what
    /// the spawned process actually saw.
    /// </remarks>
    /// <summary>
    /// PowerShell 5.1 initialization script. Runs inside the spawned <c>powershell.exe</c>
    /// child process. Exposed as a class-level constant (instead of a local in
    /// <see cref="OpenWindowsPowerShell51"/>) so Issue #52 Phase 2 diagnostic logging can
    /// surface its exact contents BEFORE the OOP runspace is opened.
    /// </summary>
    /// <remarks>
    /// RC-6 / RC-7 (Loopback #3): the script now (a) prepends BOTH Windows PowerShell
    /// module roots (System32 + Program Files), (b) verifies Hyper-V is actually
    /// discoverable on the augmented path BEFORE attempting <c>Import-Module</c> so
    /// failures surface a descriptive message instead of the opaque
    /// <c>ArgumentNullException: Parameter name: name</c> previously seen, and
    /// (c) imports the module by its literal string name (never a variable) so a
    /// null/empty value can never reach the SDK.
    /// </remarks>
    // RC-11.10: $PSDefaultParameterValues injection appended after Import-Module
    // makes -ComputerName localhost a process-wide invariant for ALL Get-VM /
    // New-PSSession invocations that subsequently run in this OOP runspace —
    // including the synthesized internal `Get-VM -Name $args` call inside
    // `New-PSSession -VMName/-VMId`'s parameter resolver that bypasses
    // RC-11.4's per-callsite -ComputerName workaround. Belt-and-suspenders
    // coverage in case any callsite forgets the SessionStore script's local
    // injection. Validated empirically via the OOP harness relocated to the
    // roo-vault at myscripts/archive/harness-rc11-oop (not tracked in this repo;
    // 10/10 probes succeeding under MCP-identical ServerRemoteHost hosting).
    // Additive form preserves any defaults already set in the runspace.
    internal const string Ps51InitializationScript =
        // Issue #58: Defensive init of $global:__HvMcpSessions. Mirror of the on-demand
        // guard added at every read site in SessionStore.cs so the invariant is
        // enforced both at runspace bring-up AND restored on demand if the runspace
        // is recycled (which previously surfaced as "Cannot index into a null array"
        // on retry, masking the actual root cause of the original failure).
        "if (-not $global:__HvMcpSessions) { $global:__HvMcpSessions = @{} }; " +
        "$env:PSModulePath = " +
        "'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\Modules;" +
        "C:\\Program Files\\WindowsPowerShell\\Modules;' + $env:PSModulePath; " +
        "if (-not (Get-Module -ListAvailable -Name 'Hyper-V')) { " +
        "    throw \"RC-7: Hyper-V module not found on PSModulePath: $env:PSModulePath\" " +
        "}; " +
        "Import-Module -Name 'Hyper-V' -ErrorAction Stop; " +
        "if (-not $PSDefaultParameterValues) { $PSDefaultParameterValues = @{} }; " +
        "$PSDefaultParameterValues['Get-VM:ComputerName']       = 'localhost'; " +
        "$PSDefaultParameterValues['New-PSSession:ComputerName'] = 'localhost'";

    private Runspace OpenWindowsPowerShell51(ILogger logger)
    {
        // RC-8: per-stage instrumentation for the PS5.1 OOP path. Stages here are
        // necessarily different from the PS7 in-proc path (process spawn + OOP
        // runspace + in-script imports).
        const string EditionLabel = "WindowsPowerShell51";
        var attempt = new PowerShellEditionAttemptBuilder { Attempted = true };
        Runspace? runspace = null;
        try
        {
            // Stage 1: env mutation (RC-6 — PSModulePath augmentation for child env).
            attempt.FailureStage = "env.SetEnvironmentVariable(PSModulePath)";
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            string? originalPsModulePath = Environment.GetEnvironmentVariable("PSModulePath");
            string augmentedPsModulePath = AugmentPsModulePath(originalPsModulePath);
            if (!string.Equals(originalPsModulePath, augmentedPsModulePath, StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("PSModulePath", augmentedPsModulePath);
                logger.LogInformation(
                    "RC-6: PSModulePath augmented for PS5.1 child process. Augmented value: {PSModulePath}",
                    augmentedPsModulePath);
            }

            // Stage 2: ScriptBlock.Create for the initialization script.
            attempt.FailureStage = "ScriptBlock.Create(initScript)";
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            const string initScript = Ps51InitializationScript;
            ScriptBlock initBlock = ScriptBlock.Create(initScript);

            // Stage 3: PowerShellProcessInstance ctor (spawns powershell.exe child).
            attempt.FailureStage = "PowerShellProcessInstance.ctor";
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            PowerShellProcessInstance processInstance = new(
                powerShellVersion: new Version(5, 1),
                credential: null,
                initializationScript: initBlock,
                useWow64: false);

            // Stage 4: RunspaceFactory.CreateOutOfProcessRunspace.
            attempt.FailureStage = "RunspaceFactory.CreateOutOfProcessRunspace";
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            runspace = RunspaceFactory.CreateOutOfProcessRunspace(
                typeTable: null,
                processInstance: processInstance);

            // RC-11.8: Hyper-V WMI proxy (Microsoft.Virtualization.Client.Management.Server.GetServer)
            // requires STA apartment. Hosted runspaces created by RunspaceFactory default to MTA,
            // which causes New-PSSession -VMId's internal Get-VM resolution to throw
            // ArgumentNullException ("name=null") under LF-D7. Stock PS5.1 console is STA;
            // matching that here closes RC-11. See harness (formerly
            // scripts/harness-rc117-newpssession-variants.ps1; removed in Phase E — recoverable
            // from git history) for the empirical proof. The properties are only settable while the runspace is in
            // BeforeOpen state, so this MUST run before runspace.Open(). Wrapped defensively in
            // try/catch because out-of-process / remote runspaces in some PowerShell SDK versions
            // throw InvalidOperationException for these setters; in that case we log and continue
            // rather than crash startup. The Open() call below is NOT inside the try/catch — its
            // failures must still surface as RC-8 stage 5.
            try
            {
                runspace.ApartmentState = System.Threading.ApartmentState.STA;
                runspace.ThreadOptions  = System.Management.Automation.Runspaces.PSThreadOptions.UseNewThread;
                logger.LogInformation("RC-11.8: PS5.1 runspace forced STA/UseNewThread");
            }
            catch (Exception staEx)
            {
                logger.LogWarning(
                    staEx,
                    "RC-11.8: failed to force STA apartment on PS5.1 runspace; continuing");
            }

            // Stage 5: runspace.Open() — also runs the initializationScript inside child.
            attempt.FailureStage = "runspace.Open";
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            runspace.Open();

            // Best-effort: log the spawned process's resolved PSModulePath for live diagnostics.
            try
            {
                using PowerShell probe = PowerShell.Create();
                probe.Runspace = runspace;
                probe.AddScript("$env:PSModulePath");
                Collection<PSObject> output = probe.Invoke();
                string resolved = output.Count > 0 ? output[0]?.BaseObject?.ToString() ?? "" : "";
                logger.LogInformation(
                    "Windows PowerShell 5.1 child process PSModulePath: {PSModulePath}",
                    resolved);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to log PSModulePath of WPS 5.1 child process (non-fatal).");
            }

            attempt.Succeeded = true;
            attempt.FailureStage = null;
            _ps51Attempt = attempt.Build();
            return runspace;
        }
        catch (Exception ex)
        {
            attempt.RecordException(ex, RedactCredentialsDefensively);
            _ps51Attempt = attempt.Build();
            logger.LogError(
                ex,
                "RC-8 stage failed: {Stage} (edition={Edition}) | full chain: {FullChain}",
                attempt.FailureStage,
                EditionLabel,
                RedactCredentialsDefensively(ex.ToString()));
            try { runspace?.Dispose(); } catch { /* swallow */ }
            throw;
        }
    }

    /// <summary>
    /// Probe Hyper-V availability by invoking <c>Get-VMHost | Select -Expand Name</c>.
    /// Returns null on success; otherwise a short failure description suitable for logging.
    /// Detects the known PS7 "Value cannot be null" Hyper-V non-interactive bug as a
    /// failure rather than a success.
    /// </summary>
    /// <remarks>
    /// RC-9 (Loopback #5) secondary fix: the originating exception (either the
    /// <see cref="ErrorRecord.Exception"/> for stream-error failures or the caught
    /// <see cref="Exception"/> for thrown failures) is surfaced via
    /// <paramref name="originatingException"/> so callers can wrap it via
    /// <see cref="InvalidOperationException(string, Exception)"/> instead of
    /// discarding it. Tester probe #5 saw <c>Ps7Attempt.InnerExceptionType=null</c>
    /// because the previous code path used <c>RecordFailureMessage(string)</c>,
    /// which never captured the underlying exception. This overload lets
    /// <see cref="TryOpenPowerShell7"/> preserve the chain.
    /// </remarks>
    private static string? ProbeHyperV(Runspace runspace, out Exception? originatingException)
    {
        originatingException = null;
        try
        {
            using PowerShell probe = PowerShell.Create();
            probe.Runspace = runspace;
            probe.AddScript("Get-VMHost | Select-Object -ExpandProperty Name");

            Collection<PSObject> _ = probe.Invoke();

            if (probe.HadErrors)
            {
                foreach (ErrorRecord error in probe.Streams.Error)
                {
                    if (MatchesValueCannotBeNullSignature(error.Exception, error))
                    {
                        originatingException = error.Exception;
                        return "PS7 Hyper-V non-interactive bug detected: 'Value cannot be null'.";
                    }
                }

                ErrorRecord? firstError = probe.Streams.Error.Count > 0 ? probe.Streams.Error[0] : null;
                originatingException = firstError?.Exception;
                return firstError?.ToString() ?? "Get-VMHost probe reported errors.";
            }

            return null;
        }
        catch (Exception ex)
        {
            originatingException = ex;
            if (MatchesValueCannotBeNullSignature(ex))
            {
                return "PS7 Hyper-V non-interactive bug detected: 'Value cannot be null'.";
            }
            return ex.Message;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> matches the known PS7 Hyper-V
    /// non-interactive "Value cannot be null" signature with HIGH specificity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RC-3: tightened matcher. The previous implementation did a case-insensitive
    /// substring match on <c>"Value cannot be null"</c> against the top-level message,
    /// which collided with any <see cref="ArgumentNullException"/> whose message happened
    /// to contain those bytes — including missing-cmdlet and unrelated parameter-binding
    /// failures. That false positive masked the real RC-2 root cause (PS5.1 fallback
    /// couldn't find the Hyper-V module) by misclassifying it as the same PS7 bug.
    /// </para>
    /// <para>
    /// New rule: BOTH must hold —
    /// <list type="number">
    ///   <item><description>The exception (or any inner exception in the chain) is an
    ///     <see cref="ArgumentNullException"/> OR a <see cref="ParameterBindingException"/>
    ///     whose <c>ParameterName == "name"</c>.</description></item>
    ///   <item><description>The exception's message <b>starts with</b> "Value cannot be null"
    ///     (not merely contains it elsewhere).</description></item>
    /// </list>
    /// When an <see cref="ErrorRecord"/> is supplied, we additionally accept the match
    /// only when the originating activity targets a Hyper-V cmdlet
    /// (<c>Get-VM*</c>, <c>Get-VMHost*</c>, <c>New-PSSession</c>) or Hyper-V module load.
    /// </para>
    /// </remarks>
    public static bool MatchesValueCannotBeNullSignature(Exception? ex)
        => MatchesValueCannotBeNullSignature(ex, errorRecord: null);

    /// <summary>
    /// Overload that accepts an optional <see cref="ErrorRecord"/> for richer activity-based
    /// discrimination (RC-3). Call sites that have an <c>ErrorRecord</c> available should
    /// prefer this overload.
    /// </summary>
    public static bool MatchesValueCannotBeNullSignature(Exception? ex, ErrorRecord? errorRecord)
    {
        if (ex is null) return false;

        // Walk the inner-exception chain looking for a typed match.
        bool typeMatch = false;
        Exception? cursor = ex;
        Exception? typedCarrier = null;
        int depth = 0;
        while (cursor is not null && depth < 16)
        {
            if (cursor is ArgumentNullException)
            {
                typeMatch = true;
                typedCarrier = cursor;
                break;
            }
            if (cursor is ParameterBindingException pbe
                && string.Equals(pbe.ParameterName, "name", StringComparison.OrdinalIgnoreCase))
            {
                typeMatch = true;
                typedCarrier = cursor;
                break;
            }
            cursor = cursor.InnerException;
            depth++;
        }

        if (!typeMatch)
        {
            return false;
        }

        // Message must START WITH "Value cannot be null" (not just contain it elsewhere).
        // We check both the typed carrier and the top-level exception so we catch wrappers.
        if (!StartsWithValueCannotBeNull(typedCarrier?.Message)
            && !StartsWithValueCannotBeNull(ex.Message))
        {
            return false;
        }

        // When we have an ErrorRecord, require that the originating activity targets a
        // Hyper-V cmdlet or Hyper-V module load. This eliminates the false-positive class
        // where an unrelated cmdlet's parameter binding produces an ArgumentNullException
        // whose message happens to start with "Value cannot be null".
        if (errorRecord is not null)
        {
            string? activity = errorRecord.CategoryInfo?.Activity;
            string? commandName = errorRecord.InvocationInfo?.MyCommand?.Name;
            if (!IsHyperVActivity(activity) && !IsHyperVActivity(commandName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWithValueCannotBeNull(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.StartsWith("Value cannot be null", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHyperVActivity(string? activity)
    {
        if (string.IsNullOrEmpty(activity)) return false;

        // Hyper-V cmdlets we care about for the PS7 autoload bug surface here.
        // Also accept Import-Module Hyper-V activity (module-load path).
        if (activity.StartsWith("Get-VM", StringComparison.OrdinalIgnoreCase)) return true;
        if (activity.Equals("New-PSSession", StringComparison.OrdinalIgnoreCase)) return true;
        if (activity.Equals("Import-Module", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PowerShellHost));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Issue #52 Phase 2 live-debug observability. Non-blocking — does NOT trigger init,
    /// does NOT acquire the init lock, and is safe to call before/after disposal. Tolerates
    /// a disposed host (returns whatever last-known state was observed) so <c>vm_diag</c>
    /// can always report something useful.
    /// </remarks>
    public PowerShellHostInitDiagnostics GetInitDiagnostics()
    {
        bool initialized = _initialized;
        Exception? failure = _initFailure;
        PowerShellEdition? edition = initialized ? _edition : null;

        string? lastError = null;
        string? lastErrorType = null;
        string? lastErrorTrace = null;

        if (failure is not null)
        {
            lastErrorType = failure.GetType().FullName;
            lastError = RedactCredentialsDefensively(
                $"{failure.GetType().FullName}: {failure.Message}");

            // RC-8.4 (CRITICAL FIX): use ex.ToString() (the full chain WITH stack
            // traces) instead of just the type:message walk. Previously
            // lastInitErrorTrace contained no actual stack data — it was
            // visually identical to lastInitError in the smoke probe output.
            lastErrorTrace = RedactCredentialsDefensively(failure.ToString());
        }

        // Resolved PSModulePath of the dotnet host process. Always available; we report
        // it regardless of init outcome because it is the single most useful field for
        // triaging "why did the runspace fail to load Hyper-V" failures.
        string? psModulePath = RedactCredentialsDefensively(
            Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty);
        if (string.IsNullOrEmpty(psModulePath)) psModulePath = null;

        return new PowerShellHostInitDiagnostics(
            Initialized: initialized,
            Edition: edition,
            LastInitError: lastError,
            LastInitErrorType: lastErrorType,
            LastInitErrorTrace: lastErrorTrace,
            PsModulePath: psModulePath,
            Ps7Attempt: _ps7Attempt,
            Ps51Attempt: _ps51Attempt);
    }

    /// <summary>
    /// Dispose the underlying runspace and release the init lock.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _initLock.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            try { _runspace?.Dispose(); } catch { /* swallow */ }
            _runspace = null;
            _disposed = true;
        }
        finally
        {
            _initLock.Release();
            _initLock.Dispose();
            try { _runspaceLock.Dispose(); } catch { /* swallow */ }
        }
    }
}
