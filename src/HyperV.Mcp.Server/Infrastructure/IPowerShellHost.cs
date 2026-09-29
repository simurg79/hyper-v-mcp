using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>Host PowerShell edition. See /myplans/remoting/powershell-direct/powershell-direct-design.md — PSD-D5.</summary>
public enum PowerShellEdition
{
    /// <summary>PowerShell 7.x (in-process via Microsoft.PowerShell.SDK).</summary>
    PowerShell7,

    /// <summary>Windows PowerShell 5.1 (out-of-process fallback for Hyper-V module compatibility).</summary>
    WindowsPowerShell51
}

/// <summary>Invocation result: Success means !HadErrors; Output unwraps PSObject.BaseObject; Stderr joins ErrorRecord.ToString() with '\n'. ExitCode is 0/1/null (success/errors/not applicable), matching the legacy executor's shape.
/// WireStderr contains safe failure facets: exception type/message, FullyQualifiedErrorId, CategoryInfo and inner type/message; no ScriptStackTrace or positional rendering. Null means not composed; use WireOrFullStderr.
/// See /myplans/remoting/session-management/session-open-error-envelope-design.md — SOE-D10: bootstrap-script suppression cannot help because CLR rendering adds those fields after return.</summary>
public sealed record PowerShellHostResult(
    bool Success,
    IReadOnlyList<object?> Output,
    string Stderr,
    int? ExitCode,
    string? WireStderr = null)
{
    /// <summary>Consumer-visible envelopes MUST use this value. Only null WireStderr permits fallback to full Stderr;
    /// an empty safe payload MUST NOT expose positional source rendering or stack detail.</summary>
    public string WireOrFullStderr => WireStderr ?? Stderr;
}

/// <summary>Read-only vm_diag initialization telemetry (Issue #52). Initialized means the runspace opened; Edition is null until initialized.
/// LastInitError contains the cached failure's type/message, redacted; null before an attempt or after success. LastInitErrorType is the full CLR type name, or null.
/// LastInitErrorTrace is the redacted newline-joined type:message chain through every InnerException, or null. PsModulePath is the host process path at snapshot time, available even on failure for triage.</summary>
public sealed record PowerShellHostInitDiagnostics(
    bool Initialized,
    PowerShellEdition? Edition,
    string? LastInitError,
    string? LastInitErrorType,
    string? LastInitErrorTrace,
    string? PsModulePath,
    // RC-8: per-edition stage and full exception chains make cached failures diagnosable; null defaults preserve positional callers.
    PowerShellEditionAttempt? Ps7Attempt = null,
    PowerShellEditionAttempt? Ps51Attempt = null,
    string? StartupState = null,
    string? StartupDetail = null,
    StartupProgress? StartupProgress = null,
    double? StartupElapsedSeconds = null,
    PowerShellChildIdentity? ChildIdentity = null);

public sealed record PowerShellChildIdentity(int? ProcessId, DateTime? StartTimeUtc, string Status);

/// <summary>RC-8: retain per-edition failure stage and full exception details for triage without re-probing. Attempted means entered; Succeeded means a usable runspace opened; FailureStage is the last marker, null on success.
/// ExceptionType/ExceptionMessage describe the outer exception; InnerExceptionType/InnerExceptionMessage describe its immediate inner exception, if any. Messages are credential-redacted.
/// InnerExceptionStackTrace is the immediate inner exception's full stack; FullExceptionToString contains every wrapped type, message and stack from the outer exception's ToString().</summary>
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

/// <summary>RC-8: collect per-stage PS7/PS5.1 probe state, then Build an immutable PowerShellEditionAttempt for host diagnostics.</summary>
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

    /// <summary>Capture outer/immediate-inner details and ToString()'s full wrapped stacks; apply optional redaction to every string to prevent credential leaks.</summary>
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

    /// <summary>For non-exception probe failures, populate only ExceptionMessage and FullExceptionToString.</summary>
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

/// <summary>SDK host with one lazy, reused runspace. See /myplans/remoting/powershell-direct/powershell-direct-design.md — PSD-D5.
/// Expose InvokeAsync, not GetRunspaceAsync: sealed Runspace cannot be mocked, making SessionStore/IPowerShellDirectChannel consumers untestable.
/// Scripts and bound arguments satisfy all callers without exposing the runspace.</summary>
public interface IPowerShellHost
{
    /// <summary>Valid only after EnsureInitializedAsync completes at least once.</summary>
    PowerShellEdition Edition { get; }

    /// <summary>Idempotent, thread-safe initialization: probe PS7, then fall back to PS5.1 if Hyper-V cannot load (known non-interactive null-value bug).</summary>
    /// <param name="ct">Cancellation token observed during initialization.</param>
    Task EnsureInitializedAsync(CancellationToken ct = default);

    /// <summary>Bind optional args as $key session variables, then clear them best-effort to avoid cross-call leaks.
    /// A runspace-global semaphore serializes even cross-VM invocations; see PowerShellHost (Issue #52, Gate 6 Fix #1). Use InvokeWithTimeoutAsync for per-invocation timeouts (Fix #2).</summary>
    /// <param name="script">PowerShell script body.</param><param name="args">Optional variable bindings.</param><param name="ct">Cancellation triggers PowerShell.Stop().</param>
    Task<PowerShellHostResult> InvokeAsync(
        string script,
        IDictionary<string, object?>? args = null,
        CancellationToken ct = default);

    /// <summary>Issue #52, Gate 6 Fix #2: positive timeoutSeconds bounds invocation via linked cancellation, aborting the pipeline with TimeoutException.
    /// Caller cancellation instead throws OperationCanceledException; 0/null means caller token only, no per-call timeout.</summary>
    Task<PowerShellHostResult> InvokeWithTimeoutAsync(
        string script,
        IDictionary<string, object?>? args,
        int? timeoutSeconds,
        CancellationToken ct = default);

    /// <summary>Get-VM -Id returns the VM state (Running/Off/Saved/Paused); a missing VM throws VmNotFoundException.
    /// RC-1: ToolDispatcher preflight uses this host, not the legacy executor, under the PSD-D5/D6 guest-tool single-facade rule.</summary>
    /// <param name="hostId">Typed exception context only.</param><param name="vmId">VM GUID string.</param><param name="ct">Cancellation token.</param>
    Task<string> GetVmStateAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>vm_diag MUST NOT block or trigger init: report observed initialized/cached-failure/fresh state with defensively redacted strings.</summary>
    PowerShellHostInitDiagnostics GetInitDiagnostics();
}
