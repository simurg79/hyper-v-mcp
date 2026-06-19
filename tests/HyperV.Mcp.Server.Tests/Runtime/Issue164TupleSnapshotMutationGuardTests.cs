using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #164 / ST-D6a regression coverage (Gate 6 loop-back, 🟡 #3).
///
/// Why this test class exists:
/// Most manager-level <c>vm_create</c> tests run with <c>baseImageHashCache: null</c>
/// (the back-compat null path), which bypasses the ST-D6a mutation guard entirely.
/// That gap allowed the Gate 5 reviewer to surface a 🔴 bug where the pre-create
/// and post-create FileInfo objects were both read lazily AFTER the PowerShell
/// pipeline ran — so both endpoints saw the SAME (post) filesystem state and the
/// tuple comparison ALWAYS returned "unchanged", silently skipping the SHA-256
/// recompute even when the base VHDX actually changed.
///
/// These tests close that gap by:
/// 1. Constructing a manager with a REAL <see cref="BaseImageHashCache"/>.
/// 2. Creating a real on-disk base VHDX file.
/// 3. Using a test seam — a custom <see cref="IPowerShellExecutor"/> whose
///    "primary" handler MUTATES the base VHDX file between the
///    <c>preStat</c> snapshot and the <c>postStat</c> comparison.
/// 4. Asserting the manager detects the mutation and throws
///    <see cref="VmCreateRollbackException"/> citing the mismatched hash.
///
/// If the tuple-snapshot bug regresses (i.e. both stats are read lazily), the
/// mutation goes undetected and these tests fail.
/// </summary>
[Trait("Category", "Runtime")]
public sealed class Issue164TupleSnapshotMutationGuardTests : IDisposable
{
    private const string LocalHostId = "local";
    private const string TestVmName = "issue164-snapshot-vm";

    private readonly string _tempDir;
    private readonly string _baseVhdx;
    private readonly string _storageRoot;

