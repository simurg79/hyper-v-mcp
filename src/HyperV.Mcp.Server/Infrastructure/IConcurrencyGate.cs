namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Concurrency gate interface for three-level semaphore hierarchy.
/// See /myplans/operational/concurrency/concurrency-design.md — CC-D1 through CC-D7.
/// 
/// Lock acquisition order (deadlock prevention):
///   1. Global slot (outermost)
///   2. Per-host lock (if needed)
///   3. Per-VM lock (innermost)
/// </summary>
public interface IConcurrencyGate : IDisposable
{
    /// <summary>
    /// Acquire per-VM serialization lock. Prevents concurrent commands
    /// on the same VM from racing on a shared PSSession.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D1.
    /// </summary>
    Task<IDisposable> AcquireVmLockAsync(string hostId, string vmId,
        TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Acquire per-host lock for lifecycle operations.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D3.
    /// </summary>
    Task<IDisposable> AcquireHostLockAsync(string hostId,
        TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Acquire a global concurrency slot.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D2.
    /// </summary>
    Task<IDisposable> AcquireGlobalSlotAsync(
        TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Get current queue depth (number of threads waiting to acquire the global semaphore)
    /// for diagnostics. Returns waiters, not active slots.
    /// </summary>
    int GetQueueDepth();
}
