namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Owns the lifecycle of persistent PSSession objects targeting Hyper-V guest VMs.
/// Sessions live inside the singleton IPowerShellHost runspace and are referenced
/// from PowerShell scripts via $global:__HvMcpSessions[sessionName].
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Returns a healthy SessionHandle for (hostId, vmId), creating a new
    /// New-PSSession -VMId session inside the host runspace if necessary.
    /// On a cache hit, runs IsAliveAsync first; if unhealthy, evicts and recreates.
    /// Serialized per-(hostId,vmId).
    /// </summary>
    Task<SessionHandle> GetOrCreateAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        CancellationToken ct = default);

    /// <summary>
    /// Probes the underlying PSSession with a trivial command (e.g. `1`) and
    /// returns true iff it returns success. Catches all exceptions and returns false.
    /// </summary>
    Task<bool> IsAliveAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// Removes the session entry from the store and runs Remove-PSSession
    /// against the underlying PSSession. Idempotent: no-op if no entry.
    /// </summary>
    Task EvictAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// Removes and disposes ALL sessions for the given host. Used during shutdown.
    /// </summary>
    Task EvictAllAsync(string hostId, CancellationToken ct = default);

    /// <summary>
    /// True iff the store currently has a (possibly unhealthy) entry for (hostId, vmId).
    /// Does NOT trigger a health check.
    /// </summary>
    bool HasSession(string hostId, string vmId);
}

/// <summary>
/// Opaque handle to a live PSSession inside IPowerShellHost.
/// Pass SessionName into PowerShell scripts via the variable bound by IPowerShellHost.InvokeAsync,
/// then reference $global:__HvMcpSessions[$sessionName] inside the script.
/// </summary>
public sealed record SessionHandle(string HostId, string VmId, string SessionName);
