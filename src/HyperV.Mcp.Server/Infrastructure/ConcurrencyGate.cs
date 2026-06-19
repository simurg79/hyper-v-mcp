using System.Collections.Concurrent;
using HyperV.Mcp.Server.Configuration;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Three-level semaphore hierarchy for concurrency control.
/// See /myplans/operational/concurrency/concurrency-design.md — CC-D1 through CC-D7.
///
/// Lock acquisition order (deadlock prevention):
///   1. Global slot (outermost) — SemaphoreSlim(MaxConcurrentOperations)
///   2. Per-host lock (if needed) — SemaphoreSlim(MaxPerHostOperations) keyed by hostId
///   3. Per-VM lock (innermost) — SemaphoreSlim(1,1) keyed by "hostId:vmId"
///
/// Design decisions:
/// - Per-VM locks use SemaphoreSlim(1,1) for strict serialization (CC-D1).
/// - Per-host locks use SemaphoreSlim(MaxPerHostOperations) to limit concurrent
///   operations per host (CC-D3).
/// - GetQueueDepth returns the number of threads waiting to acquire the global
///   semaphore, tracked via an atomic counter incremented before WaitAsync and
///   decremented after WaitAsync completes or fails.
/// - ConcurrencyLimitException is thrown when WaitAsync times out (CC-D4).
/// - CancellationToken is forwarded to SemaphoreSlim.WaitAsync for proper
///   cancellation support (Constraint #4).
/// - SemaphoreSlim instances for per-VM and per-host locks are lazily created
///   and stored in ConcurrentDictionary to support dynamic VM/host sets.
/// - Idle semaphore entries are evicted via TryEvictIdle after lock release
///   to prevent unbounded dictionary growth under VM/host churn (CC-D7).
/// </summary>
public class ConcurrencyGate : IConcurrencyGate
{
    private readonly ServerOptions _options;
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly ConcurrentDictionary<string, EvictableSemaphore> _vmSemaphores = new();
    private readonly ConcurrentDictionary<string, EvictableSemaphore> _hostSemaphores = new();

    /// <summary>
    /// Tracks the number of threads currently waiting to acquire the global semaphore.
    /// Incremented before WaitAsync, decremented after (whether success or failure).
    /// </summary>
    private int _globalQueueDepth;

    /// <summary>
    /// Maximum number of idle semaphore entries per dictionary before triggering eviction.
    /// This bounds memory growth under VM/host churn.
    /// </summary>
    internal const int MaxIdleEntries = 100;

