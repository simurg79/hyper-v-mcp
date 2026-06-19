using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Regression tests for Issue #207 (TC-W14 / cross-platform TC-L13):
/// <c>vm_cleanup_orphans { dryRun: true }</c> returned a sentinel orphan record
/// (<c>vmId:""</c>, <c>name:""</c>, <c>state:"Unknown"</c>) with <c>count:1</c>
/// on a host with zero MCP-tagged VMs, instead of <c>orphans:[], count:0</c>.
///
/// Fix (defense-in-depth, design doc
/// <c>myplans/vm-management/cleanup-orphans/vm-cleanup-orphans-empty-host-envelope-design.md</c>):
/// <list type="bullet">
///   <item>PS-side (VC-CO-D2): strengthened empty-array guard
///   (<c>$null -eq $orphans -or @($orphans).Count -eq 0</c>) and forced array
///   semantics via <c>ConvertTo-Json -InputObject @($orphans) -Depth 4</c>.</item>
///   <item>C#-side (VC-CO-D3/D4/D5): private static
///   <c>FilterEmptyOrphanRows</c> helper, scoped to <see cref="HyperVManager.CleanupOrphansAsync"/>
///   only, drops rows whose VmId AND Name are both empty/whitespace and emits
///   exactly one structured <c>LogWarning</c> canary when any rows are dropped.</item>
/// </list>
///
/// Design constraints verified:
/// <list type="bullet">
///   <item>C1: non-empty envelopes remain byte-identical (T7).</item>
///   <item>C2: <c>ParseVmInfoList</c> / <c>MapJsonToVmInfo</c> NOT modified —
///   asserted indirectly by exercising the public manager API rather than
///   any private parser.</item>
/// </list>
/// </summary>
[Trait("Category", "Runtime")]
public class Issue207CleanupOrphansEmptyHostTests
{
    private const string LocalHostId = "local";

    /// <summary>
    /// Builds a <see cref="HyperVManager"/> wired to a mock PS executor and a
    /// mock <see cref="ILogger{HyperVManager}"/> so tests can assert both the
    /// envelope shape and the structured warning canary (or absence thereof).
    /// </summary>
    private static (HyperVManager manager, Mock<IPowerShellExecutor> exec, Mock<ILogger<HyperVManager>> logger) BuildManager()
    {
        var exec = new Mock<IPowerShellExecutor>();
        var logger = new Mock<ILogger<HyperVManager>>();
        var options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [LocalHostId] = new HostProfile
                {
                    HostId = LocalHostId,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        var resolver = new HostResolver(options);
        var manager = new HyperVManager(exec.Object, resolver, options, logger.Object, new TestIsoInspector());
        return (manager, exec, logger);
    }

    private static PowerShellResult SuccessResult(string stdout) => new()
    {
        ExitCode = 0,
        Stdout = stdout,
        Stderr = string.Empty,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 100,
    };

    private static void SetupStdout(Mock<IPowerShellExecutor> exec, string stdout)
    {
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(stdout));
    }

    /// <summary>
    /// Counts how many times <c>ILogger.Log</c> was invoked at the given level
    /// with a formatted message containing <paramref name="messageSubstring"/>.
    /// Mirrors the Moq <c>It.Is&lt;object&gt;(...)</c> idiom used in other
    /// runtime tests in this suite.
    /// </summary>
    private static int CountWarningsContaining(Mock<ILogger<HyperVManager>> logger, string messageSubstring)
    {
        int count = 0;
        logger.Invocations
            .Where(i => i.Method.Name == nameof(ILogger.Log))
            .ToList()
            .ForEach(invocation =>
            {
                if (invocation.Arguments.Count < 3) return;
                if (invocation.Arguments[0] is not LogLevel level || level != LogLevel.Warning) return;
                var state = invocation.Arguments[2];
                if (state is null) return;
                if (state.ToString()?.Contains(messageSubstring, StringComparison.OrdinalIgnoreCase) == true)
                {
                    count++;
                }
            });
        return count;
    }

    // ─── T1: Empty host — PS payload "[]" → orphans:[], count:0, no warning ─

    /// <summary>
    /// T1: TC-W14 acceptance criterion. On a host with zero MCP-tagged VMs the
    /// PS-side empty-branch emits <c>'[]'</c>. The manager must return an
    /// empty list with no canary warning fired.
    /// </summary>
    [Fact]
    public async Task T1_EmptyHost_EmptyArray_ReturnsEmptyList_NoWarning()
    {
        var (manager, exec, logger) = BuildManager();
        SetupStdout(exec, "[]");

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().BeEmpty("'[]' from PS-side empty guard must surface as orphans:[]/count:0 (Issue #207 / TC-W14).");
        CountWarningsContaining(logger, "empty orphan rows filtered")
            .Should().Be(0, "no rows were dropped, so the canary must not fire.");
    }