    public Issue164TupleSnapshotMutationGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "issue164-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _baseVhdx = Path.Combine(_tempDir, "base.vhdx");
        _storageRoot = Path.Combine(_tempDir, "VMs");
        // Seed with deterministic, non-trivial bytes so SHA-256 is well-defined.
        File.WriteAllBytes(_baseVhdx, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private ServerOptions BuildOptions() => new()
    {
        DefaultHostId = LocalHostId,
        Hosts = new Dictionary<string, HostProfile>
        {
            [LocalHostId] = new HostProfile
            {
                HostId = LocalHostId,
                ComputerName = "localhost",
                TrustPolicy = "local",
                BaseVhdxPath = _baseVhdx,
                StorageRoot = _storageRoot,
            },
        },
    };

    /// <summary>
    /// Test executor whose primary handler runs an arbitrary <c>onPrimary</c>
    /// hook against the real filesystem AFTER the manager has taken its
    /// pre-create snapshot but BEFORE the manager evaluates the post-create
    /// stat tuple. This is the seam that exercises the tuple-snapshot fix.
    /// </summary>
    private sealed class SeamedExecutor : IPowerShellExecutor
    {
        public Action? OnPrimary { get; set; }
        public string PrimaryStdout { get; set; } = string.Empty;
        public int CallCount { get; private set; }

        public Task<PowerShellResult> ExecuteAsync(string script, int timeoutSeconds = 300,
            CancellationToken ct = default, bool allowDump = true)
        {
            // Issue #203 / LF-D19 pre-create probe — return 'absent' so the
            // test progresses to the primary pipeline (which is the seam this
            // test is exercising).
            if (IsLfD19ProbeScript(script))
            {
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = "absent",
                    DurationMs = 1,
                });
            }

            CallCount++;
            if (CallCount == 1)
            {
                OnPrimary?.Invoke();
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = PrimaryStdout,
                    DurationMs = 1,
                });
            }
            // All subsequent calls (rollback / probes): no-op success.
            return Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "{\"removed\":[],\"failed\":[],\"residual\":[]}",
                DurationMs = 1,
            });
        }

        private static bool IsLfD19ProbeScript(string script) =>
            script.Contains("Get-VM", StringComparison.OrdinalIgnoreCase) &&
            script.Contains("'present'", StringComparison.OrdinalIgnoreCase) &&
            script.Contains("'absent'", StringComparison.OrdinalIgnoreCase) &&
            !script.Contains("probe-failed", StringComparison.OrdinalIgnoreCase) &&
            !script.Contains("New-VHD", StringComparison.OrdinalIgnoreCase) &&
            !script.Contains("New-VM", StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidVmInfoJson() => """
        {"Id":"12345678-1234-1234-1234-123456789abc","Name":"issue164-snapshot-vm","State":2,"ProcessorCount":2,"MemoryMB":4096,"UptimeSeconds":0}
        """;

    /// <summary>
    /// ST-D6a mutation guard — POSITIVE detection.
    ///
    /// Asserts the 🔴 tuple-snapshot fix: when the parent base VHDX is mutated
    /// BETWEEN the manager's pre-create snapshot and the post-create comparison,
    /// the eagerly-captured (Length, LastWriteTimeUtc, IsReadOnly) primitives
    /// MUST differ from the freshly-read post-create FileInfo, forcing a
    /// SHA-256 recompute via the cache. Because the recomputed hash differs
    /// from the pre-hash, the manager surfaces a <c>VmCreateRollbackException</c>
    /// citing the mutation.
    ///
    /// If the tuple-snapshot bug regresses (both endpoints read lazily and
    /// observe identical post-state), <c>tupleMoved</c> would be <c>false</c>,
    /// the guard would NOT fire, and the manager would return success — this
    /// test would then fail on the <c>ThrowAsync&lt;VmCreateRollbackException&gt;</c>
    /// assertion.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_BaseVhdxMutatedDuringCreate_DetectsViaTupleSnapshot()
    {
        var options = BuildOptions();
        var cache = new BaseImageHashCache(NullLogger<BaseImageHashCache>.Instance);

        var exec = new SeamedExecutor
        {
            PrimaryStdout = ValidVmInfoJson(),
            // Mutate the base VHDX file from inside the primary executor call —
            // i.e. AFTER the manager has snapshotted preStat but BEFORE it
            // re-reads postStat for comparison.
            OnPrimary = () =>
            {
                // Write different bytes AND bump the size so Length differs.
                File.WriteAllBytes(_baseVhdx, new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0xFF, 0xFF, 0xFF, 0xFF, 0xAA, 0xBB });
                // Also bump mtime explicitly in case the FS clock resolution
                // didn't tick between WriteAllBytes calls.
                File.SetLastWriteTimeUtc(_baseVhdx, DateTime.UtcNow.AddMinutes(5));
            },
        };

        var resolver = new HostResolver(options);
        var manager = new HyperVManager(
            exec, resolver, options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(),
            fileSystemProbe: null,
            baseImageHashCache: cache);

        Func<Task> act = async () => await manager.CreateVmAsync(
            LocalHostId, TestVmName, baseVhdxPath: _baseVhdx,
            cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<VmCreateRollbackException>(
            "the eagerly-snapshotted pre-create tuple must detect the in-flight base VHDX mutation and force a recompute that surfaces a hash mismatch");
        ex.Which.Message.Should().Contain("mutated",
            "the BASE_IMAGE_MUTATED guard message must be preserved verbatim");
        exec.CallCount.Should().BeGreaterThanOrEqualTo(2,
            "primary + rollback must both have executed");
    }

    /// <summary>
    /// ST-D6a mutation guard — NEGATIVE control.
    ///
    /// When the base VHDX is NOT mutated during create, the eagerly-snapshotted
    /// pre-tuple and the freshly-read post-tuple MUST compare equal, the
    /// mutation guard MUST NOT fire, and <see cref="HyperVManager.CreateVmAsync"/>
    /// MUST return the parsed <see cref="VmInfo"/> normally.
    ///
    /// This guards against the opposite regression: an over-eager guard that
    /// incorrectly reports "tuple moved" even on a stable filesystem.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_BaseVhdxUnchanged_PassesMutationGuard()
    {
        var options = BuildOptions();
        var cache = new BaseImageHashCache(NullLogger<BaseImageHashCache>.Instance);

        var exec = new SeamedExecutor
        {
            PrimaryStdout = ValidVmInfoJson(),
            OnPrimary = null, // do nothing — base VHDX stays put.
        };

        var resolver = new HostResolver(options);
        var manager = new HyperVManager(
            exec, resolver, options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(),
            fileSystemProbe: null,
            baseImageHashCache: cache);

        var info = await manager.CreateVmAsync(
            LocalHostId, TestVmName, baseVhdxPath: _baseVhdx,
            cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None);

        info.Should().NotBeNull();
        info.Name.Should().Be(TestVmName);
        exec.CallCount.Should().Be(1,
            "stable filesystem ⇒ no rollback path, only the primary call should execute");
    }
}
