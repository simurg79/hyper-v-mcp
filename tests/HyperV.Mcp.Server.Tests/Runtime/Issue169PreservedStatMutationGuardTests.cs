using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #169 / VC-D6 / VC-D8 / ADR-4 — regression guard for the
/// "preserved-stat mutation" attack class (Rows 1, 4, 5 of the
/// <c>vm-create-unblocking-design.md</c> §ADR-4 Threat Model:
/// sparse in-place overwrite, <c>touch -t</c> mtime-reset, silent storage-layer
/// bit-flip).
///
/// <para>
/// These tests exist to lock the behavioral contract that Gate 6 finding #1
/// surfaced and Option D′ hybrid (amended VC-D6 + VC-D8) closed: the cached
/// SHA-256 must NOT be re-served as the post-hash on the default
/// (<c>verifyBaseImageHash:true</c>) path. Concretely they prove:
/// </para>
/// <list type="number">
///   <item><description><c>BaseImageHashCache.ForceRecomputeAsync</c> bypasses
///   the stat-tuple short-circuit and re-reads the on-disk bytes — so a
///   preserved-stat mutation (size + mtime + IsReadOnly all unchanged) IS
///   detected as a hash mismatch.</description></item>
///   <item><description>The opt-out (<c>verifyBaseImageHash:false</c>) code
///   path — modeled here by a plain <c>GetOrComputeAsync</c> follow-up call
///   that hits the cached entry — does NOT detect the same mutation. This is
///   the explicit ADR-4 trade-off the operator accepts when opting out and
///   serves as living documentation that future refactors must not silently
///   widen.</description></item>
/// </list>
///
/// <para>
/// The tests target the cache's post-hash-recompute primitive directly rather
/// than driving the full <c>vm_create</c> dispatch path because the latter
/// requires Hyper-V / PowerShell on the test host. The hash-mismatch detection
/// logic that <c>HyperVManager.CreateVmAsync</c> performs is a thin
/// <c>!string.Equals(preHash, postHash)</c> check around the same primitive,
/// so this seam is the right place to enforce the contract.
/// </para>
///
/// <para>
/// Files used here are deliberately small (a few MiB) — the test exercises
/// hash-detection semantics, not VHDX structure or cold-I/O latency. No
/// <c>RunCostly</c> trait gate is needed.
/// </para>
/// </summary>
[Trait("Category", "Runtime")]
public sealed class Issue169PreservedStatMutationGuardTests : IDisposable
{
    private readonly string _tempDir;

    public Issue169PreservedStatMutationGuardTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "issue169-preserved-stat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* swallow */ }
    }

    private static BaseImageHashCache NewCache() =>
        new(NullLogger<BaseImageHashCache>.Instance);

    /// <summary>
    /// Writes <paramref name="content"/> to a fresh file and returns its path.
    /// The size is fixed at the content's byte count so we can later overwrite
    /// in-place with a different payload of the same length to simulate a
    /// preserved-stat mutation.
    /// </summary>
    private string WriteSampleFile(string name, byte[] content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Overwrites the file with new bytes of identical length, then restores
    /// the original LastWriteTimeUtc and IsReadOnly attribute so the
    /// <c>(Length, LastWriteTimeUtc, IsReadOnly)</c> stat tuple is unchanged.
    /// This is exactly the mutation pattern the cache's stat-tuple short-circuit
    /// cannot detect — and which Gate 6 finding #1 demonstrated would defeat
    /// ST-D6 if the cached pre-hash were re-served as the post-hash.
    /// </summary>
    private static void MutatePreservingStat(string path, byte[] newContent)
    {
        var fi = new FileInfo(path);
        var originalMtime = fi.LastWriteTimeUtc;
        var originalReadOnly = fi.IsReadOnly;
        if (originalReadOnly)
        {
            // Need write access to overwrite; we restore the flag below.
            fi.IsReadOnly = false;
        }
        if (newContent.Length != fi.Length)
        {
            throw new InvalidOperationException(
                $"Test setup error: mutation must preserve length (was {fi.Length}, new {newContent.Length}).");
        }

        // In-place overwrite — File.WriteAllBytes will truncate-then-write,
        // which preserves length when sizes match.
        File.WriteAllBytes(path, newContent);

        // Restore mtime + IsReadOnly to make this a "preserved-stat" mutation.
        File.SetLastWriteTimeUtc(path, originalMtime);
        if (originalReadOnly)
        {
            new FileInfo(path).IsReadOnly = true;
        }
    }

    /// <summary>
    /// Test 1 — Default <c>verifyBaseImageHash:true</c> path: a preserved-stat
    /// mutation MUST be detected by the unconditional post-hash recompute that
    /// <see cref="IBaseImageHashCache.ForceRecomputeAsync"/> performs (VC-D8).
    /// The pre-hash and post-hash differ, which is the condition
    /// <c>HyperVManager.CreateVmAsync</c> escalates as <c>BASE_IMAGE_MUTATED</c>.
    /// </summary>
    [Fact]
    public async Task Default_PreservedStatMutation_DetectedByForcedPostHashRecompute()
    {
        // Arrange — small synthetic base file (4 MiB).
        const int sizeBytes = 4 * 1024 * 1024;
        var originalContent = new byte[sizeBytes];
        new Random(169).NextBytes(originalContent);
        var path = WriteSampleFile("base-default.bin", originalContent);

        using var cache = NewCache();

        // Pre-hash via the warm-on-init / ST-D6a path. This is what
        // HyperVManager.CreateVmAsync stores as `preHash` when
        // verifyBaseImageHash:true.
        var preHash = await cache.GetOrComputeAsync(path, CancellationToken.None);
        preHash.Should().NotBeNullOrEmpty();

        // Mutate the file's content while preserving (Length, mtime, IsReadOnly).
        // Stat-tuple-only checks would NOT detect this; only a fresh SHA-256
        // re-read of the bytes can.
        var mutatedContent = new byte[sizeBytes];
        new Random(4242).NextBytes(mutatedContent);
        MutatePreservingStat(path, mutatedContent);

        // Sanity-check the stat tuple really is unchanged from the cache's
        // perspective: a follow-up GetOrComputeAsync would happily return the
        // cached preHash because MatchesStat succeeds. (This is the exact bug
        // Gate 6 finding #1 described.)
        var staleHashFromCachedPath = await cache.GetOrComputeAsync(path, CancellationToken.None);
        staleHashFromCachedPath.Should().Be(preHash,
            "this confirms the preserved-stat mutation defeats the cache's cheap-stat short-circuit; " +
            "ForceRecomputeAsync must therefore bypass that short-circuit");

        // Act — the VC-D8 contract: post-hash MUST be force-recomputed from
        // disk. ForceRecomputeAsync ignores the stat-tuple hit and re-reads
        // the bytes.
        var postHash = await cache.ForceRecomputeAsync(path, CancellationToken.None);

        // Assert — pre vs. post differ, which is exactly the condition
        // CreateVmAsync escalates as BASE_IMAGE_MUTATED.
        postHash.Should().NotBeNullOrEmpty();
        postHash.Should().NotBe(preHash,
            "VC-D8 / ADR-4: a preserved-stat mutation on the default verifyBaseImageHash:true " +
            "path MUST be detected via the unconditional post-hash recompute (Rows 1/4/5 of the " +
            "§ADR-4 Threat Model). If this assertion fails, the cache has regressed to " +
            "re-serving the stored pre-hash as the post-hash and ST-D6 / Issue #23 is broken.");

        // Documentation assertion: mismatch → BASE_IMAGE_MUTATED is the
        // dispatch contract (HyperVManager.CreateVmAsync surfaces an
        // InvalidOperationException whose message contains "BASE_IMAGE_MUTATED").
        // We assert the equality predicate here so future readers can grep for
        // this contract from the test file.
        string.Equals(preHash, postHash, StringComparison.OrdinalIgnoreCase)
            .Should().BeFalse("pre != post is the BASE_IMAGE_MUTATED escalation predicate");
    }

    /// <summary>
    /// Test 2 — Opt-out <c>verifyBaseImageHash:false</c> path: a preserved-stat
    /// mutation is NOT detected, because no SHA-256 work is performed at all.
    /// We model this here by skipping <see cref="IBaseImageHashCache.ForceRecomputeAsync"/>
    /// entirely (which is what <c>HyperVManager.CreateVmAsync</c> does when
    /// <c>verifyBaseImageHash:false</c>) and observing that the cached pre-hash
    /// would be the only signal available — and it has not moved despite the
    /// mutation.
    ///
    /// This test exists primarily as <em>living documentation</em> of the
    /// VC-D6 opt-out contract: future refactors that quietly re-introduce a
    /// post-hash recompute on the opt-out path (turning the ADR-4 trade-off
    /// into a contract violation) will fail this assertion.
    /// </summary>
    [Fact]
    public async Task OptOut_PreservedStatMutation_NotDetected_ReadOnlyOnlyGuard()
    {
        // Arrange — same setup as Test 1.
        const int sizeBytes = 4 * 1024 * 1024;
        var originalContent = new byte[sizeBytes];
        new Random(169).NextBytes(originalContent);
        var path = WriteSampleFile("base-optout.bin", originalContent);

        using var cache = NewCache();

        // On the opt-out path HyperVManager.CreateVmAsync does NOT call
        // GetOrComputeAsync at all (no preHash captured) and does NOT call
        // ForceRecomputeAsync afterward. The mutation guard collapses to the
        // ReadOnly-attribute check inside the PowerShell BuildBaseVhdxGuardScript.
        //
        // To make the contract explicit we still take a pre-hash here for
        // comparison purposes (so the test can assert the stat-tuple-only view
        // sees no change). In a real opt-out vm_create call this preHash would
        // never be read.
        var preHash = await cache.GetOrComputeAsync(path, CancellationToken.None);

        // Mutate while preserving (Length, mtime, IsReadOnly).
        var mutatedContent = new byte[sizeBytes];
        new Random(4242).NextBytes(mutatedContent);
        MutatePreservingStat(path, mutatedContent);

        // Act — emulate the opt-out path: the only "guard" is the cached
        // stat-tuple-driven cache lookup. We deliberately do NOT call
        // ForceRecomputeAsync.
        var observedHashWithoutRecompute = await cache.GetOrComputeAsync(path, CancellationToken.None);

        // Assert — stat-tuple-only inspection sees no mutation, so the cache
        // returns the stored pre-hash. There is NO BASE_IMAGE_MUTATED signal
        // available on the opt-out path. This is the documented ADR-4 trade-off.
        observedHashWithoutRecompute.Should().Be(preHash,
            "VC-D6 opt-out (verifyBaseImageHash:false): the SHA-256 mutation guard is bypassed; " +
            "preserved-stat mutations (Rows 1/4/5 of the §ADR-4 Threat Model) go undetected. " +
            "If this assertion fails, the opt-out path has silently grown a SHA-256 recompute and " +
            "the operator-facing contract documented on the vm_create tool description is wrong.");

        // Cross-check that the underlying file ACTUALLY changed — this protects
        // the test from a false-pass where MutatePreservingStat silently no-op'd.
        var freshHashViaForceRecompute = await cache.ForceRecomputeAsync(path, CancellationToken.None);
        freshHashViaForceRecompute.Should().NotBe(preHash,
            "sanity check: the file's bytes really did change between pre-hash capture and now; " +
            "if this fails the test setup didn't actually mutate the file and Test 2's assertion above is meaningless");
    }
}
