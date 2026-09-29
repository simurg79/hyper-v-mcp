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

/// <summary>Singleton SDK host: lazily reuses one runspace, falling back to PS5.1 when PS7 cannot load Hyper-V ("Value cannot be null").
/// See internal documentation — PSD-D5.
/// Runspaces are not thread-safe: a runspace-global semaphore protects bound variables, including credentials, across VMs;
/// SessionStore and PowerShellDirectChannel gates protect only same-VM access (Issue #52, Gate 6 Fix #1).
/// All guest invocations are sequential; SM-D3 follow-up tracks a pool for per-pipeline isolation and concurrency.</summary>
public class PowerShellHost : IPowerShellHost, IDisposable
{
    /// <summary>Post-Invoke safety net for <c>[RC11.5:</c> markers missed by DataAdded before subscription or after pipeline kill.
    /// Mirrors them to the structured Debug log so the full phase trace survives.
    /// Logging MUST NOT throw into production paths.</summary>
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

    /// <summary>RC-6: CreateDefault2 strips the System32 WindowsPowerShell module path, hiding Windows 11's Hyper-V module
    /// at <c>C:\Windows\System32\WindowsPowerShell\v1.0\Modules\Hyper-V</c>.
    /// Both editions need these roots restored to avoid <c>ArgumentNullException: Parameter name: name</c> on import.</summary>
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
    private readonly StartupInitialization? _startup;
    private readonly ShutdownDeadline _shutdownDeadline;
    private readonly object _childOwnershipLock = new();
    private PowerShellProcessInstance? _childInstance;
    private bool _shutdownRequested;

    /// <summary>Gate 6 Fix #1: serialize before variable binding or pipeline creation to protect cross-VM state; release in finally.</summary>
    private readonly SemaphoreSlim _runspaceLock = new(1, 1);

    private Runspace? _runspace;
    private PowerShellEdition _edition;
    private bool _initialized;
    private bool _disposed;

    /// <summary>RC-8: retain the latest per-edition stage and full exception chain for vm_diag failure triage; null if not attempted.</summary>
    private PowerShellEditionAttempt? _ps7Attempt;
    private PowerShellEditionAttempt? _ps51Attempt;

    /// <summary>RC-8: probe overrides inject simulated attempt records; production edition-open paths populate them directly.</summary>
    protected void SetEditionAttemptsForTesting(
        PowerShellEditionAttempt? ps7,
        PowerShellEditionAttempt? ps51)
    {
        _ps7Attempt = ps7;
        _ps51Attempt = ps51;
    }

    /// <summary>RC-9: let per-edition test seams record PS7 attempts without overwriting PS5.1 during probe orchestration.</summary>
    protected void SetPs7AttemptForTesting(PowerShellEditionAttempt? ps7) => _ps7Attempt = ps7;

    /// <summary>RC-9: PS5.1 companion to <see cref="SetPs7AttemptForTesting"/>.</summary>
    protected void SetPs51AttemptForTesting(PowerShellEditionAttempt? ps51) => _ps51Attempt = ps51;

    /// <summary>RC-4: cache failures until restart to avoid repeated ~2-6s probes and preserve deterministic error precedence.</summary>
    private Exception? _initFailure;

    /// <summary>Defer runspace opening until the first EnsureInitializedAsync or InvokeAsync call.</summary>
    public PowerShellHost(ILogger<PowerShellHost> logger)
        : this(logger, null, new ShutdownDeadline()) { }