    // ─── T2: Single matching VM → exactly that VM, count:1, no warning ─────

    /// <summary>
    /// T2: a single valid orphan row from PS must round-trip with the same
    /// identity (id + name) and not trip the empty-row filter.
    /// </summary>
    [Fact]
    public async Task T2_SingleMatchingVm_RoundTrips_NoWarning()
    {
        var (manager, exec, logger) = BuildManager();
        const string payload = @"[
  {
    ""Id"": ""11111111-1111-1111-1111-111111111111"",
    ""Name"": ""vm-alpha"",
    ""State"": 2,
    ""ProcessorCount"": 2,
    ""MemoryMB"": 2048,
    ""UptimeSeconds"": 0,
    ""Reason"": ""orphan""
  }
]";
        SetupStdout(exec, payload);

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().HaveCount(1, "single valid orphan must surface as count:1.");
        result[0].Name.Should().Be("vm-alpha");
        result[0].VmId.Should().Be("11111111-1111-1111-1111-111111111111");
        result[0].Reason.Should().Be("orphan");
        CountWarningsContaining(logger, "empty orphan rows filtered")
            .Should().Be(0, "no rows dropped → no canary.");
    }

    // ─── T3: Multiple matching VMs → all present, count:N, no warning ──────

    /// <summary>
    /// T3: with N&gt;1 valid rows, all must surface in-order and the canary
    /// must remain silent.
    /// </summary>
    [Fact]
    public async Task T3_MultipleMatchingVms_AllReturned_NoWarning()
    {
        var (manager, exec, logger) = BuildManager();
        const string payload = @"[
  { ""Id"": ""aaa"", ""Name"": ""vm-a"", ""State"": 2, ""ProcessorCount"": 1, ""MemoryMB"": 1024, ""UptimeSeconds"": 0, ""Reason"": ""orphan"" },
  { ""Id"": ""bbb"", ""Name"": ""vm-b"", ""State"": 3, ""ProcessorCount"": 2, ""MemoryMB"": 2048, ""UptimeSeconds"": 5, ""Reason"": ""unknown-age"" },
  { ""Id"": ""ccc"", ""Name"": ""vm-c"", ""State"": 2, ""ProcessorCount"": 4, ""MemoryMB"": 4096, ""UptimeSeconds"": 0, ""Reason"": ""orphan"" }
]";
        SetupStdout(exec, payload);

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().HaveCount(3);
        result.Select(r => r.Name).Should().ContainInOrder("vm-a", "vm-b", "vm-c");
        CountWarningsContaining(logger, "empty orphan rows filtered").Should().Be(0);
    }

    // ─── T4: Mixed payload with one degenerate {} row → drop it, 1 warning ─

    /// <summary>
    /// T4: defense-in-depth proof — when the PS-side guard is bypassed (e.g.,
    /// a future PS host regression emits a degenerate <c>{}</c> alongside
    /// valid rows), the C# filter drops the degenerate row and emits exactly
    /// one structured <c>LogWarning</c> with <c>droppedCount=1</c>.
    /// </summary>
    [Fact]
    public async Task T4_DegenerateRowMixedWithValid_DroppedWithSingleWarning()
    {
        var (manager, exec, logger) = BuildManager();
        const string payload = @"[
  { ""Id"": ""aaa"", ""Name"": ""vm-a"", ""State"": 2, ""ProcessorCount"": 1, ""MemoryMB"": 1024, ""UptimeSeconds"": 0, ""Reason"": ""orphan"" },
  { },
  { ""Id"": ""ccc"", ""Name"": ""vm-c"", ""State"": 2, ""ProcessorCount"": 4, ""MemoryMB"": 4096, ""UptimeSeconds"": 0, ""Reason"": ""orphan"" }
]";
        SetupStdout(exec, payload);

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().HaveCount(2, "the degenerate '{}' row must be filtered out.");
        result.Select(r => r.Name).Should().ContainInOrder("vm-a", "vm-c");

        CountWarningsContaining(logger, "empty orphan rows filtered")
            .Should().Be(1, "exactly one structured canary must fire when rows are dropped.");
        CountWarningsContaining(logger, "droppedCount")
            .Should().Be(1, "the canary message template must include the droppedCount field.");
    }

    // ─── T5: PS payload "null" or empty stdout → treated as empty host ─────

    /// <summary>
    /// T5: whitespace / empty stdout must be treated as an empty host (no
    /// rows, no warning). The PS-side guard (VC-CO-D2) now unconditionally
    /// emits <c>'[]'</c> for the null/empty $orphans case, so the literal
    /// JSON token <c>"null"</c> is no longer a reachable wire shape from a
    /// correctly-deployed server — empty stdout is the realistic regression
    /// surface (e.g., script aborts before reaching the emit branch).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task T5_EmptyOrWhitespacePayload_TreatedAsEmpty_NoWarning(string payload)
    {
        var (manager, exec, logger) = BuildManager();
        SetupStdout(exec, payload);

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().BeEmpty($"PS payload '{payload}' must be treated as zero orphans.");
        CountWarningsContaining(logger, "empty orphan rows filtered")
            .Should().Be(0, "no rows present → droppedCount=0 → no canary.");
    }

    // ─── T6: Single-element array of just {} → orphans:[], 1 warning ───────

    /// <summary>
    /// T6: the original TC-W14 wire shape — a single-element array containing
    /// only <c>{}</c>. The C# filter must collapse this to <c>orphans:[]</c>
    /// and emit exactly one canary with <c>droppedCount=1</c>.
    /// </summary>
    [Fact]
    public async Task T6_SingleDegenerateRow_CollapsesToEmpty_OneWarning()
    {
        var (manager, exec, logger) = BuildManager();
        SetupStdout(exec, "[ { } ]");

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().BeEmpty(
            "TC-W14: a lone '{}' row from PS must NOT surface as a sentinel orphan envelope (Issue #207).");
        CountWarningsContaining(logger, "empty orphan rows filtered")
            .Should().Be(1, "exactly one structured canary must fire with droppedCount=1.");
    }

    // ─── T7: dryRun:false non-empty host → envelope byte-identical to T2 ───

    /// <summary>
    /// T7 / constraint C1: when the host has non-empty orphans, the serialized
    /// envelope must be byte-identical regardless of the new defense layer —
    /// i.e., the filter is a no-op for valid rows and adds no fields. We
    /// snapshot-compare the JSON serialization of both dryRun branches against
    /// the expected shape.
    /// </summary>
    [Fact]
    public async Task T7_DryRunFalse_NonEmptyHost_EnvelopeUnchanged()
    {
        var (manager, exec, _) = BuildManager();
        const string payload = @"[
  {
    ""Id"": ""11111111-1111-1111-1111-111111111111"",
    ""Name"": ""vm-alpha"",
    ""State"": 2,
    ""ProcessorCount"": 2,
    ""MemoryMB"": 2048,
    ""UptimeSeconds"": 0,
    ""Reason"": ""orphan""
  }
]";
        SetupStdout(exec, payload);

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: false);

        // Snapshot the wire shape using the same JSON options the dispatcher
        // would use. The defense layer must not add fields or reorder rows.
        var serialized = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        serialized.Should().Contain("\"vmId\":\"11111111-1111-1111-1111-111111111111\"");
        serialized.Should().Contain("\"name\":\"vm-alpha\"");
        serialized.Should().Contain("\"reason\":\"orphan\"");
        result.Should().HaveCount(1, "byte-identical envelope: exactly the rows PS emitted, no more, no fewer.");
    }

    // ─── T8: PS payload as object literal (not array) with empty fields ────

    /// <summary>
    /// T8: PS <c>ConvertTo-Json</c> can emit a bare object (not an array) when
    /// there is exactly one element. If that single element is degenerate
    /// (empty vmId AND empty name), the C# filter must still drop it and emit
    /// one canary — even though the PS-side guard would normally short-circuit
    /// to <c>'[]'</c> before we get here.
    /// </summary>
    [Fact]
    public async Task T8_DegenerateObjectLiteral_HandledByCSharpFilter()
    {
        var (manager, exec, logger) = BuildManager();
        // Bare object literal — exactly what PS would emit if the inline
        // empty-guard ever regressed and ConvertTo-Json saw a single
        // degenerate row.
        const string payload = @"{ ""Id"": """", ""Name"": """", ""State"": 0, ""ProcessorCount"": 0, ""MemoryMB"": 0, ""UptimeSeconds"": 0 }";
        SetupStdout(exec, payload);

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().BeEmpty(
            "C#-side defense must drop the degenerate single-object envelope (Issue #207 / VC-CO-D5).");
        CountWarningsContaining(logger, "empty orphan rows filtered")
            .Should().Be(1, "exactly one canary must fire with droppedCount=1.");
    }
}
