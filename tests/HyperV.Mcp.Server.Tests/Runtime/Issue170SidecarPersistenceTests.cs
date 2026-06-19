using System.Text;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #170 / VC-D14..VC-D18: behavioral tests for the
/// <c>&lt;base&gt;.vhdx.sha256</c> sidecar persistence surface added to
/// <see cref="BaseImageHashCache"/>.
///
/// Per VC-D18 these tests exercise the contract via the public
/// <see cref="IBaseImageHashCache.GetOrComputeAsync"/> /
/// <see cref="IBaseImageHashCache.ForceRecomputeAsync"/> entry points plus
/// direct filesystem assertions on the sidecar file. Sidecar helpers
/// (<c>TryReadSidecar</c>/<c>TryWriteSidecar</c>/<c>TryDeleteSidecar</c>)
/// remain <c>private</c> implementation details — no visibility widening is
/// required for these scenarios.
///
/// Cases (VC-D18 enumeration):
///   1. SecondCreate_UsesSidecar_DoesNotRecompute
///   2. SidecarStatTupleMismatch_DiscardsAndRecomputes
///   3. CorruptSidecarJson_IsDiscarded_LogsWarning
///   4. MutationDetected_DeletesSidecar
///   5. SidecarWriteFailure_DoesNotFailVmCreate
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public sealed class Issue170SidecarPersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalTtl;

    public Issue170SidecarPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "hypervmcp-issue170-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalTtl = Environment.GetEnvironmentVariable(BaseImageHashCache.TtlEnvVar);
        Environment.SetEnvironmentVariable(BaseImageHashCache.TtlEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BaseImageHashCache.TtlEnvVar, _originalTtl);
        // Strip ReadOnly so cleanup can delete files set RO during the
        // SidecarWriteFailure simulation.
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* swallow */ }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* swallow */ }
    }

    private static BaseImageHashCache NewCache() =>
        new(NullLogger<BaseImageHashCache>.Instance);

    private string WriteSampleFile(string name, byte[] content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string SidecarPathFor(string basePath) => basePath + ".sha256";

    // ════════════════════════════════════════════════════════════════════
    // VC-D18 Case 1: warm restart hits the sidecar without recomputing.
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task SecondCreate_UsesSidecar_DoesNotRecompute()
    {
        var path = WriteSampleFile("case1.vhdx", new byte[] { 1, 2, 3, 4, 5 });

        // First lifecycle: cold compute → writes sidecar.
        string firstHash;
        using (var cache1 = NewCache())
        {
            firstHash = await cache1.GetOrComputeAsync(path, CancellationToken.None);
            cache1.Stats.Computes.Should().Be(1, "cold first touch always computes once");
            cache1.SidecarStats.SidecarWrites.Should().Be(1, "GetOrComputeAsync must persist sidecar after a cold compute");
        }

        File.Exists(SidecarPathFor(path)).Should().BeTrue("sidecar should survive cache disposal");

        // Second lifecycle (simulates server restart): same in-memory cache
        // is empty but the sidecar on disk should short-circuit the SHA read.
        using var cache2 = NewCache();
        var secondHash = await cache2.GetOrComputeAsync(path, CancellationToken.None);

        secondHash.Should().Be(firstHash, "sidecar must return the same hash as the cold compute");
        cache2.Stats.Computes.Should().Be(0, "VC-D14 fast-path must skip the full SHA-256 read");
        cache2.SidecarStats.SidecarHits.Should().Be(1, "sidecar hit counter must increment");
        cache2.Stats.Hits.Should().Be(1, "sidecar hit is promoted into the in-memory hit counter");
    }

    // ════════════════════════════════════════════════════════════════════
    // VC-D18 Case 2: stale stat-tuple → discard + recompute + fresh sidecar.
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task SidecarStatTupleMismatch_DiscardsAndRecomputes()
    {
        var path = WriteSampleFile("case2.vhdx", new byte[] { 9, 9, 9, 9 });

        // Seed the sidecar with the v1 content.
        string staleHash;
        using (var seed = NewCache())
        {
            staleHash = await seed.GetOrComputeAsync(path, CancellationToken.None);
        }
        File.Exists(SidecarPathFor(path)).Should().BeTrue();

        // Mutate the base file (changes size + mtime), invalidating the sidecar's stat tuple.
        File.WriteAllBytes(path, new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 });

        using var cache = NewCache();
        var newHash = await cache.GetOrComputeAsync(path, CancellationToken.None);

        newHash.Should().NotBe(staleHash, "content changed ⇒ hash must differ");
        cache.SidecarStats.SidecarDiscards.Should().BeGreaterOrEqualTo(1,
            "stat-tuple mismatch must increment sidecarDiscards (VC-D14)");
        cache.Stats.Computes.Should().Be(1, "discard must fall through to a full compute");
        cache.SidecarStats.SidecarWrites.Should().Be(1, "a fresh sidecar must be written after the recompute");

        // Confirm the on-disk sidecar now carries the new hash.
        var sidecarJson = File.ReadAllText(SidecarPathFor(path));
        sidecarJson.Should().Contain(newHash, "sidecar file content must reflect the freshly computed hash");
    }

    // ════════════════════════════════════════════════════════════════════
    // VC-D18 Case 3: corrupt JSON sidecar → treated as miss, warning, replaced.
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task CorruptSidecarJson_IsDiscarded_LogsWarning()
    {
        var path = WriteSampleFile("case3.vhdx", new byte[] { 4, 4, 4, 4 });

        // Plant an invalid-JSON sidecar before any cache touches the file.
        File.WriteAllText(SidecarPathFor(path), "{ this is not valid JSON ::: ");

        using var cache = NewCache();
        var hash = await cache.GetOrComputeAsync(path, CancellationToken.None);

        hash.Should().NotBeNullOrWhiteSpace();
        cache.Stats.Computes.Should().Be(1, "corrupt sidecar must fall through to a full compute");
        cache.SidecarStats.SidecarDiscards.Should().BeGreaterOrEqualTo(1,
            "corrupt JSON must increment sidecarDiscards (VC-D16)");
        cache.SidecarStats.SidecarWrites.Should().Be(1, "a fresh, valid sidecar must replace the corrupt one");

        // The replacement sidecar must be parseable as valid JSON containing the new hash.
        var replaced = File.ReadAllText(SidecarPathFor(path));
        Action parse = () => JsonDocument.Parse(replaced);
        parse.Should().NotThrow("the replacement sidecar must be valid JSON");
        replaced.Should().Contain(hash);
    }

    // ════════════════════════════════════════════════════════════════════
    // VC-D18 Case 4: RecordMutationDetected deletes sidecar + updates diag.
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task MutationDetected_DeletesSidecar()
    {
        var path = WriteSampleFile("case4.vhdx", new byte[] { 6, 6, 6, 6 });

        using var cache = NewCache();
        var expected = await cache.GetOrComputeAsync(path, CancellationToken.None);
        File.Exists(SidecarPathFor(path)).Should().BeTrue("warm path must have written the sidecar");

        const string vmName = "issue170-mutation-vm";
        const string actualSha = "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF";

        cache.RecordMutationDetected(path, vmName, expected, actualSha);

        File.Exists(SidecarPathFor(path)).Should().BeFalse(
            "VC-D15 (a): RecordMutationDetected must delete the stale sidecar");

        var snapshot = cache.SidecarStats;
        snapshot.LastMutationDetected.Should().NotBeNull(
            "VC-D15 (b): lastMutationDetected must be projected for vm_diag");
        snapshot.LastMutationDetected!.VmName.Should().Be(vmName);
        snapshot.LastMutationDetected!.ExpectedSha256.Should().Be(expected);
        snapshot.LastMutationDetected!.ActualSha256.Should().Be(actualSha);
        snapshot.LastMutationDetected!.BaseVhdxPath.Should().Be(Path.GetFullPath(path));
    }

    // ════════════════════════════════════════════════════════════════════
    // VC-D18 Case 5: sidecar write failure does NOT fail vm_create
    // (in-memory cache + returned hash remain correct; only a warning logs).
    //
    // Simulation: place a *directory* at the sidecar path so File.WriteAllText
    // on the .tmp neighbor + File.Move both fail. The compute succeeds, the
    // in-memory cache updates, but the sidecar write is suppressed.
    // ════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task SidecarWriteFailure_DoesNotFailVmCreate()
    {
        var path = WriteSampleFile("case5.vhdx", new byte[] { 2, 2, 2, 2, 2 });

        // Make the sidecar path itself a non-empty directory. File.WriteAllText
        // on "<path>.sha256.tmp" can succeed, but File.Move(...overwrite:true)
        // onto a directory raises an IOException — exercising the
        // TryWriteSidecar catch arm (VC-D17).
        var sidecarPath = SidecarPathFor(path);
        Directory.CreateDirectory(sidecarPath);
        File.WriteAllText(Path.Combine(sidecarPath, "blocker.txt"), "blocks File.Move overwrite");

        using var cache = NewCache();

        Func<Task> act = async () => await cache.GetOrComputeAsync(path, CancellationToken.None);
        await act.Should().NotThrowAsync("VC-D17: sidecar write failure must never bubble out of GetOrComputeAsync");

        var hash = await cache.GetOrComputeAsync(path, CancellationToken.None);
        hash.Should().NotBeNullOrWhiteSpace();

        // In-memory cache is populated → second call is a hit, not a recompute.
        cache.Stats.Computes.Should().Be(1, "compute itself must succeed despite sidecar write failure");
        cache.Stats.Hits.Should().BeGreaterOrEqualTo(1, "in-memory cache must serve the second call");

        // Sidecar write counter must NOT have advanced (write failed silently).
        cache.SidecarStats.SidecarWrites.Should().Be(0,
            "VC-D17: sidecar write failure must be swallowed and NOT counted as a write");
    }
}