    public PowerShellHost(ILogger<PowerShellHost> logger, StartupInitialization? startup, ShutdownDeadline shutdownDeadline)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startup = startup;
        _shutdownDeadline = shutdownDeadline;
        if (startup is not null) startup.ReadChildIdentity = GetChildIdentity;
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
        if (_startup is not null)
        {
            _startup.ThrowIfUnavailable();
            if (!_startup.IsWorkerThread)
            {
                await _startup.Completion.WaitAsync(ct).ConfigureAwait(false);
                _startup.ThrowIfUnavailable();
            }
        }

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
                // RC-4-fix-C: virtual probing lets tests force outcomes without PowerShell/Hyper-V; production keeps PS7 → PS5.1.
                (Runspace runspace, PowerShellEdition edition) = await ProbeAndOpenRunspaceAsync(ct).ConfigureAwait(false);
                _runspace = runspace;
                _startup?.ThrowIfUnavailable();
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

    /// <summary>Expose the cached cause's type and message, with defensive credential redaction even for init failures.
    /// Preserve InnerException so error mapping and logging can walk the chain.</summary>
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

    /// <summary>Issue #20: redact the configured VM password at every diagnostic boundary, even for unlikely init leaks.</summary>
    private static string RedactCredentialsDefensively(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string? pw = Environment.GetEnvironmentVariable(CredentialResolver.EnvVarPassword);
        if (string.IsNullOrEmpty(pw)) return text;
        return CredentialResolver.RedactPassword(text, pw);
    }

    /// <summary>Log each inner-exception level at WARN so init failures are visible beyond cached rethrows (Issue #52).</summary>
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

    /// <summary>RC-4-fix-C: production MUST use the PS7 → PS5.1 probe; tests may override it to avoid PowerShell/Hyper-V.
    /// Return the opened runspace and edition, or throw; EnsureInitializedAsync owns failure caching.</summary>
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
        _startup?.SetStage("PS7 probe", "PowerShell7");
        _startup?.ThrowIfUnavailable();
        Runspace? ps7Runspace = TryOpenPowerShell7ForTesting(out string? ps7Failure);
        _startup?.ThrowIfUnavailable();
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

        // RC-9: PS5.1 initialization already verifies and imports Hyper-V with -ErrorAction Stop.
        // Adopt a successful open unconditionally; gating on the diagnostic Get-VMHost probe can reject a usable runspace.
        Runspace ps51Runspace;
        try
        {
            _startup?.SetStage("PS5.1 probe", "WindowsPowerShell51");
            _startup?.ThrowIfUnavailable();
            ps51Runspace = OpenWindowsPowerShell51ForTesting();
            _startup?.ThrowIfUnavailable();
        }
        catch (Exception ex)
        {
            // Preserve "neither PS7 nor PS5.1" for log/test compatibility and the PS5.1 inner exception for triage.
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

        LogRunspaceHyperVSnapshot(ps51Runspace, "PS5.1");
        _logger.LogInformation("PowerShellHost initialized using Windows PowerShell 5.1 (out-of-process).");
        return Task.FromResult((ps51Runspace, PowerShellEdition.WindowsPowerShell51));
    }

    /// <summary>RC-9: tests override PS7 opening to exercise orchestration without a real runspace; production uses the default.</summary>
    internal bool ForceWindowsPowerShell51ForTesting { get; init; }

    protected virtual Runspace? TryOpenPowerShell7ForTesting(out string? failureReason)
    {
        if (!ForceWindowsPowerShell51ForTesting) return TryOpenPowerShell7(out failureReason);
        failureReason = "PS7 skipped by test-only edition selection";
        return null;
    }

    /// <summary>RC-9: tests override PS5.1 opening to exercise orchestration without a real child; production uses the default.</summary>
    protected virtual Runspace OpenWindowsPowerShell51ForTesting()
        => OpenWindowsPowerShell51(_logger);

