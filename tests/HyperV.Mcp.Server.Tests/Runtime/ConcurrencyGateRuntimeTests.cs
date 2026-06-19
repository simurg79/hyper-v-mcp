using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Runtime tests for concurrency gate acquire/release semantics.
/// See /myplans/operational/concurrency/concurrency-design.md — CC-D1 through CC-D7.
///
/// These tests exercise the REAL ConcurrencyGate implementation against expected
/// runtime behavior. They will fail with NotImplementedException until the
/// gate is fully implemented with SemaphoreSlim hierarchy.
///
/// Expected runtime flows:
/// - AcquireGlobalSlotAsync returns an IDisposable that releases on dispose
/// - AcquireVmLockAsync serializes per-VM operations
/// - AcquireHostLockAsync serializes per-host lifecycle operations
/// - Timeout produces ConcurrencyLimitException (maps to CONCURRENCY_LIMIT)
/// - Lock acquisition follows the hierarchy: global → host → VM
/// - GetQueueDepth reports current queued waiters (not active slots)
/// - Idle semaphore entries are evicted to prevent unbounded dictionary growth
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement ConcurrencyGate with SemaphoreSlim-based concurrency control.
/// 2. AcquireGlobalSlotAsync: Use SemaphoreSlim(N) where N = MaxConcurrentOperations.
/// 3. AcquireVmLockAsync: Use ConcurrentDictionary of SemaphoreSlim(1,1) keyed by hostId:vmId.
/// 4. AcquireHostLockAsync: Use ConcurrentDictionary of SemaphoreSlim(M) keyed by hostId.
/// 5. Return IDisposable wrapper that releases the semaphore on dispose.
/// 6. Throw ConcurrencyLimitException when WaitAsync times out.
/// 7. GetQueueDepth must track queued waiters, not active slots.
/// 8. Idle semaphore entries must be evicted to prevent unbounded growth.
/// </summary>
[Trait("Category", "Runtime")]
public class ConcurrencyGateRuntimeTests
{
    private ConcurrencyGate CreateGate(int maxGlobal = 10, int maxPerHost = 5)
    {
        var options = new ServerOptions
        {
            MaxConcurrentOperations = maxGlobal,
            MaxPerHostOperations = maxPerHost,
            QueueTimeoutSeconds = 1,  // Short timeout for tests
            VmLockTimeoutSeconds = 1
        };
        return new ConcurrencyGate(options);
    }

    // ─── Global Slot Acquire/Release ───────────────────────────────────

    /// <summary>
    /// Acquiring a global slot must return a non-null IDisposable.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D2.
    /// </summary>
    [Fact]
    public async Task AcquireGlobalSlot_Returns_Disposable_Lock()
    {
        using var gate = CreateGate();

        var lockHandle = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        lockHandle.Should().NotBeNull(
            "AcquireGlobalSlotAsync must return an IDisposable lock handle " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D2)");

        // Disposing the handle should release the slot (no exception)
        lockHandle.Dispose();
    }

    /// <summary>
    /// Acquiring more global slots than the limit must throw ConcurrencyLimitException.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D4: Non-blocking TryWait.
    /// </summary>
    [Fact]
    public async Task AcquireGlobalSlot_Exceeding_Limit_Throws_ConcurrencyLimitException()
    {
        using var gate = CreateGate(maxGlobal: 1);

        // Acquire the only available slot
        var first = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        // Second acquisition must throw (limit = 1, timeout = 1s)
        Func<Task> act = async () => await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyLimitException>(
            "exceeding global slot limit must throw ConcurrencyLimitException " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D4)");

        first.Dispose();
    }

    /// <summary>
    /// After releasing a global slot, it must be available for re-acquisition.
    /// </summary>
    [Fact]
    public async Task Released_GlobalSlot_Is_Reacquirable()
    {
        using var gate = CreateGate(maxGlobal: 1);

        var first = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);
        first.Dispose(); // Release

