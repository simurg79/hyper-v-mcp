using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Identifies which PowerShell edition the in-process host is running.
/// See /myplans/remoting/powershell-direct/powershell-direct-design.md — PSD-D5.
/// </summary>
public enum PowerShellEdition
{
    /// <summary>PowerShell 7.x (in-process via Microsoft.PowerShell.SDK).</summary>
    PowerShell7,

    /// <summary>Windows PowerShell 5.1 (out-of-process fallback for Hyper-V module compatibility).</summary>
    WindowsPowerShell51
}

/// <summary>
/// Result of a single <see cref="IPowerShellHost.InvokeAsync"/> call.
/// </summary>
/// <param name="Success">True iff <c>PowerShell.HadErrors</c> was false after invocation.</param>
/// <param name="Output">Pipeline output objects (already unwrapped from <c>PSObject.BaseObject</c>).</param>
/// <param name="Stderr">Joined error-stream text (one <c>ErrorRecord.ToString()</c> per line, '\n' separated).</param>
/// <param name="ExitCode">
/// Synthetic exit code: <c>0</c> on success, <c>1</c> on errors, <c>null</c> when not applicable.
/// Provided to keep the result shape comparable to the legacy out-of-process executor.
/// </param>
public sealed record PowerShellHostResult(
    bool Success,
    IReadOnlyList<object?> Output,
    string Stderr,
    int? ExitCode);

/// <summary>
/// Diagnostic snapshot of <see cref="IPowerShellHost"/> initialization state.
/// Surfaced via <c>vm_diag</c> for Issue #52 Phase 2 live-debug observability —
/// pre-existing behavior is unchanged; this is read-only telemetry.
/// </summary>
/// <param name="Initialized">True when the runspace has been opened successfully.</param>
/// <param name="Edition">
/// PowerShell edition currently backing the host. <c>null</c> when not initialized.
/// </param>
/// <param name="LastInitError">
/// Message of the cached init failure (if any). Includes the underlying exception's
/// type+message. Credential-redacted defensively. <c>null</c> when no init has been
/// attempted or initialization succeeded.
/// </param>
/// <param name="LastInitErrorType">
/// Full CLR type name of the cached init failure. <c>null</c> when not applicable.
/// </param>
/// <param name="LastInitErrorTrace">
/// Newline-joined chain of <c>type: message</c> entries walked from the cached
/// failure through every <see cref="System.Exception.InnerException"/>. Credential-redacted.
/// <c>null</c> when not applicable.
/// </param>
/// <param name="PsModulePath">
/// Resolved <c>$env:PSModulePath</c> of the dotnet host process at the time of the
/// snapshot. Provided regardless of init success so it is useful for failure triage.
/// </param>
public sealed record PowerShellHostInitDiagnostics(
    bool Initialized,
    PowerShellEdition? Edition,
    string? LastInitError,
    string? LastInitErrorType,
    string? LastInitErrorTrace,
    string? PsModulePath,
    // RC-8 (Issue #52 Phase 2 Gate 3 Loopback #4): per-edition attempt details so
    // diagnostics can localize WHICH edition failed at WHICH stage with the FULL
    // inner-exception chain (the missing signal that makes the cached
    // <c>InvalidOperationException</c> opaque). Both fields default to <c>null</c>
    // for back-compat with callers that construct this record positionally.
    PowerShellEditionAttempt? Ps7Attempt = null,
    PowerShellEditionAttempt? Ps51Attempt = null);