    /// <summary>Issue #52: log the runspace's PSModulePath and available Hyper-V modules at INFO.
    /// Introspection must not break init; swallow failures and log them at DEBUG.</summary>
    private void LogRunspaceHyperVSnapshot(Runspace runspace, string editionLabel)
    {
        _startup?.ThrowIfUnavailable();
        _startup?.SetStage("runspace Hyper-V snapshot", editionLabel);
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

    /// <summary>Issue #52: retain every exception level's type and message in WARN diagnostics.</summary>
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

    /// <summary>RC-10.3a: stable indentation and [RC103a:Inner] frames make each exception level searchable.
    /// Cap the chain at 16 levels to break cycles.</summary>
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

    /// <summary>Exclude ScriptStackTrace, positional ErrorRecord.ToString() and Exception.ToString() from wire facets to avoid script disclosure.
    /// See internal documentation — SOE-D10.</summary>
    private static void AppendWireFacets(
        StringBuilder dest,
        ref bool isFirst,
        string? exceptionTypeName,
        string? message,
        string? fullyQualifiedErrorId,
        string? category,
        Exception? innerException)
    {
        if (!isFirst)
        {
            dest.Append('\n');
        }
        isFirst = false;

        dest.Append(exceptionTypeName ?? "(unknown)")
            .Append(": ")
            .Append(message ?? string.Empty);
        if (!string.IsNullOrEmpty(fullyQualifiedErrorId))
        {
            dest.Append(" | FQEID=").Append(fullyQualifiedErrorId);
        }
        if (!string.IsNullOrEmpty(category))
        {
            dest.Append(" | Category=").Append(category);
        }

        Exception? cursor = innerException;
        int depth = 0;
        while (cursor is not null && depth < 16)
        {
            dest.Append("\n  inner: ")
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

                        // Type/IsString/IsCred reveal SetVariable coercion; NEVER log values or put secrets on disk.
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

                // Mirror SessionStore CreateSession markers to Debug in real time so a 60s kill retains the last phase.
                // The [RC11.5 prefix accepts [RC11.5:T+...ms] markers but excludes unrelated Hyper-V chatter.
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
                    // RC-10.3a: combine Streams.Error and non-cancellation CLR exceptions in searchable [RC103a:...] stderr.
                    // Terminating errors can leave rich ErrorRecord data that would be lost if the drain were skipped.
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

                        // Preserve other terminating exceptions for stderr assembly rather than let them escape uninterpreted.
                        invokeException = ex;
                    }

                    StringBuilder stderrBuilder = new();

                    // CLR rendering adds ScriptStackTrace, positional ErrorRecord text and Exception.ToString() frames after the script returns;
                    // script edits alone cannot prevent wire disclosure. See
                    // internal documentation — SOE-D10.
                    StringBuilder wireStderrBuilder = new();
                    bool first = true;
                    bool wireFirst = true;

                    // RC-10.3a: PowerShell populates Streams.Error before throwing; drain it even after Invoke fails.
                    foreach (ErrorRecord errorRecord in ps.Streams.Error)
                    {
                        if (!first)
                        {
                            stderrBuilder.Append('\n');
                        }
                        stderrBuilder.Append("[RC103a:Stream] ");
                        stderrBuilder.Append(errorRecord.ToString());

                        AppendWireFacets(
                            wireStderrBuilder,
                            ref wireFirst,
                            errorRecord.Exception?.GetType().FullName,
                            errorRecord.Exception?.Message ?? errorRecord.ErrorDetails?.Message,
                            errorRecord.FullyQualifiedErrorId,
                            errorRecord.CategoryInfo?.ToString(),
                            errorRecord.Exception?.InnerException);

                        // RC-10.3: ToString() can omit FullyQualifiedErrorId, CategoryInfo and inner exception types needed for triage.
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

                        AppendWireFacets(
                            wireStderrBuilder,
                            ref wireFirst,
                            invokeException.GetType().FullName,
                            invokeException.Message,
                            (invokeException as IContainsErrorRecord)?.ErrorRecord?.FullyQualifiedErrorId,
                            (invokeException as IContainsErrorRecord)?.ErrorRecord?.CategoryInfo?.ToString(),
                            invokeException.InnerException);

                        // RuntimeException hides the embedded ErrorRecord's ScriptStackTrace and FullyQualifiedErrorId.
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

                    // DIAG-D6 (#59): redact stderr before disk writes; derive spill, preview and length from one redacted payload.
                    // Passing it through the spill helper's own redaction is idempotent.
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
                        ExitCode: success ? 0 : 1,
                        WireStderr: wireStderrBuilder.ToString());
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

        // RC-1 / PSD-D5/D6: the single guest-tool facade chooses the edition/runspace; local Get-VM needs no per-VM PSSession.
        // RC-1-fix-A / LF-D7: match HyperVManager.cs:556-562's -ComputerName localhost to avoid Windows 11 26200+ null-name WMI failures.
        // -ErrorAction Stop makes cmdlet failures terminating.
        const string script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vm = Get-VM -Id $vmId -ComputerName localhost -ErrorAction Stop; " +
            "$vm.State.ToString()";

        var args = new Dictionary<string, object?> { ["vmId"] = vmId };

        // RC-1-fix-B: the legacy 30s budget (HyperVManager.cs:567) prevents a stalled provider from holding the global lock indefinitely.
        // TimeoutException maps to COMMAND_TIMEOUT.
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

    /// <summary>RC-6: both editions need missing Windows module roots prepended and case-insensitively deduplicated; null/empty input yields the joined roots, always a non-empty semicolon-separated path.
    /// RC-10.2: the SDK injects bin-local modules at static init, shadowing system Microsoft.PowerShell.Security. Code Integrity rejects its unsigned Security.types.ps1xml, causing ConvertTo-SecureString load failure during New-PSSession credential binding.
    /// Match only case-insensitive \runtimes\win\lib\net{version}\Modules suffixes, optionally trailing slash (e.g. 8.0, 10.0-windows).</summary>
    private static readonly Regex SdkBinLocalModuleSubtreeRegex = new(
        @"\\runtimes\\win\\lib\\net\d+(\.\d+)?(-[a-z]+)?\\Modules\\?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>RC-10.2: identify the SDK subtree by the narrow <see cref="SdkBinLocalModuleSubtreeRegex"/> shape.</summary>
    internal static bool IsBinLocalSdkModuleSubtree(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        return SdkBinLocalModuleSubtreeRegex.IsMatch(entry);
    }

    internal static string AugmentPsModulePath(string? current)
    {
        // RC-10.2: exclude the SDK subtree that fails catalog signing and shadows system Microsoft.PowerShell.Security.
        List<string> existing = string.IsNullOrEmpty(current)
            ? new List<string>()
            : current!.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Where(p => !IsBinLocalSdkModuleSubtree(p))
                .ToList();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = new();


        foreach (string root in WindowsPowerShellModuleRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (seen.Add(root))
            {
                result.Add(root);
            }
        }


        foreach (string entry in existing)
        {
            if (seen.Add(entry))
            {
                result.Add(entry);
            }
        }

        return string.Join(";", result);
    }

    /// <summary>RC-2b: explicit Hyper-V import avoids PS7 autoload's non-interactive "Value cannot be null" bug.
    /// Return the opened runspace or null with a reason; the caller falls back to PS5.1 if explicit import fails.
    /// RC-6: augment process PSModulePath before CreateRunspace because runspaces snapshot it at open and the SDK exposes no iss.EnvironmentVariables.</summary>
    private Runspace? TryOpenPowerShell7(out string? failureReason)
    {
        // RC-8: retain the failing stage and full exception chain/stacks for vm_diag triage.
        const string EditionLabel = "PowerShell7";
        var attempt = new PowerShellEditionAttemptBuilder { Attempted = true };
        Runspace? runspace = null;
        try
        {
            // Stage 1: env mutation (RC-6 — PSModulePath augmentation).
            attempt.FailureStage = "env.SetEnvironmentVariable(PSModulePath)";
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
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
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            InitialSessionState iss = InitialSessionState.CreateDefault2();
            iss.ThreadOptions = PSThreadOptions.UseCurrentThread;

            // Stage 3: ImportPSModule registration (RC-2b + RC-7 literal-name guard).
            attempt.FailureStage = "iss.ImportPSModule(Hyper-V)";
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
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
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            runspace = RunspaceFactory.CreateRunspace(iss);

            // Stage 5: runspace.Open().
            attempt.FailureStage = "runspace.Open";
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            runspace.Open();

            // Stage 6: post-open Hyper-V verification.
            _startup?.ThrowIfUnavailable();
            attempt.FailureStage = "post-open.ProbeHyperV(Get-VMHost)";
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
            _logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            string? probeFailure = ProbeHyperV(runspace, out Exception? probeOriginatingException);
            if (probeFailure is not null)
            {
                // RC-9: preserve the originating exception so RecordException retains its type, stack and full chain.
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

    /// <summary>RC-2/RC-6/RC-7: the PS5.1 child inherits PS7 module paths; prepend System32 and Program Files roots, verify discovery, and import literal Hyper-V with -ErrorAction Stop to avoid opaque null-name errors. Log the resolved path; keep the script constant so diagnostics can show it before open.
    /// RC-11.10: additive defaults preserve existing values and enforce -ComputerName localhost for all Get-VM/New-PSSession calls, including internal VMName/VMId resolution that bypasses RC-11.4 callsite injection.
    /// The untracked local documentation myscripts/archive/harness-rc11-oop recorded 10/10 successes under MCP-identical ServerRemoteHost hosting.</summary>
    internal const string Ps51InitializationScript =
        // Issue #58: initialize __HvMcpSessions here and guard SessionStore reads after recycling to avoid null-array errors masking the cause.
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
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
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
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            const string initScript = Ps51InitializationScript;
            ScriptBlock initBlock = ScriptBlock.Create(initScript);

            // Stage 3: PowerShellProcessInstance ctor (spawns powershell.exe child).
            attempt.FailureStage = "PowerShellProcessInstance.ctor";
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            PowerShellProcessInstance processInstance = new(
                powerShellVersion: new Version(5, 1),
                credential: null,
                initializationScript: initBlock,
                useWow64: false);
            lock (_childOwnershipLock)
            {
                if (_shutdownRequested)
                    throw new InvalidOperationException("Shutdown began before the remoting child was registered.");
                _childInstance = processInstance;
            }

            // Stage 4: RunspaceFactory.CreateOutOfProcessRunspace.
            attempt.FailureStage = "RunspaceFactory.CreateOutOfProcessRunspace";
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            runspace = RunspaceFactory.CreateOutOfProcessRunspace(
                typeTable: null,
                processInstance: processInstance);

            // RC-11.8 / LF-D7: Hyper-V WMI Server.GetServer requires STA; default MTA causes New-PSSession -VMId's internal Get-VM name=null failure. Match the STA PS5.1 console.
            // Evidence: scripts/harness-rc117-newpssession-variants.ps1 in git history. Set properties before Open; setters require BeforeOpen.
            // Some SDKs reject remote setters with InvalidOperationException: log and continue, but let Open failures surface as RC-8 stage 5.
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
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
            logger.LogInformation("RC-8 stage: {Stage} (edition={Edition})", attempt.FailureStage, EditionLabel);
            lock (_childOwnershipLock)
            {
                if (_shutdownRequested)
                    throw new InvalidOperationException("Shutdown began before the remoting child open.");
            }
            OpenWindowsPowerShell51Runspace(runspace);
            _startup?.ThrowIfUnavailable();

            // Best-effort: log the spawned process's resolved PSModulePath for live diagnostics.
            attempt.FailureStage = "post-open.PSModulePath";
            _startup?.SetStage(attempt.FailureStage, EditionLabel);
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

    /// <summary>Probe with Get-VMHost | Select -Expand Name: return null on success or a short failure reason, including PS7's non-interactive null-name bug.
    /// RC-9: expose the originating stream or thrown exception so callers can wrap it without losing the chain.</summary>
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

    /// <summary>RC-3: avoid misclassifying missing-module or unrelated binding failures as the PS7 Hyper-V bug.
    /// Require an ArgumentNullException or a ParameterBindingException with ParameterName="name" in the chain, plus a "Value cannot be null" message prefix.
    /// With an ErrorRecord, also require Hyper-V activity (Get-VM*, Get-VMHost*, New-PSSession or module load).</summary>
    public static bool MatchesValueCannotBeNullSignature(Exception? ex)
        => MatchesValueCannotBeNullSignature(ex, errorRecord: null);

    /// <summary>RC-3: prefer this overload when an ErrorRecord is available to distinguish the originating activity.</summary>
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

        // Require Hyper-V activity to reject unrelated cmdlet binding errors with the same null-name message.
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
    /// <remarks>Issue #52: vm_diag must remain useful before/after disposal; return last-known state without triggering init or taking its lock.</remarks>
    public PowerShellHostInitDiagnostics GetInitDiagnostics()
    {
        var terminal = _startup?.Terminal;
        bool initialized = _initialized && (_startup is null || terminal?.State == "Ready");
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

            // RC-8.4: ToString() preserves stack traces that a type/message walk omits.
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
            Ps51Attempt: _ps51Attempt,
            StartupState: terminal?.State ?? "Warming",
            StartupDetail: terminal?.Detail,
            StartupProgress: _startup?.Progress,
            StartupElapsedSeconds: _startup?.ElapsedSeconds,
            ChildIdentity: GetChildIdentity());
    }

    protected virtual void OpenWindowsPowerShell51Runspace(Runspace runspace) => runspace.Open();

    private System.Diagnostics.Process? ReadChildProcess()
    {
        lock (_childOwnershipLock)
        {
            try
            {
                var process = _childInstance?.Process;
                if (process is not null) _ = process.Id;
                return process;
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    public PowerShellChildIdentity GetChildIdentity()
    {
        var process = ReadChildProcess();
        if (process is null)
            return new(null, null, "child not yet started");
        try { return new(process.Id, process.StartTime.ToUniversalTime(), "started"); }
        catch (InvalidOperationException) { return new(null, null, "child not yet started"); }
    }

    internal void CleanupOwnedChild()
    {
        _shutdownDeadline.Begin();
        lock (_childOwnershipLock) _shutdownRequested = true;
        try
        {
            // Registration cannot veto the dependency's later start; this re-read only narrows the approved window.
            // See internal documentation — SE-D9.
            var process = ReadChildProcess();
            if (process is null || process.HasExited) return;
            process.Kill();
            var milliseconds = (int)Math.Min(250, _shutdownDeadline.Remaining.TotalMilliseconds);
            if (milliseconds > 0) process.WaitForExit(milliseconds);
        }
        catch (Exception exception)
        {
            try { _logger.LogWarning(exception, "Owned remoting child cleanup failed; shutdown will continue."); }
            catch { }
        }
    }

    public void Dispose()
    {
        _shutdownDeadline.Begin();
        CleanupOwnedChild();
        if (_disposed) return;
        if (!_initLock.Wait(TimeSpan.Zero))
        {
            _disposed = true;
            return;
        }
        try
        {
            if (_disposed) return;
            _disposed = true;
            var runspace = _runspace;
            _runspace = null;
            if (runspace is null || _shutdownDeadline.Remaining == TimeSpan.Zero) return;
            using var finished = new ManualResetEventSlim();
            var disposal = new Thread(() =>
            {
                try { runspace.Dispose(); } catch { }
                finally { try { finished.Set(); } catch (ObjectDisposedException) { } }
            }) { IsBackground = true, Name = "PowerShell disposal" };
            disposal.Start();
            finished.Wait(_shutdownDeadline.Remaining);
        }
        finally
        {
            _initLock.Release();
        }
    }
}