    public ConcurrencyGate(ServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _globalSemaphore = new SemaphoreSlim(
            _options.MaxConcurrentOperations,
            _options.MaxConcurrentOperations);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Acquires a global concurrency slot. Returns an IDisposable that releases
    /// the slot when disposed. Throws ConcurrencyLimitException if the timeout
    /// expires before a slot becomes available (CC-D4).
    /// Queue depth is tracked via _globalQueueDepth atomic counter.
    /// </remarks>
    public async Task<IDisposable> AcquireGlobalSlotAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _globalQueueDepth);
        try
        {
            var acquired = await _globalSemaphore.WaitAsync(timeout, ct);
            if (!acquired)
            {
                throw new ConcurrencyLimitException(
                    $"Global concurrency limit reached: {_options.MaxConcurrentOperations} operations in progress");
            }

            return new SemaphoreReleaser(_globalSemaphore);
        }
        finally
        {
            Interlocked.Decrement(ref _globalQueueDepth);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Acquires a per-VM serialization lock using SemaphoreSlim(1,1).
    /// Key is "hostId:vmId" to scope locks per host+VM combination.
    /// Idle semaphores are evicted after release to prevent unbounded growth.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D1.
    ///
    /// Race-safe acquire: uses AcquireEntryRef to atomically get-or-add an entry
    /// and increment its ref count before any eviction can dispose it.
    /// </remarks>
    public async Task<IDisposable> AcquireVmLockAsync(string hostId, string vmId,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var key = $"{hostId}:{vmId}";
        var entry = AcquireEntryRef(key, _vmSemaphores, () => new EvictableSemaphore(1, 1));
        bool acquired = false;

        try
        {
            acquired = await entry.Semaphore.WaitAsync(timeout, ct);
            if (!acquired)
            {
                throw new ConcurrencyLimitException(
                    $"VM '{vmId}' on host '{hostId}' is busy with another operation");
            }

            return new EvictableSemaphoreReleaser(entry, key, _vmSemaphores, MaxIdleEntries);
        }
        catch
        {
            // On any failure path (timeout, cancellation, ObjectDisposedException, etc.),
            // decrement the ref count and attempt eviction to prevent resource leaks.
            if (!acquired)
            {
                entry.DecrementRefCount();
                TryEvictEntry(key, entry, _vmSemaphores);
            }
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Acquires a per-host lock using SemaphoreSlim(MaxPerHostOperations).
    /// Idle semaphores are evicted after release to prevent unbounded growth.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D3.
    ///
    /// Race-safe acquire: uses AcquireEntryRef to atomically get-or-add an entry
    /// and increment its ref count before any eviction can dispose it.
    /// </remarks>
    public async Task<IDisposable> AcquireHostLockAsync(string hostId,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var entry = AcquireEntryRef(
            hostId, _hostSemaphores,
            () => new EvictableSemaphore(_options.MaxPerHostOperations, _options.MaxPerHostOperations));
        bool acquired = false;

        try
        {
            acquired = await entry.Semaphore.WaitAsync(timeout, ct);
            if (!acquired)
            {
                throw new ConcurrencyLimitException(
                    $"Per-host concurrency limit reached for host '{hostId}': {_options.MaxPerHostOperations} operations in progress");
            }

            return new EvictableSemaphoreReleaser(entry, hostId, _hostSemaphores, MaxIdleEntries);
        }
        catch
        {
            // On any failure path (timeout, cancellation, ObjectDisposedException, etc.),
            // decrement the ref count and attempt eviction to prevent resource leaks.
            if (!acquired)
            {
                entry.DecrementRefCount();
                TryEvictEntry(hostId, entry, _hostSemaphores);
            }
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the number of threads currently waiting to acquire the global semaphore.
    /// This is the true queue depth (waiters), not the number of active slots.
    /// See /myplans/operational/concurrency/concurrency-design.md — Monitoring.
    /// </remarks>
    public int GetQueueDepth()
    {
        return Volatile.Read(ref _globalQueueDepth);
    }

    /// <summary>
    /// Returns the number of active global slots (acquired but not released).
    /// Computed as MaxConcurrentOperations minus the current semaphore count.
    /// Useful for diagnostics alongside GetQueueDepth.
    /// </summary>
    public int GetActiveSlotCount()
    {
        return _options.MaxConcurrentOperations - _globalSemaphore.CurrentCount;
    }

    /// <summary>
    /// Returns the current number of tracked per-VM semaphore entries.
    /// Useful for monitoring dictionary size and validating eviction.
    /// </summary>
    internal int VmSemaphoreCount => _vmSemaphores.Count;

    /// <summary>
    /// Returns the current number of tracked per-host semaphore entries.
    /// Useful for monitoring dictionary size and validating eviction.
    /// </summary>
    internal int HostSemaphoreCount => _hostSemaphores.Count;

    /// <summary>
    /// Atomically acquires a reference to a semaphore entry, preventing the
    /// dispose-after-eviction race. The entry's ref count is incremented
    /// atomically with a disposed check (via TryIncrementRefCount), guaranteeing
    /// the semaphore won't be disposed while the caller is using it.
    ///
    /// Race-safety: TryIncrementRefCount and TryMarkDisposedIfIdle use the same
    /// internal SpinLock to ensure mutual exclusion between acquire and eviction
    /// paths. This closes the window where an entry could be disposed between
    /// GetOrAdd returning and IncrementRefCount executing.
    /// </summary>
    private static EvictableSemaphore AcquireEntryRef(
        string key,
        ConcurrentDictionary<string, EvictableSemaphore> dictionary,
        Func<EvictableSemaphore> factory)
    {
        // Spin until we get a non-disposed entry with a valid ref count.
        // In practice this loop executes at most twice: once if the initial
        // entry was concurrently evicted, once more with a fresh entry.
        while (true)
        {
            var entry = dictionary.GetOrAdd(key, _ => factory());

            // Atomically increment ref count only if not disposed.
            // This closes the race window: if eviction disposed the entry
            // between GetOrAdd and here, TryIncrementRefCount returns false.
            if (!entry.TryIncrementRefCount())
            {
                // Entry is disposed — remove stale entry so GetOrAdd creates a fresh one.
                dictionary.TryRemove(new KeyValuePair<string, EvictableSemaphore>(key, entry));
                continue;
            }

            // Verify the dictionary still holds this exact instance.
            // If another thread replaced it, drop ref and retry.
            if (dictionary.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                return entry;
            }

            // The entry was evicted but not yet disposed (still has our ref).
            // Re-add it if the slot is empty, or drop ref if someone else took the slot.
            if (dictionary.TryAdd(key, entry))
            {
                return entry;
            }

            // Slot was taken by a new entry. Drop our ref on the orphaned one
            // and attempt disposal if it became idle.
            entry.DecrementRefCount();
            entry.TryDisposeIfIdle();
        }
    }

    /// <summary>
    /// Attempts to evict a specific entry from a semaphore dictionary if idle.
    /// Called from both the releaser Dispose and the timeout/cancel paths
    /// to ensure entries are cleaned up regardless of which path completes last.
    ///
    /// Race-safety: Uses TryMarkDisposedIfIdle which atomically checks idle state
    /// and sets the disposed flag under the entry's SpinLock, preventing concurrent
    /// AcquireEntryRef from incrementing the ref count on a soon-to-be-disposed entry.
    /// The sequence is: mark disposed (under lock) → remove from dictionary → dispose semaphore.
    /// </summary>
    private static void TryEvictEntry(string key, EvictableSemaphore entry,
        ConcurrentDictionary<string, EvictableSemaphore> dictionary)
    {
        if (entry.TryMarkDisposedIfIdle())
        {
            // Entry is now marked disposed — no new acquirers can reference it.
            // Remove from dictionary and dispose the semaphore.
            dictionary.TryRemove(new KeyValuePair<string, EvictableSemaphore>(key, entry));
            entry.Semaphore.Dispose();
        }
    }

    public void Dispose()
    {
        _globalSemaphore.Dispose();

        foreach (var entry in _vmSemaphores.Values)
        {
            entry.Semaphore.Dispose();
        }
        _vmSemaphores.Clear();

        foreach (var entry in _hostSemaphores.Values)
        {
            entry.Semaphore.Dispose();
        }
        _hostSemaphores.Clear();
    }

    /// <summary>
    /// Wraps a SemaphoreSlim with a reference count and disposal flag for safe eviction.
    /// When refCount drops to 0, the entry is eligible for removal from the dictionary.
    ///
    /// Race-safety invariant: TryIncrementRefCount and TryMarkDisposedIfIdle use the
    /// same SpinLock to ensure mutual exclusion. This guarantees that:
    ///   - An acquirer cannot increment the ref count on an entry that is being disposed.
    ///   - An evictor cannot mark an entry disposed while an acquirer is referencing it.
    /// The SpinLock is held only for a few integer operations, so contention is minimal.
    /// </summary>
    internal sealed class EvictableSemaphore
    {
        public SemaphoreSlim Semaphore { get; }
        private int _refCount;
        private int _disposed;
        private SpinLock _lifecycleLock = new(enableThreadOwnerTracking: false);

        /// <summary>
        /// The maximum count this semaphore was created with.
        /// Used to determine if the semaphore is fully released (idle).
        /// </summary>
        public int MaxCount { get; }

        public EvictableSemaphore(int initialCount, int maxCount)
        {
            Semaphore = new SemaphoreSlim(initialCount, maxCount);
            MaxCount = maxCount;
            _refCount = 0;
            _disposed = 0;
        }

        /// <summary>
        /// Atomically increments the ref count only if the entry is not disposed.
        /// Returns true if the ref count was incremented; false if the entry is
        /// already disposed and must not be used.
        ///
        /// This is the acquire-side of the lifecycle lock. It ensures that once
        /// TryMarkDisposedIfIdle sets _disposed = 1, no new acquirers can increment
        /// the ref count, preventing use-after-dispose.
        /// </summary>
        public bool TryIncrementRefCount()
        {
            bool lockTaken = false;
            try
            {
                _lifecycleLock.Enter(ref lockTaken);
                if (Volatile.Read(ref _disposed) == 1)
                    return false;
                _refCount++;
                return true;
            }
            finally
            {
                if (lockTaken) _lifecycleLock.Exit(useMemoryBarrier: true);
            }
        }

        public void DecrementRefCount() => Interlocked.Decrement(ref _refCount);

        /// <summary>
        /// Atomically checks if the entry is idle (refCount &lt;= 0 and semaphore
        /// fully released) and marks it as disposed if so. Returns true if the
        /// entry was marked disposed (caller must then dispose the semaphore).
        ///
        /// This is the eviction-side of the lifecycle lock. It ensures that once
        /// an acquirer has incremented the ref count (inside the same lock), the
        /// evictor will see the non-idle state and skip disposal.
        /// </summary>
        public bool TryMarkDisposedIfIdle()
        {
            bool lockTaken = false;
            try
            {
                _lifecycleLock.Enter(ref lockTaken);
                if (_refCount <= 0 && Semaphore.CurrentCount == MaxCount
                    && Volatile.Read(ref _disposed) == 0)
                {
                    _disposed = 1;
                    return true;
                }
                return false;
            }
            finally
            {
                if (lockTaken) _lifecycleLock.Exit(useMemoryBarrier: true);
            }
        }

        /// <summary>
        /// Convenience method: attempts to mark disposed and dispose the semaphore
        /// if idle. Safe to call from any thread. No-op if not idle or already disposed.
        /// </summary>
        public void TryDisposeIfIdle()
        {
            if (TryMarkDisposedIfIdle())
            {
                Semaphore.Dispose();
            }
        }

        /// <summary>
        /// Returns true if no threads are referencing or waiting on this semaphore
        /// and the semaphore is fully released (all slots available).
        /// Note: This is a non-locked snapshot; for race-safe disposal use
        /// TryMarkDisposedIfIdle instead.
        /// </summary>
        public bool IsIdle => Volatile.Read(ref _refCount) <= 0 && Semaphore.CurrentCount == MaxCount;

        /// <summary>
        /// Returns true if this entry's semaphore has been disposed.
        /// Used by AcquireEntryRef to detect stale entries.
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        /// <summary>
        /// Marks this entry as disposed unconditionally. Used only during
        /// ConcurrencyGate.Dispose() for bulk cleanup.
        /// </summary>
        public void MarkDisposed() => Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>
    /// IDisposable wrapper that releases a SemaphoreSlim when disposed,
    /// then attempts to evict idle entries from the parent dictionary
    /// to prevent unbounded growth.
    /// </summary>
    private sealed class EvictableSemaphoreReleaser : IDisposable
    {
        private EvictableSemaphore? _entry;
        private readonly string _key;
        private readonly ConcurrentDictionary<string, EvictableSemaphore> _dictionary;
        private readonly int _maxIdleEntries;

        public EvictableSemaphoreReleaser(
            EvictableSemaphore entry,
            string key,
            ConcurrentDictionary<string, EvictableSemaphore> dictionary,
            int maxIdleEntries)
        {
            _entry = entry;
            _key = key;
            _dictionary = dictionary;
            _maxIdleEntries = maxIdleEntries;
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null) return;

            entry.Semaphore.Release();
            entry.DecrementRefCount();

            // Try to evict this specific entry if it's now idle.
            // Uses TryMarkDisposedIfIdle for race-safe disposal.
            TryEvictEntry(_key, entry);

            // If dictionary is over capacity, scan for idle entries to evict
            if (_dictionary.Count > _maxIdleEntries)
            {
                TryEvictIdle();
            }
        }

        private void TryEvictEntry(string key, EvictableSemaphore entry)
        {
            if (entry.TryMarkDisposedIfIdle())
            {
                // Entry is now marked disposed — no new acquirers can reference it.
                _dictionary.TryRemove(new KeyValuePair<string, EvictableSemaphore>(key, entry));
                entry.Semaphore.Dispose();
            }
        }

        private void TryEvictIdle()
        {
            foreach (var kvp in _dictionary)
            {
                if (kvp.Value.TryMarkDisposedIfIdle())
                {
                    _dictionary.TryRemove(kvp);
                    kvp.Value.Semaphore.Dispose();
                }

                // Stop once we're under the limit
                if (_dictionary.Count <= _maxIdleEntries)
                    break;
            }
        }
    }

    /// <summary>
    /// IDisposable wrapper that releases a SemaphoreSlim when disposed.
    /// Used for the global semaphore which doesn't need eviction.
    /// This enables the "using" pattern for lock acquisition/release:
    ///   using var lock = await gate.AcquireGlobalSlotAsync(...);
    ///   // lock is released when 'lock' goes out of scope
    /// </summary>
    private sealed class SemaphoreReleaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            // Use Interlocked.Exchange to ensure single-release even if
            // Dispose is called multiple times (defensive).
            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            semaphore?.Release();
        }
    }
}