/// <summary>
/// Diagnostic snapshot of a single PowerShell-edition initialization attempt
/// (PS7 in-proc or PS5.1 OOP). Records which pipeline stage was executing when
/// failure occurred and the FULL outer-+-inner exception payload so post-mortem
/// triage does not require re-running the probe. (Issue #52 Phase 2 RC-8.)
/// </summary>
/// <param name="Attempted">True iff the edition path was entered at all.</param>
/// <param name="Succeeded">True iff the edition path opened a usable runspace.</param>
/// <param name="FailureStage">
/// Last stage marker that was set before failure (e.g. <c>"iss.ImportPSModule"</c>,
/// <c>"RunspaceFactory.CreateRunspace"</c>, <c>"runspace.Open"</c>,
/// <c>"post-open.GetModule(Hyper-V)"</c>). <c>null</c> on success.
/// </param>
/// <param name="ExceptionType">Outermost exception's full CLR type name.</param>
/// <param name="ExceptionMessage">Outermost exception's message (credential-redacted).</param>
/// <param name="InnerExceptionType">Immediate inner exception type, if any.</param>
/// <param name="InnerExceptionMessage">Immediate inner exception message (redacted), if any.</param>
/// <param name="InnerExceptionStackTrace">
/// FULL <see cref="System.Exception.StackTrace"/> of the immediate inner exception.
/// This is the signal previously missing from <c>vm_diag</c> output.
/// </param>
/// <param name="FullExceptionToString">
/// <c>ex.ToString()</c> on the outer exception, which contains every wrapped
/// exception type, message, and stack trace concatenated by the runtime — the
/// most complete textual representation available without re-throwing.
/// </param>
public sealed record PowerShellEditionAttempt(
    bool Attempted,
    bool Succeeded,
    string? FailureStage,
    string? ExceptionType,
    string? ExceptionMessage,
    string? InnerExceptionType,
    string? InnerExceptionMessage,
    string? InnerExceptionStackTrace,
    string? FullExceptionToString);

/// <summary>
/// Mutable accumulator used by <see cref="PowerShellHost"/> to collect per-stage
/// state during a single edition probe (PS7 in-proc or PS5.1 OOP). Calls
/// <see cref="Build"/> to produce an immutable <see cref="PowerShellEditionAttempt"/>
/// snapshot suitable for inclusion in
/// <see cref="PowerShellHostInitDiagnostics"/>. (Issue #52 Phase 2 RC-8.)
/// </summary>
internal sealed class PowerShellEditionAttemptBuilder
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public string? FailureStage { get; set; }
    public string? ExceptionType { get; private set; }
    public string? ExceptionMessage { get; private set; }
    public string? InnerExceptionType { get; private set; }
    public string? InnerExceptionMessage { get; private set; }
    public string? InnerExceptionStackTrace { get; private set; }
    public string? FullExceptionToString { get; private set; }

    /// <summary>
    /// Capture the full outer + immediate-inner exception payload AND
    /// <c>ex.ToString()</c> (which contains every wrapped exception's stack trace
    /// concatenated by the runtime). The optional <paramref name="redact"/>
    /// callback is applied to every emitted string so credentials cannot leak
    /// through diagnostics.
    /// </summary>
    public void RecordException(Exception ex, Func<string, string>? redact = null)
    {
        if (ex is null) throw new ArgumentNullException(nameof(ex));
        Func<string, string> r = redact ?? (s => s);

        ExceptionType = ex.GetType().FullName;
        ExceptionMessage = r(ex.Message ?? string.Empty);

        if (ex.InnerException is not null)
        {
            Exception inner = ex.InnerException;
            InnerExceptionType = inner.GetType().FullName;
            InnerExceptionMessage = r(inner.Message ?? string.Empty);
            InnerExceptionStackTrace = r(inner.StackTrace ?? string.Empty);
        }

        FullExceptionToString = r(ex.ToString());
    }

    /// <summary>
    /// Capture a non-exception failure description (e.g. a probe that returned a
    /// failure string instead of throwing). Populates <see cref="ExceptionMessage"/>
    /// and <see cref="FullExceptionToString"/> only.
    /// </summary>
    public void RecordFailureMessage(string message)
    {
        ExceptionMessage = message;
        FullExceptionToString = message;
    }

    public PowerShellEditionAttempt Build() => new(
        Attempted: Attempted,
        Succeeded: Succeeded,
        FailureStage: FailureStage,
        ExceptionType: ExceptionType,
        ExceptionMessage: ExceptionMessage,
        InnerExceptionType: InnerExceptionType,
        InnerExceptionMessage: InnerExceptionMessage,
        InnerExceptionStackTrace: InnerExceptionStackTrace,
        FullExceptionToString: FullExceptionToString);
}

/// <summary>
/// In-process PowerShell host backed by <c>Microsoft.PowerShell.SDK</c>.
/// Owns a singleton <see cref="System.Management.Automation.Runspaces.Runspace"/> that is
/// created lazily on the first call and reused for every subsequent invocation.
/// See /myplans/remoting/powershell-direct/powershell-direct-design.md — PSD-D5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design trade-off — no <c>GetRunspaceAsync</c>:</b> This interface deliberately exposes
/// only <see cref="InvokeAsync"/> and does NOT surface the underlying
/// <see cref="System.Management.Automation.Runspaces.Runspace"/>. Reason: <c>Runspace</c> is a
/// concrete sealed type that cannot be mocked, which would make every consumer
/// (<c>SessionStore</c>, <c>IPowerShellDirectChannel</c>) untestable. All callers can satisfy
/// their needs by passing scripts and bound arguments through <see cref="InvokeAsync"/>.
/// </para>
/// </remarks>
public interface IPowerShellHost
{
    /// <summary>
    /// PowerShell edition currently backing the host. Valid only after
    /// <see cref="EnsureInitializedAsync"/> has completed at least once.
    /// </summary>
    PowerShellEdition Edition { get; }

