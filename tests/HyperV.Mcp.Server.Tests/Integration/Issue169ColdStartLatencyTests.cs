using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// Issue #169 / VC-D10: cold-start integration tests for the
/// <see cref="BaseImageHashCache"/> pre-hash path that underpins
/// <c>vm_create</c>'s 60-second MCP RPC budget guarantee.
///
/// <para>
/// VC-D10 defines two contractual guarantees:
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Test 1 — warm-on-init unblocks cold-start.</b> After the server has
///   warmed the cache against a ≥1 GiB base VHDX, a subsequent <c>vm_create</c>
///   (modelled here as a direct <see cref="IBaseImageHashCache.GetOrComputeAsync"/>
///   call, which is the exact call <c>HyperVManager.CreateVmAsync</c> issues on
///   its pre-hash path) MUST return within 5 s. <c>Stats.Computes</c> MUST
///   remain at 1 (warm-up did the single hash) and <c>Stats.Hits</c> MUST be
///   ≥ 1 (the post-warm call hit the cache).
///   </description></item>
///   <item><description>
///   <b>Test 2 — VC-D5 Shape B: originator cancellation does not kill detached
///   compute.</b> A first caller is cancelled after 100 ms (well before a 1 GiB
///   SHA-256 can complete). A second caller, arriving for the same path within
///   the detached compute's lifetime, MUST observe a successful hash and the
///   <c>OnHashComputed</c> event MUST fire exactly once across both calls
///   (proving the second call did NOT re-compute — it consumed the result of
///   the detached first compute).
///   </description></item>
/// </list>
///
/// <para>
/// <b>Gating:</b> Both tests carry <c>[Trait("RunCostly","true")]</c>. They are
/// excluded from the default test run; enable them with
/// <c>dotnet test --filter "Trait=RunCostly"</c> (after setting the
/// <c>RunCostly=1</c> env var by convention; the filter alone is sufficient).
/// </para>
///
/// <para>
/// <b>VC-D10 deviation note:</b> The design doc anticipates booting the full
/// DI graph and dispatching <c>vm_create</c> end-to-end. Per the Gate 5 dispatch
/// instructions ("use a host-side validation flow that exercises the cache
/// pre-hash path without requiring real Hyper-V") we instead drive
/// <see cref="IBaseImageHashCache"/> directly. This isolates the exact contract
/// VC-D10 is policing (cache pre-hash latency + detached compute survival)
/// without dragging in PowerShell, the concurrency gate, or real Hyper-V
/// provisioning — all of which would add flake without strengthening the
/// guarantee. The <c>warmUpStatus == "completed"</c> condition is observed via
/// <see cref="IBaseImageHashCache.LatestWarmUpReport"/>, which is the same
/// property <c>vm_diag.baseImageHashCache.warmUpStatus</c> projects.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("RunCostly", "true")]
[SupportedOSPlatform("windows")]
public sealed class Issue169ColdStartLatencyTests
{
    /// <summary>VC-D10 latency budget for a warm-cache <c>vm_create</c>.</summary>
    private static readonly TimeSpan LatencyBudget = TimeSpan.FromSeconds(5);

    private readonly ITestOutputHelper _output;

