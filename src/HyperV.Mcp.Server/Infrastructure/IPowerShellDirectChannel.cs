namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Single facade for all guest-targeted PowerShell Direct operations.
/// Owns per-(hostId,vmId) serialization, broken-session retry, and credential redaction.
/// See /myplans/remoting/powershell-direct/powershell-direct-design.md — PSD-D6, PSD-D7, PSD-D8.
/// </summary>
public interface IPowerShellDirectChannel
{
    /// <summary>
    /// Executes a script inside the persistent PSSession for (hostId, vmId).
    /// The script runs via <c>Invoke-Command -Session $session -ScriptBlock { ... }</c>.
    /// <para>
    /// Caller-supplied <paramref name="args"/> are passed positionally to the remote script
    /// via <c>-ArgumentList</c> in insertion order; the remote <paramref name="script"/> body
    /// must declare a matching <c>param(...)</c> block at the top to receive them.
    /// </para>
    /// Stderr in the returned result has the literal <paramref name="password"/> substring redacted.
    /// </summary>
    /// <param name="hostId">Target Hyper-V host identifier.</param>
    /// <param name="vmId">Target guest VM identifier.</param>
    /// <param name="username">Guest username (used to acquire/refresh the PSSession).</param>
    /// <param name="password">Guest password (used to acquire/refresh the PSSession).</param>
    /// <param name="script">PowerShell script body to execute inside the guest session.</param>
    /// <param name="args">Optional ordered name/value pairs passed positionally as <c>-ArgumentList</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PowerShellHostResult> InvokeScriptAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string script,
        IDictionary<string, object?>? args = null,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="InvokeScriptAsync"/> but with an enforced per-invocation timeout.
    /// When <paramref name="timeoutSeconds"/> is greater than zero the invocation is
    /// bounded by a linked timeout cancellation. On timeout the call throws
    /// <see cref="TimeoutException"/>. (Issue #52, Gate 6 Fix #2.)
    /// </summary>
    Task<PowerShellHostResult> InvokeScriptWithTimeoutAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string script,
        IDictionary<string, object?>? args,
        int timeoutSeconds,
        CancellationToken ct = default);

    /// <summary>
    /// Copies a single local file to the guest via <c>Copy-Item -ToSession</c>.
    /// </summary>
    /// <param name="hostId">Target Hyper-V host identifier.</param>
    /// <param name="vmId">Target guest VM identifier.</param>
    /// <param name="username">Guest username.</param>
    /// <param name="password">Guest password.</param>
    /// <param name="localSourcePath">Source path on the local host.</param>
    /// <param name="guestDestinationPath">Destination path inside the guest.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PowerShellHostResult> CopyToSessionAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string localSourcePath,
        string guestDestinationPath,
        CancellationToken ct = default);

    /// <summary>
    /// Copies a single guest file back to the local host via <c>Copy-Item -FromSession</c>.
    /// </summary>
    /// <param name="hostId">Target Hyper-V host identifier.</param>
    /// <param name="vmId">Target guest VM identifier.</param>
    /// <param name="username">Guest username.</param>
    /// <param name="password">Guest password.</param>
    /// <param name="guestSourcePath">Source path inside the guest.</param>
    /// <param name="localDestinationPath">Destination path on the local host.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PowerShellHostResult> CopyFromSessionAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string guestSourcePath,
        string localDestinationPath,
        CancellationToken ct = default);

    /// <summary>
    /// Best-effort eviction of any persistent PSSession for (hostId, vmId).
    /// Idempotent. Called by <c>ToolDispatcher.HandleDestroyAsync</c> BEFORE
    /// <c>IHyperVManager.DestroyVmAsync</c> (SM-D7) and by the channel itself on
    /// broken-session retry.
    /// </summary>
    /// <param name="hostId">Target Hyper-V host identifier.</param>
    /// <param name="vmId">Target guest VM identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EvictSessionAsync(string hostId, string vmId, CancellationToken ct = default);
}