    /// <summary>
    /// Ensures the underlying runspace is initialized. Idempotent and thread-safe.
    /// The first successful call performs the PS7 probe and falls back to Windows
    /// PowerShell 5.1 if PS7 cannot load the Hyper-V module (known PS7
    /// "Value cannot be null" non-interactive bug).
    /// </summary>
    /// <param name="ct">Cancellation token observed during initialization.</param>
    Task EnsureInitializedAsync(CancellationToken ct = default);

    /// <summary>
    /// Invokes a PowerShell script in the singleton runspace.
    /// Each entry in <paramref name="args"/> is bound as a session variable
    /// (<c>$key</c>) via <c>SessionStateProxy.SetVariable</c> before the script runs,
    /// then cleared (best-effort) after the script returns to avoid leaking values
    /// across calls.
    /// <para>
    /// <b>Concurrency:</b> Calls are serialized internally via a runspace-global
    /// semaphore. Concurrent invocations targeting different VMs queue behind each
    /// other. See <see cref="PowerShellHost"/> remarks (Issue #52, Gate 6 Fix #1).
    /// </para>
    /// <para>
    /// For per-invocation timeout enforcement, see
    /// <see cref="InvokeWithTimeoutAsync"/> (Issue #52, Gate 6 Fix #2).
    /// </para>
    /// </summary>
    /// <param name="script">PowerShell script body to execute.</param>
    /// <param name="args">Optional name/value pairs to bind as <c>$variables</c>.</param>
    /// <param name="ct">Cancellation token; cancellation triggers <c>PowerShell.Stop()</c>.</param>
    Task<PowerShellHostResult> InvokeAsync(
        string script,
        IDictionary<string, object?>? args = null,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="InvokeAsync"/> but with an enforced per-invocation timeout.
    /// When <paramref name="timeoutSeconds"/> is greater than zero the invocation is
    /// bounded by a linked timeout cancellation. On timeout the pipeline is aborted
    /// and a <see cref="TimeoutException"/> is thrown — distinct from caller-driven
    /// <see cref="OperationCanceledException"/> on <paramref name="ct"/>.
    /// A value of <c>0</c> or <c>null</c> means no per-call timeout (caller token only).
    /// (Issue #52, Gate 6 Fix #2.)
    /// </summary>
    Task<PowerShellHostResult> InvokeWithTimeoutAsync(
        string script,
        IDictionary<string, object?>? args,
        int? timeoutSeconds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the Hyper-V state string (e.g. <c>"Running"</c>, <c>"Off"</c>, <c>"Saved"</c>,
    /// <c>"Paused"</c>) for the VM identified by <paramref name="vmId"/> by invoking
    /// <c>Get-VM -Id &lt;vmid&gt; | Select-Object -ExpandProperty State</c> in the in-process
    /// runspace. Throws <see cref="VmNotFoundException"/> when the VM does not exist on the
    /// host. Used by <c>ToolDispatcher.EnsureVmRunningAsync</c> as a pre-flight check —
    /// routing through <see cref="IPowerShellHost"/> rather than the legacy out-of-process
    /// <c>PowerShellExecutor</c> (PSD-D5, PSD-D6 single-facade rule for guest-targeted tools).
    /// (Issue #52, Phase 2 Gate 3 RC-1.)
    /// </summary>
    /// <param name="hostId">Host identifier — used only for typed exception context.</param>
    /// <param name="vmId">VM identifier (GUID string) to query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetVmStateAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// Returns a snapshot of the host's initialization diagnostics for the
    /// <c>vm_diag</c> tool (Issue #52 Phase 2 live-debug observability).
    /// This method is non-blocking and MUST NOT trigger initialization — it reports
    /// whatever state the host has already observed (initialized / cached-failure / fresh).
    /// All string fields are credential-redacted defensively.
    /// </summary>
    PowerShellHostInitDiagnostics GetInitDiagnostics();
}