    public Issue169ColdStartLatencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// VC-D10 Test 1 — Post-warm-up <c>vm_create</c> latency.
    ///
    /// <para>Flow:</para>
    /// <list type="number">
    ///   <item><description>Provision a ≥1 GiB synthetic VHDX (Hyper-V <c>New-VHD</c> when available, plain file otherwise).</description></item>
    ///   <item><description>Point <c>HYPERV_MCP_BASE_VHDX</c> at the fixture (for parity with VC-D10's env-var contract).</description></item>
    ///   <item><description>Attempt to evict the OS page cache via <see cref="ColdCachePrimer"/>.</description></item>
    ///   <item><description>Call <see cref="IBaseImageHashCache.WarmAsync"/> (the entry point <c>Program.cs</c>'s warm-on-init invokes).</description></item>
    ///   <item><description>Poll <see cref="IBaseImageHashCache.LatestWarmUpReport"/> until <c>Status == Completed</c> (max 60 s).</description></item>
    ///   <item><description>Time a <see cref="IBaseImageHashCache.GetOrComputeAsync"/> call — this is the exact pre-hash path <c>vm_create</c> takes inside <c>HyperVManager.CreateVmAsync</c>.</description></item>
    ///   <item><description>Assert latency &lt; 5 s, <c>Computes == 1</c>, <c>Hits ≥ 1</c>.</description></item>
    /// </list>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Cold_VmCreate_AfterWarmUpOnInit_ReturnsWithin5Seconds()
    {
        using var vhdx = new LargeVhdxFixture();
        _output.WriteLine($"VHDX: {vhdx.Path} (IsRealVhdx={vhdx.IsRealVhdx}, NewVhdDiagnostic={vhdx.NewVhdDiagnostic ?? "<none>"})");

        var originalEnv = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX");
        Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", vhdx.Path);
        try
        {
            var evicted = ColdCachePrimer.TryEvictPageCache(vhdx.Path);
            _output.WriteLine($"ColdCachePrimer.TryEvictPageCache → {evicted} " +
                (evicted ? "(throughput precondition satisfied)" : "(best-effort; latency budget remains primary criterion per VC-D10)"));

            var cache = new BaseImageHashCache(NullLogger<BaseImageHashCache>.Instance);

            // ── Warm-on-init ────────────────────────────────────────────────
            var warmSw = Stopwatch.StartNew();
            var warmReport = await cache.WarmAsync(new[] { vhdx.Path }, CancellationToken.None);
            warmSw.Stop();
            _output.WriteLine($"WarmAsync completed in {warmSw.ElapsedMilliseconds} ms, status={warmReport.Status}");

            warmReport.Status.Should().Be(WarmUpStatus.Completed,
                "VC-D7: warm-up of a single valid path must reach a Completed terminal state");

            // ── Poll diagnostic property — mirrors vm_diag.baseImageHashCache.warmUpStatus ──
            var pollDeadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < pollDeadline)
            {
                var snap = cache.LatestWarmUpReport;
                if (snap is not null && snap.Status == WarmUpStatus.Completed) break;
                await Task.Delay(50);
            }
            cache.LatestWarmUpReport!.Status.Should().Be(WarmUpStatus.Completed);

            var computesAfterWarm = cache.Stats.Computes;
            computesAfterWarm.Should().Be(1, "warm-up performed exactly one SHA-256 of the synthetic VHDX");

            // ── The cold vm_create equivalent: a post-warm hash lookup ──────
            var sw = Stopwatch.StartNew();
            var hash = await cache.GetOrComputeAsync(vhdx.Path, CancellationToken.None);
            sw.Stop();
            _output.WriteLine($"Post-warm GetOrComputeAsync returned in {sw.ElapsedMilliseconds} ms (budget={LatencyBudget.TotalMilliseconds} ms)");

            hash.Should().NotBeNullOrWhiteSpace();
            sw.Elapsed.Should().BeLessThan(LatencyBudget,
                "VC-D10: warm-cache vm_create pre-hash must complete inside the 5 s MCP latency budget");

            cache.Stats.Computes.Should().Be(1,
                "VC-D10: a warm-cache call must not trigger a re-compute");
            cache.Stats.Hits.Should().BeGreaterThanOrEqualTo(1,
                "VC-D10: the post-warm call must register as a cache hit");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", originalEnv);
        }
    }

    /// <summary>
    /// VC-D10 Test 2 / VC-D5 Shape B — Originator cancellation must NOT kill
    /// the detached SHA-256 compute. A second caller arriving while the
    /// detached compute is still running observes the same successful hash,
    /// and <see cref="IBaseImageHashCache.OnHashComputed"/> fires exactly once
    /// across both calls.
    ///
    /// <para>
    /// Implementation note: we drive the cache directly via
    /// <see cref="IBaseImageHashCache.GetOrComputeAsync"/> rather than through
    /// <c>vm_create</c>. Per VC-D5 Shape B the contract under test is purely
    /// the cache's responsibility; the dispatcher's race-the-CT behavior sits
    /// on top of it and is covered by existing unit tests.
    /// </para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task OriginatorCancellation_DoesNotKillDetachedCompute_SecondCallSucceeds()
    {
        using var vhdx = new LargeVhdxFixture();
        _output.WriteLine($"VHDX: {vhdx.Path} (IsRealVhdx={vhdx.IsRealVhdx})");

        // No warm-on-init: the first caller must trigger the cold compute.
        var cache = new BaseImageHashCache(NullLogger<BaseImageHashCache>.Instance);

        var fireCount = 0;
        cache.OnHashComputed += (_, _) => Interlocked.Increment(ref fireCount);

        // ── Caller #1: short-deadline cancellation ──────────────────────────
        // 100 ms is far below any plausible SHA-256 time for 1 GiB of bytes.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Exception? caller1Error = null;
        var caller1 = Task.Run(async () =>
        {
            try
            {
                await cache.GetOrComputeAsync(vhdx.Path, cts.Token);
            }
            catch (Exception ex)
            {
                caller1Error = ex;
            }
        });

        await caller1;
        _output.WriteLine($"Caller #1 outcome: {(caller1Error?.GetType().Name ?? "<no exception>")} " +
            $"(Stats: Computes={cache.Stats.Computes}, Misses={cache.Stats.Misses}, Hits={cache.Stats.Hits})");

        // VC-D5 Shape B: per the cache implementation, the inbound CT only
        // governs gate acquisition. For a cold first-touch the gate is
        // immediately acquired by caller #1 and the SHA-256 is then detached
        // from the inbound CT. Caller #1 therefore typically returns
        // successfully (it owns the compute) — its 100 ms CT is dropped at
        // the gate-handoff seam. Either outcome (clean return OR
        // OperationCanceledException from a CT fired before gate acquisition)
        // is contractually acceptable; what matters for the detached-compute
        // contract is caller #2.
        if (caller1Error is not null)
        {
            caller1Error.Should().BeAssignableTo<OperationCanceledException>(
                "Shape B: if caller #1 surfaces an error it MUST be a clean cancellation, not an IO failure");
        }

        // ── Caller #2: same path, up to 90 s for the result ────────────────
        var sw = Stopwatch.StartNew();
        string? secondHash = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        secondHash = await cache.GetOrComputeAsync(vhdx.Path, deadline.Token);
        sw.Stop();
        _output.WriteLine($"Caller #2 returned hash={secondHash} in {sw.ElapsedMilliseconds} ms; " +
            $"Stats: Computes={cache.Stats.Computes}, Misses={cache.Stats.Misses}, Hits={cache.Stats.Hits}, " +
            $"OnHashComputed fired={fireCount} time(s)");

        secondHash.Should().NotBeNullOrWhiteSpace(
            "VC-D5 Shape B: caller #2 must observe a successful hash even though caller #1 was cancelled");

        cache.Stats.Computes.Should().Be(1,
            "VC-D5 Shape B: the detached compute (started for caller #1) must satisfy caller #2 — exactly ONE compute total");

        fireCount.Should().Be(1,
            "VC-D5 Shape B: OnHashComputed must fire exactly once across both calls (proves the detached compute survived caller-1 cancellation)");
    }
}