        // Should succeed since slot was released
        var second = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);
        second.Should().NotBeNull(
            "released global slot must be re-acquirable");
        second.Dispose();
    }

    // ─── Per-VM Lock Acquire/Release ───────────────────────────────────

    /// <summary>
    /// Acquiring a per-VM lock must return a non-null IDisposable.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D1.
    /// </summary>
    [Fact]
    public async Task AcquireVmLock_Returns_Disposable_Lock()
    {
        using var gate = CreateGate();

        var lockHandle = await gate.AcquireVmLockAsync(
            "local", "test-vm-001", TimeSpan.FromSeconds(5), CancellationToken.None);

        lockHandle.Should().NotBeNull(
            "AcquireVmLockAsync must return an IDisposable lock handle " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D1)");
        lockHandle.Dispose();
    }

    /// <summary>
    /// Per-VM lock is SemaphoreSlim(1,1) — second acquire on same VM must timeout.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D1: Per-VM serialization.
    /// </summary>
    [Fact]
    public async Task AcquireVmLock_Same_Vm_Serializes()
    {
        using var gate = CreateGate();

        var first = await gate.AcquireVmLockAsync(
            "local", "test-vm-001", TimeSpan.FromSeconds(5), CancellationToken.None);

        // Second acquire on same VM must timeout
        Func<Task> act = async () => await gate.AcquireVmLockAsync(
            "local", "test-vm-001", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyLimitException>(
            "concurrent operations on the same VM must be serialized " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D1)");

        first.Dispose();
    }

    /// <summary>
    /// Different VMs can be locked concurrently (no serialization conflict).
    /// </summary>
    [Fact]
    public async Task AcquireVmLock_Different_Vms_Can_Run_Concurrently()
    {
        using var gate = CreateGate();

        var lock1 = await gate.AcquireVmLockAsync(
            "local", "vm-001", TimeSpan.FromSeconds(5), CancellationToken.None);
        var lock2 = await gate.AcquireVmLockAsync(
            "local", "vm-002", TimeSpan.FromSeconds(5), CancellationToken.None);

        lock1.Should().NotBeNull();
        lock2.Should().NotBeNull(
            "different VMs should not block each other");

        lock1.Dispose();
        lock2.Dispose();
    }

    // ─── Per-Host Lock ─────────────────────────────────────────────────

    /// <summary>
    /// Acquiring a per-host lock must return a non-null IDisposable.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D3.
    /// </summary>
    [Fact]
    public async Task AcquireHostLock_Returns_Disposable_Lock()
    {
        using var gate = CreateGate();

        var lockHandle = await gate.AcquireHostLockAsync(
            "local", TimeSpan.FromSeconds(5), CancellationToken.None);

        lockHandle.Should().NotBeNull(
            "AcquireHostLockAsync must return an IDisposable lock handle " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D3)");
        lockHandle.Dispose();
    }

    /// <summary>
    /// Per-host lock has a configurable limit (default 5). Exceeding throws.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D3.
    /// </summary>
    [Fact]
    public async Task AcquireHostLock_Exceeding_PerHost_Limit_Throws()
    {
        using var gate = CreateGate(maxGlobal: 10, maxPerHost: 1);

        var first = await gate.AcquireHostLockAsync(
            "local", TimeSpan.FromSeconds(5), CancellationToken.None);

        Func<Task> act = async () => await gate.AcquireHostLockAsync(
            "local", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyLimitException>(
            "exceeding per-host limit must throw ConcurrencyLimitException " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D3)");

        first.Dispose();
    }

    // ─── Queue Depth Reporting ─────────────────────────────────────────

    /// <summary>
    /// GetQueueDepth must return 0 when no operations are waiting.
    /// See /myplans/operational/concurrency/concurrency-design.md — Monitoring.
    /// </summary>
    [Fact]
    public void GetQueueDepth_Initially_Zero()
    {
        using var gate = CreateGate();

        var depth = gate.GetQueueDepth();

        depth.Should().Be(0,
            "queue depth should be 0 when no operations are active or waiting");
    }

    /// <summary>
    /// GetQueueDepth must return 0 when operations are active but none are waiting.
    /// This is a regression test: GetQueueDepth reports queued waiters, not active slots.
    /// Previously, GetQueueDepth incorrectly returned the number of active slots.
    /// See /myplans/operational/concurrency/concurrency-design.md — Monitoring.
    /// </summary>
    [Fact]
    public async Task GetQueueDepth_Reports_Waiters_Not_Active_Slots()
    {
        using var gate = CreateGate(maxGlobal: 2);

        // Acquire two global slots — both are active, none waiting
        var lock1 = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);
        var lock2 = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        // Queue depth should be 0: two active slots, but zero waiters
        gate.GetQueueDepth().Should().Be(0,
            "GetQueueDepth must report queued waiters (0), not active slots (2). " +
            "This is a regression test for the bug where GetQueueDepth incorrectly " +
            "reported MaxConcurrentOperations - CurrentCount (active slots).");

        // GetActiveSlotCount should report 2 active slots
        gate.GetActiveSlotCount().Should().Be(2,
            "GetActiveSlotCount should report 2 active slots");

        lock1.Dispose();
        lock2.Dispose();
    }

    /// <summary>
    /// GetQueueDepth must reflect threads that are blocked waiting for a global slot.
    /// Regression test: ensures the queue depth counter tracks actual waiters.
    /// </summary>
    [Fact]
    public async Task GetQueueDepth_Increments_When_Waiter_Blocked()
    {
        using var gate = CreateGate(maxGlobal: 1);

        // Acquire the only slot
        var lock1 = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        // Start a second acquire that will block (with a long timeout)
        var waiterStarted = new TaskCompletionSource<bool>();
        var waiterTask = Task.Run(async () =>
        {
            waiterStarted.SetResult(true);
            // This will block because the slot is taken. Use a short timeout
            // so the test doesn't hang, but long enough to observe the queue depth.
            try
            {
                await gate.AcquireGlobalSlotAsync(
                    TimeSpan.FromMilliseconds(500), CancellationToken.None);
            }
            catch (ConcurrencyLimitException)
            {
                // Expected — timeout
            }
        });

        // Wait for the waiter task to start
        await waiterStarted.Task;
        // Give the waiter a moment to enter WaitAsync and increment the counter
        await Task.Delay(50);

        // Now queue depth should be at least 1 (the blocked waiter)
        gate.GetQueueDepth().Should().BeGreaterThanOrEqualTo(1,
            "GetQueueDepth must count threads blocked waiting for global slot");

        lock1.Dispose();
        await waiterTask;

        // After everything completes, queue depth should be 0
        gate.GetQueueDepth().Should().Be(0,
            "queue depth should return to 0 after all waiters complete");
    }

    // ─── Semaphore Eviction ────────────────────────────────────────────

    /// <summary>
    /// After releasing a per-VM lock, the idle semaphore entry must be evicted
    /// from the dictionary to prevent unbounded memory growth under VM churn.
    /// Regression test for unbounded _vmSemaphores dictionary growth.
    /// </summary>
    [Fact]
    public async Task VmSemaphore_Evicted_After_Release()
    {
        using var gate = CreateGate();

        // Acquire and release a VM lock
        var lockHandle = await gate.AcquireVmLockAsync(
            "local", "ephemeral-vm", TimeSpan.FromSeconds(5), CancellationToken.None);

        gate.VmSemaphoreCount.Should().BeGreaterOrEqualTo(1,
            "VM semaphore should exist while lock is held");

        lockHandle.Dispose();

        // After release, the idle entry should be evicted
        gate.VmSemaphoreCount.Should().Be(0,
            "idle VM semaphore entries must be evicted after release " +
            "to prevent unbounded dictionary growth");
    }

    /// <summary>
    /// After releasing a per-host lock, the idle semaphore entry must be evicted.
    /// Regression test for unbounded _hostSemaphores dictionary growth.
    /// </summary>
    [Fact]
    public async Task HostSemaphore_Evicted_After_Release()
    {
        using var gate = CreateGate();

        // Acquire and release a host lock
        var lockHandle = await gate.AcquireHostLockAsync(
            "ephemeral-host", TimeSpan.FromSeconds(5), CancellationToken.None);

        gate.HostSemaphoreCount.Should().BeGreaterOrEqualTo(1,
            "host semaphore should exist while lock is held");

        lockHandle.Dispose();

        // After release, the idle entry should be evicted
        gate.HostSemaphoreCount.Should().Be(0,
            "idle host semaphore entries must be evicted after release " +
            "to prevent unbounded dictionary growth");
    }

    /// <summary>
    /// Semaphore entries must NOT be evicted while still in use by another waiter.
    /// This ensures the eviction strategy is safe under concurrent access.
    /// When the first lock is released, the waiter may either succeed in acquiring
    /// or time out — either way, the entry must be evicted after all references are released.
    /// </summary>
    [Fact]
    public async Task VmSemaphore_Not_Evicted_While_In_Use()
    {
        using var gate = CreateGate();

        // Acquire a VM lock
        var lock1 = await gate.AcquireVmLockAsync(
            "local", "busy-vm", TimeSpan.FromSeconds(5), CancellationToken.None);

        // Start a second acquire that will block (but succeed once lock1 is released)
        var waiterStarted = new TaskCompletionSource<bool>();
        var waiterTask = Task.Run(async () =>
        {
            waiterStarted.SetResult(true);
            try
            {
                var acquiredLock = await gate.AcquireVmLockAsync(
                    "local", "busy-vm", TimeSpan.FromMilliseconds(500), CancellationToken.None);
                // Waiter succeeded — dispose the acquired lock to release the semaphore
                acquiredLock.Dispose();
            }
            catch (ConcurrencyLimitException)
            {
                // Timed out — the refCount was already decremented in the catch path
            }
        });

        await waiterStarted.Task;
        await Task.Delay(50);

        // Verify the semaphore entry exists while both references are active
        gate.VmSemaphoreCount.Should().BeGreaterOrEqualTo(1,
            "VM semaphore should exist while lock is held and waiter is pending");

        // Release first lock — the waiter will succeed and acquire the semaphore
        lock1.Dispose();

        // Wait for the waiter to complete (it acquires, then disposes)
        await waiterTask;

        // After everything completes, the entry should be evicted
        gate.VmSemaphoreCount.Should().Be(0,
            "VM semaphore should be evicted after all references are released");
    }

    /// <summary>
    /// Many distinct VM locks acquired and released must not cause unbounded growth.
    /// Regression test: verifies eviction under churn scenario.
    /// </summary>
    [Fact]
    public async Task Many_Distinct_VmLocks_Do_Not_Cause_Unbounded_Growth()
    {
        using var gate = CreateGate();

        // Acquire and release locks for many distinct VMs
        for (int i = 0; i < 200; i++)
        {
            var lockHandle = await gate.AcquireVmLockAsync(
                "local", $"vm-{i}", TimeSpan.FromSeconds(5), CancellationToken.None);
            lockHandle.Dispose();
        }

        // All entries should have been evicted after release
        gate.VmSemaphoreCount.Should().Be(0,
            "all idle VM semaphore entries should be evicted after release, " +
            "preventing unbounded growth under VM churn");
    }

    // ─── CancellationToken Support ─────────────────────────────────────

    /// <summary>
    /// CancellationToken must be respected during lock acquisition.
    /// See /myplans/operational/concurrency/concurrency-design.md — Constraint #4.
    /// </summary>
    [Fact]
    public async Task AcquireVmLock_Respects_CancellationToken()
    {
        using var gate = CreateGate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await gate.AcquireVmLockAsync(
            "local", "test-vm", TimeSpan.FromSeconds(5), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancelled token must cause immediate cancellation " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Constraint #4)");
    }

    // ─── Race Condition Regression Tests ────────────────────────────────

    /// <summary>
    /// Regression test for the eviction/acquire race condition.
    /// Rapidly acquires and releases VM locks from multiple concurrent threads
    /// on the same key to exercise the AcquireEntryRef retry loop.
    /// No ObjectDisposedException should be thrown.
    /// </summary>
    [Fact]
    public async Task Concurrent_Acquire_Release_Same_Key_No_Disposed_Exception()
    {
        using var gate = CreateGate(maxGlobal: 100);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = new List<Task>();

        // 20 concurrent tasks all racing on the same VM key
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                for (int j = 0; j < 10; j++)
                {
                    try
                    {
                        var lockHandle = await gate.AcquireVmLockAsync(
                            "local", "race-vm", TimeSpan.FromSeconds(5), CancellationToken.None);
                        // Brief hold to create eviction opportunities
                        await Task.Yield();
                        lockHandle.Dispose();
                    }
                    catch (ConcurrencyLimitException)
                    {
                        // Expected: VM lock is SemaphoreSlim(1,1), so contention timeouts are normal
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty(
            "no ObjectDisposedException or other unexpected exceptions should occur " +
            "during concurrent acquire/release on the same key — " +
            "regression test for the eviction/acquire race condition");
    }

    /// <summary>
    /// Regression test for the eviction/acquire race condition on host locks.
    /// Exercises the same AcquireEntryRef path for per-host semaphores.
    /// </summary>
    [Fact]
    public async Task Concurrent_Acquire_Release_Host_Lock_No_Disposed_Exception()
    {
        using var gate = CreateGate(maxGlobal: 100, maxPerHost: 2);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = new List<Task>();

        // 10 concurrent tasks racing on the same host key
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                for (int j = 0; j < 10; j++)
                {
                    try
                    {
                        var lockHandle = await gate.AcquireHostLockAsync(
                            "race-host", TimeSpan.FromSeconds(5), CancellationToken.None);
                        await Task.Yield();
                        lockHandle.Dispose();
                    }
                    catch (ConcurrencyLimitException)
                    {
                        // Expected under contention
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty(
            "no ObjectDisposedException or other unexpected exceptions should occur " +
            "during concurrent host lock acquire/release — " +
            "regression test for the eviction/acquire race condition");
    }

    /// <summary>
    /// Regression test: acquire a VM lock on a key that was just evicted.
    /// Simulates the exact race scenario where Thread A gets a stale entry from
    /// GetOrAdd while Thread B just evicted and disposed that entry.
    /// The AcquireEntryRef retry loop must handle this gracefully.
    /// </summary>
    [Fact]
    public async Task Acquire_After_Eviction_Succeeds_Without_Disposed_Exception()
    {
        using var gate = CreateGate();

        // Acquire and release to create and evict the entry
        var lock1 = await gate.AcquireVmLockAsync(
            "local", "evict-test-vm", TimeSpan.FromSeconds(5), CancellationToken.None);
        lock1.Dispose();

        // Entry should be evicted
        gate.VmSemaphoreCount.Should().Be(0,
            "entry should be evicted after release");

        // Re-acquire on the same key — must create a new entry, not use disposed one
        var lock2 = await gate.AcquireVmLockAsync(
            "local", "evict-test-vm", TimeSpan.FromSeconds(5), CancellationToken.None);
        lock2.Should().NotBeNull(
            "re-acquiring a VM lock after eviction must succeed with a fresh semaphore");

        gate.VmSemaphoreCount.Should().Be(1,
            "new entry should exist after re-acquisition");

        lock2.Dispose();

        gate.VmSemaphoreCount.Should().Be(0,
            "entry should be evicted again after second release");
    }

    /// <summary>
    /// Regression test: rapid interleaved acquire/release across many distinct keys
    /// with concurrent operations should not leak semaphore entries.
    /// Exercises the eviction cleanup under high churn with parallelism.
    /// </summary>
    [Fact]
    public async Task Parallel_Distinct_Keys_Do_Not_Leak_Semaphore_Entries()
    {
        using var gate = CreateGate(maxGlobal: 100);
        var tasks = new List<Task>();

        // 50 concurrent tasks each using a unique key
        for (int i = 0; i < 50; i++)
        {
            var vmId = $"parallel-vm-{i}";
            tasks.Add(Task.Run(async () =>
            {
                var lockHandle = await gate.AcquireVmLockAsync(
                    "local", vmId, TimeSpan.FromSeconds(5), CancellationToken.None);
                await Task.Yield();
                lockHandle.Dispose();
            }));
        }

        await Task.WhenAll(tasks);

        // All entries should have been evicted after release
        gate.VmSemaphoreCount.Should().Be(0,
            "all VM semaphore entries should be evicted after parallel release, " +
            "verifying no entries leaked under concurrent churn");
    }
}
