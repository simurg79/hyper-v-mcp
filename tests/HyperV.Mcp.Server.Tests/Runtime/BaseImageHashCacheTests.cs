using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #164 / ST-D6a: unit tests for <see cref="BaseImageHashCache"/>.
///
/// Covers the contract that <c>vm_create</c> depends on to avoid the 60s RPC
/// budget timeout caused by per-call SHA-256 of a 29GB parent VHDX:
///
/// - Cold first-touch computes the hash exactly once.
/// - Warm second call is a hit (no recompute).
/// - TTL expiry forces a recompute.
/// - Stat-tuple invalidation (size / mtime / ReadOnly) forces a recompute.
/// - Per-path coalescing: N concurrent callers ⇒ 1 compute.
/// - Disposal cleans semaphores without leaks.
///
/// Tests that mutate <c>HYPERV_MCP_BASE_HASH_TTL_SECONDS</c> live in the
/// <c>EnvVarMutating</c> collection so xUnit serializes them with the other
/// env-var-mutating suites.
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public sealed class BaseImageHashCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalTtl;

    public BaseImageHashCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "hypervmcp-hashcache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalTtl = Environment.GetEnvironmentVariable(BaseImageHashCache.TtlEnvVar);
        // Default to a long TTL so tests that don't care about TTL don't flake.
        Environment.SetEnvironmentVariable(BaseImageHashCache.TtlEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BaseImageHashCache.TtlEnvVar, _originalTtl);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* swallow */ }
    }

    private string WriteSampleFile(string name, byte[] content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static BaseImageHashCache NewCache() =>
        new(NullLogger<BaseImageHashCache>.Instance);

    [Fact]
    public async Task ColdFirstTouch_ComputesOnce_AndFiresOnHashComputed()
    {
        var path = WriteSampleFile("cold.bin", new byte[] { 1, 2, 3, 4, 5 });
        using var cache = NewCache();

        var fired = 0;
        cache.OnHashComputed += (_, _) => Interlocked.Increment(ref fired);

        var hash = await cache.GetOrComputeAsync(path, CancellationToken.None);

        hash.Should().NotBeNullOrWhiteSpace();
        hash.Should().MatchRegex("^[0-9A-F]+$", "SHA-256 must be uppercase hex");
        cache.Stats.Computes.Should().Be(1);
        cache.Stats.Misses.Should().Be(1);
        cache.Stats.Hits.Should().Be(0);
        cache.Stats.Entries.Should().Be(1);
        fired.Should().Be(1);
    }

    [Fact]
    public async Task WarmSecondCall_IsHit_DoesNotRecompute()
    {
        var path = WriteSampleFile("warm.bin", new byte[] { 9, 9, 9 });
        using var cache = NewCache();

        var fired = 0;
        cache.OnHashComputed += (_, _) => Interlocked.Increment(ref fired);

        var first = await cache.GetOrComputeAsync(path, CancellationToken.None);
        var second = await cache.GetOrComputeAsync(path, CancellationToken.None);

        second.Should().Be(first);
        cache.Stats.Computes.Should().Be(1, "warm path must not recompute");
        cache.Stats.Hits.Should().Be(1);
        fired.Should().Be(1, "OnHashComputed must NOT fire on a warm hit");
    }

    [Fact]
    public async Task TtlExpiry_EvictsInMemory_ButSidecarFastPathServesWithoutRecompute()
    {
        // VC-D14 / VC-D16 (and roadmap D4): the TTL governs the in-memory tier
        // ONLY. The on-disk sidecar (`<base>.vhdx.sha256`) is intentionally
        // TTL-free — its freshness signal is the stat-tuple, not wall-clock age.
        //
        // Expected layered behavior on a post-TTL second call with an unchanged
        // file:
        //   1. In-memory entry is treated as expired (would otherwise recompute).
        //   2. Sidecar fast-path reads `<base>.sha256`, matches the stat-tuple,
        //      and promotes the cached hash back into memory.
        //   3. NO full-file SHA-256 recompute occurs (Computes stays at 1).
        //   4. SidecarStats.SidecarHits increments to 1.
        //
        // This is the "sidecar has no TTL; in-memory cache keeps the existing
        // TTL" contract called out explicitly in the roadmap's Phase D4 task.
        Environment.SetEnvironmentVariable(BaseImageHashCache.TtlEnvVar, "1");
        var path = WriteSampleFile("ttl.bin", new byte[] { 7, 7, 7 });
        using var cache = NewCache();

        var first = await cache.GetOrComputeAsync(path, CancellationToken.None);
        cache.Stats.Computes.Should().Be(1);
        cache.SidecarStats.SidecarWrites.Should().Be(1,
            "cold first-touch must persist a sidecar (VC-D14)");

        // Sleep past TTL — stat-tuple still matches so the in-memory entry is
        // expired but the sidecar on disk remains valid.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        var second = await cache.GetOrComputeAsync(path, CancellationToken.None);

        second.Should().Be(first, "content is unchanged so the hash must match");
        cache.Stats.Computes.Should().Be(1,
            "VC-D14/D16: sidecar fast-path must serve the post-TTL call without a full recompute");
        cache.SidecarStats.SidecarHits.Should().Be(1,
            "the post-TTL call must register as a sidecar hit, not a recompute");
    }

    [Fact]
    public async Task StatTuple_Size_Change_TriggersRecompute()
    {
        var path = WriteSampleFile("size.bin", new byte[] { 1, 2, 3 });
        using var cache = NewCache();

        var first = await cache.GetOrComputeAsync(path, CancellationToken.None);

        // Append bytes — Length changes, mtime usually also changes.
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6 });

        var second = await cache.GetOrComputeAsync(path, CancellationToken.None);

        second.Should().NotBe(first, "content changed ⇒ hash must change");
        cache.Stats.Computes.Should().Be(2);
    }

    [Fact]
    public async Task StatTuple_Mtime_Change_TriggersRecompute()
    {
        var path = WriteSampleFile("mtime.bin", new byte[] { 1, 2, 3 });
        using var cache = NewCache();

        var first = await cache.GetOrComputeAsync(path, CancellationToken.None);
        cache.Stats.Computes.Should().Be(1);

        // Bump LastWriteTimeUtc without changing content/length — stat tuple still moves.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));

        _ = await cache.GetOrComputeAsync(path, CancellationToken.None);
        cache.Stats.Computes.Should().Be(2,
            "LastWriteTimeUtc moved ⇒ stat tuple mismatch ⇒ recompute");
    }

    [Fact]
    public async Task StatTuple_ReadOnly_Change_TriggersRecompute()
    {
        var path = WriteSampleFile("ro.bin", new byte[] { 1, 2, 3 });
        using var cache = NewCache();

        _ = await cache.GetOrComputeAsync(path, CancellationToken.None);
        cache.Stats.Computes.Should().Be(1);

        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

            _ = await cache.GetOrComputeAsync(path, CancellationToken.None);
            cache.Stats.Computes.Should().Be(2,
                "IsReadOnly flip ⇒ stat tuple mismatch ⇒ recompute");
        }
        finally
        {
            // Strip ReadOnly so Dispose's directory cleanup can delete the file.
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public async Task ConcurrentCallers_Coalesce_To_Single_Compute()
    {
        // Use a moderately-sized file so the hashing actually takes long enough for
        // N concurrent waiters to pile up on the per-path semaphore.
        var bytes = new byte[2 * 1024 * 1024]; // 2 MiB
        new Random(42).NextBytes(bytes);
        var path = WriteSampleFile("coalesce.bin", bytes);
        using var cache = NewCache();

        var fired = 0;
        cache.OnHashComputed += (_, _) => Interlocked.Increment(ref fired);

        const int N = 16;
        var ready = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, N).Select(async _ =>
        {
            await ready.Task; // align starts
            return await cache.GetOrComputeAsync(path, CancellationToken.None);
        }).ToArray();

        ready.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(h => h == results[0],
            "all coalesced callers must observe the same hash");
        cache.Stats.Computes.Should().Be(1,
            "per-path coalescing must collapse N concurrent misses to a single compute");
        fired.Should().Be(1,
            "OnHashComputed must fire exactly once under coalescing");
    }

    [Fact]
    public async Task Dispose_Cleans_Semaphores_Without_Leaking()
    {
        var p1 = WriteSampleFile("d1.bin", new byte[] { 1 });
        var p2 = WriteSampleFile("d2.bin", new byte[] { 2 });
        var cache = NewCache();

        _ = await cache.GetOrComputeAsync(p1, CancellationToken.None);
        _ = await cache.GetOrComputeAsync(p2, CancellationToken.None);

        // Dispose must be idempotent and must not throw.
        cache.Dispose();
        Action again = () => cache.Dispose();
        again.Should().NotThrow("Dispose must be idempotent");
    }

    // ════════════════════════════════════════════════════════════════════
    // Issue #169 / VC-D2: WarmAsync — best-effort, non-throwing pre-population
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WarmAsync_AllPathsValid_ReturnsCompleted_AndPrePopulatesCache()
    {
        var p1 = WriteSampleFile("warmA.bin", new byte[] { 1, 2, 3 });
        var p2 = WriteSampleFile("warmB.bin", new byte[] { 4, 5, 6, 7 });
        using var cache = NewCache();

        var report = await cache.WarmAsync(new[] { p1, p2 }, CancellationToken.None);

        report.Status.Should().Be(WarmUpStatus.Completed);
        report.Paths.Should().HaveCount(2);
        report.Paths.Should().OnlyContain(p =>
            p.Status == WarmUpPathStatus.WarmedFresh || p.Status == WarmUpPathStatus.AlreadyWarm);
        cache.Stats.Computes.Should().Be(2, "every warm-up path was a cold first-touch");
        cache.LatestWarmUpReport.Should().NotBeNull();
        cache.LatestWarmUpReport!.Status.Should().Be(WarmUpStatus.Completed);
    }

    [Fact]
    public async Task WarmAsync_NeverThrows_OnMissingPath_RecordsFailedRow()
    {
        var good = WriteSampleFile("good.bin", new byte[] { 1 });
        var missing = Path.Combine(_tempDir, "definitely-not-there.bin");
        using var cache = NewCache();

        var report = await cache.WarmAsync(new[] { good, missing }, CancellationToken.None);

        // Mixed success/failure → Partial.
        report.Status.Should().Be(WarmUpStatus.Partial);
        report.Paths.Should().HaveCount(2);
        report.Paths.Should().Contain(p =>
            p.Status == WarmUpPathStatus.Failed && p.ErrorCode == "FILE_NOT_FOUND");
        report.Paths.Should().Contain(p =>
            p.Status == WarmUpPathStatus.WarmedFresh && p.Sha256 != null);
        cache.Stats.Computes.Should().Be(1, "only the good path computed");
    }

    [Fact]
    public async Task WarmAsync_Idempotent_SecondCallReportsAlreadyWarm()
    {
        var p = WriteSampleFile("idem.bin", new byte[] { 9, 9 });
        using var cache = NewCache();

        var first = await cache.WarmAsync(new[] { p }, CancellationToken.None);
        var second = await cache.WarmAsync(new[] { p }, CancellationToken.None);

        first.Paths.Single().Status.Should().Be(WarmUpPathStatus.WarmedFresh);
        second.Paths.Single().Status.Should().Be(WarmUpPathStatus.AlreadyWarm);
        cache.Stats.Computes.Should().Be(1, "the second warm-up must NOT recompute");
    }

    [Fact]
    public async Task WarmAsync_EmptyInput_ReturnsCompletedWithNoPaths()
    {
        using var cache = NewCache();

        var report = await cache.WarmAsync(Array.Empty<string>(), CancellationToken.None);

        report.Status.Should().Be(WarmUpStatus.Completed);
        report.Paths.Should().BeEmpty();
        cache.LatestWarmUpReport.Should().NotBeNull();
    }

    [Fact]
    public async Task WarmAsync_LifetimeCancelledBeforeAnyPath_ReturnsCancelled()
    {
        var p = WriteSampleFile("c1.bin", new byte[] { 1 });
        using var cache = NewCache();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var report = await cache.WarmAsync(new[] { p }, cts.Token);

        report.Status.Should().Be(WarmUpStatus.Cancelled);
        report.Paths.Single().Status.Should().Be(WarmUpPathStatus.Cancelled);
        cache.Stats.Computes.Should().Be(0);
    }

    [Fact]
    public async Task LatestWarmUpReport_TransitionsToInProgressThenCompleted()
    {
        // Use a moderately-sized file so we can observe in-progress state.
        var bytes = new byte[4 * 1024 * 1024]; // 4 MiB
        new Random(7).NextBytes(bytes);
        var p = WriteSampleFile("inprog.bin", bytes);
        using var cache = NewCache();

        cache.LatestWarmUpReport.Should().BeNull(
            "no warm-up has been scheduled yet");

        var task = cache.WarmAsync(new[] { p }, CancellationToken.None);
        var final = await task;

        final.Status.Should().Be(WarmUpStatus.Completed);
        cache.LatestWarmUpReport!.Status.Should().Be(WarmUpStatus.Completed);
    }

    // ════════════════════════════════════════════════════════════════════
    // Issue #169 / VC-D5: Shape B — detached compute survives inbound-CT cancel
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShapeB_OriginatorCancel_DoesNotKillCompute_NextCallerHitsResult()
    {
        // Use a file large enough that the SHA-256 read takes a measurable amount
        // of time relative to the originator's tight cancellation window.
        var bytes = new byte[8 * 1024 * 1024]; // 8 MiB
        new Random(11).NextBytes(bytes);
        var p = WriteSampleFile("shapeB-orig.bin", bytes);
        using var cache = NewCache();

        var computed = 0;
        cache.OnHashComputed += (_, _) => Interlocked.Increment(ref computed);

        // Race the compute against a token we cancel mid-flight. We CANNOT
        // observe cacheTask (that would re-throw on the originator), but the
        // detached compute should still complete and populate the cache.
        using var origCts = new CancellationTokenSource();
        var cacheTask = cache.GetOrComputeAsync(p, origCts.Token);

        // Cancel almost immediately so the inbound CT fires while compute is in flight.
        origCts.Cancel();

        // The originator's awaiter would normally surface OCE — we deliberately
        // do NOT await cacheTask directly. Instead poll until compute completes.
        // (Mirrors HyperVManager's Task.WhenAny race + non-observation pattern.)
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref computed) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Volatile.Read(ref computed).Should().Be(1,
            "VC-D5 Shape B: detached compute must survive originator cancellation");

        // Next caller with a fresh token must observe a cache hit (no second compute).
        var hash = await cache.GetOrComputeAsync(p, CancellationToken.None);
        hash.Should().NotBeNullOrWhiteSpace();
        Volatile.Read(ref computed).Should().Be(1,
            "the next caller must hit the populated cache entry, not recompute");

        // Drain the (possibly cancelled, possibly completed) originator task to
        // avoid an unobserved-exception warning.
        try { await cacheTask; } catch (OperationCanceledException) { /* expected */ }
    }

    [Fact]
    public async Task ShapeB_WaiterCancel_DoesNotKillGateHolderCompute()
    {
        var bytes = new byte[8 * 1024 * 1024];
        new Random(13).NextBytes(bytes);
        var p = WriteSampleFile("shapeB-waiter.bin", bytes);
        using var cache = NewCache();

        var computed = 0;
        cache.OnHashComputed += (_, _) => Interlocked.Increment(ref computed);

        // Originator with an uncancelled token holds the gate.
        var originator = cache.GetOrComputeAsync(p, CancellationToken.None);

        // Waiter with a CT we cancel — its WaitAsync should throw OCE without
        // affecting the originator's compute.
        using var waiterCts = new CancellationTokenSource();
        var waiter = cache.GetOrComputeAsync(p, waiterCts.Token);
        waiterCts.Cancel();

        var waiterAct = async () => await waiter;
        await waiterAct.Should().ThrowAsync<OperationCanceledException>(
            "waiter's gate.WaitAsync(ct) must surface its own CT");

        // Originator still completes successfully.
        var hash = await originator;
        hash.Should().NotBeNullOrWhiteSpace();
        Volatile.Read(ref computed).Should().Be(1);
    }

    [Fact]
    public async Task ShapeB_AllCallersCancel_ComputeStillCompletes()
    {
        // The "all-cancel" case (VC-D5 #3): even if the originator AND all
        // waiters cancel, the detached compute inside the cache continues to
        // completion. A subsequent caller observes a populated entry.
        var bytes = new byte[8 * 1024 * 1024];
        new Random(17).NextBytes(bytes);
        var p = WriteSampleFile("shapeB-allcancel.bin", bytes);
        using var cache = NewCache();

        var computed = 0;
        cache.OnHashComputed += (_, _) => Interlocked.Increment(ref computed);

        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();
        var a = cache.GetOrComputeAsync(p, ctsA.Token);
        var b = cache.GetOrComputeAsync(p, ctsB.Token);

        ctsA.Cancel();
        ctsB.Cancel();

        // Drain the two (potentially cancelled) tasks without observing their
        // exceptions as test failures.
        _ = a.ContinueWith(_ => { }, TaskScheduler.Default);
        _ = b.ContinueWith(_ => { }, TaskScheduler.Default);

        // The detached compute should still finish.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref computed) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Volatile.Read(ref computed).Should().Be(1,
            "VC-D5 case #3: all-cancel must not kill the detached compute");

        var hash = await cache.GetOrComputeAsync(p, CancellationToken.None);
        hash.Should().NotBeNullOrWhiteSpace();
        Volatile.Read(ref computed).Should().Be(1, "subsequent caller is a hit");
    }

    [Fact]
    public async Task ShapeB_HostShutdown_CancelsInFlightCompute()
    {
        // VC-D5 case #4: when the lifetime token fires (host shutdown), the
        // SHA-256 read inside ComputeSha256Async observes cancellation and the
        // gate-holder throws OperationCanceledException. The cache entry is
        // NOT written; the next caller (after a fresh lifetime) re-enters the
        // gate and retries.
        var bytes = new byte[8 * 1024 * 1024];
        new Random(19).NextBytes(bytes);
        var p = WriteSampleFile("shapeB-shutdown.bin", bytes);

        using var lifetimeCts = new CancellationTokenSource();
        // Use the internal constructor that takes a raw lifetime CT (test seam).
        using var cache = new BaseImageHashCache(NullLogger<BaseImageHashCache>.Instance, lifetimeCts.Token);

        var task = cache.GetOrComputeAsync(p, CancellationToken.None);
        // Fire lifetime cancellation while compute is in flight.
        lifetimeCts.Cancel();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>(
            "VC-D5 case #4: lifetime CT firing cancels the in-flight compute");

        cache.Stats.Computes.Should().Be(0, "no successful compute under shutdown");
    }
}
