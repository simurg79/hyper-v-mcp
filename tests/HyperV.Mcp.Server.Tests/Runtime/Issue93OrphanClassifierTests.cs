using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #93 — orphan classifier (age-only, fail-closed on unknown-age).
///
/// Layering decision (documented per Gate-5 instructions):
/// The classification predicate itself runs <b>inside the PowerShell host</b>
/// (composed by <see cref="HyperVManager.CleanupOrphansAsync"/> at
/// <c>src/HyperV.Mcp.Server/Infrastructure/HyperVManager.cs:902</c>) and depends
/// on real <c>Get-VM</c> output, <c>$vm.Notes</c> regex matching, and
/// <c>[DateTimeOffset]::Parse</c>. We therefore split the contract verification
/// into two sides:
///
///  1. <b>Mapping / eligibility side (C#):</b> we mock <see cref="IPowerShellExecutor"/>
///     and feed it canned JSON that simulates the predicate's three buckets
///     (orphan / unknown-age / live-skipped / untagged-skipped). We then assert
///     <c>VmInfo.Reason</c> mapping (LF-D10) and that no extra destroy invocations
///     happen on the C# side — the executor is called <b>exactly once</b> with the
///     composed script.
///  2. <b>Predicate-authoring side (script content):</b> since the .NET test host
///     has no live Hyper-V, we capture the script string passed to
///     <c>IPowerShellExecutor.ExecuteAsync</c> and assert structural invariants
///     of LF-D10: age-only predicate (no power-state input), unknown-age branch
///     never enters the destroy block, and the <c>$dryRun</c> flag flips with the
///     argument. This proves the C# author of the script encodes the contract
///     correctly, even though we cannot execute the script here.
///
/// Plus scenario (e) verifies the wire-format invariant on <see cref="VmInfo.Reason"/>
/// (omitted when null, present otherwise) — a pure serialization unit test.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue93OrphanClassifierTests
{
    private const string LocalHostId = "local";

    private static (HyperVManager manager, Mock<IPowerShellExecutor> exec) BuildManager()
    {
        var exec = new Mock<IPowerShellExecutor>();
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
        var manager = new HyperVManager(exec.Object, resolver, options, NullLogger<HyperVManager>.Instance, new TestIsoInspector());
        return (manager, exec);
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

    // ─── (a) Off-state recently-tagged VM → not in response ─────────────

    /// <summary>
    /// Scenario (a): a VM that is Off but was tagged recently (within 24h) is
    /// classified as 'live' by the predicate and is therefore <b>never returned</b>.
    /// We simulate the predicate's output by returning an empty array — this is
    /// exactly what the script emits for the live bucket. The mapping side must
    /// surface zero rows.
    /// </summary>
    [Fact]
    public async Task CleanupOrphansAsync_OffStateRecentlyTagged_NotInResponse()
    {
        var (manager, exec) = BuildManager();
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult("[]"));

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().BeEmpty(
            "live (within-cutoff) tagged VMs — regardless of power state — must NOT be returned (LF-D10).");
        exec.Verify(e => e.ExecuteAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once,
            "the executor is invoked exactly once with the composed cleanup script.");

        // LF-D10 structural guarantee: the orphan classifier must NOT key on power
        // state. The pre-#93 buggy predicate filtered on `State -eq 'Off'`, which
        // produced the Sev1 false-positive on live VMs. This assertion ensures
        // any reintroduction of a power-state predicate fails the test suite.
        capturedScript.Should().NotContainAny(
            new[] { "State -eq 'Off'", "State -eq \"Off\"", "$_.State -eq" },
            "the age-only predicate (LF-D10) must not branch on VM power state.");
    }

    // ─── (b) Running, tagged, aged, NO role=ephemeral → needs-attention ─────

    /// <summary>
    /// Scenario (b), ratified #207 contract: a running, tagged VM that is aged
    /// past the 24h cutoff but carries only the legacy <c>created=</c> marker and
    /// NO valid <c>role=ephemeral</c> now classifies as <c>needs-attention</c> —
    /// reported but NEVER auto-destroyed (the TC-W14/TC-L13 donor case). This
    /// replaces the retired pre-#207 behavior that mapped an aged Running+tagged
    /// VM to <c>orphan</c> and deleted it. The #93 power-state-neutrality intent
    /// is preserved: the destroy gate must not branch on VM state, and the
    /// destroy branch is restricted to <c>orphan-candidate</c>.
    /// </summary>
    [Fact]
    public async Task CleanupOrphansAsync_RunningTaggedOldVm_NoEphemeralRole_MapsToNeedsAttention()
    {
        var (manager, exec) = BuildManager();
        var json = """
        [
          {
            "Id": "11111111-1111-1111-1111-111111111111",
            "Name": "old-running-vm",
            "State": 2,
            "ProcessorCount": 4,
            "MemoryMB": 4096,
            "UptimeSeconds": 100000,
            "Reason": "needs-attention"
          }
        ]
        """;
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(json));

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: false);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("old-running-vm");
        result[0].Reason.Should().Be("needs-attention",
            "aged + tagged but NO role=ephemeral must be report-only needs-attention (CO-D3, TC-W14).");

        // Predicate-authoring invariant: dryRun=false wires $false, and the
        // destroy gate is restricted to orphan-candidate (never needs-attention).
        capturedScript.Should().Contain("$dryRun = $false",
            "dryRun:false must flow into the script as the PS literal $false.");
        capturedScript.Should().Contain("$reason -eq 'orphan-candidate' -and -not $dryRun",
            "the destroy gate must be restricted to orphan-candidate (CO-D6).");
        capturedScript.Should().NotContain("$reason -eq 'needs-attention' -and -not $dryRun",
            "there must be NO destroy branch keyed on needs-attention.");
    }

    // ─── (c) Tagged VM with garbled timestamp → needs-attention, never destroyed

    /// <summary>
    /// Scenario (c): a tagged VM whose <c>Notes</c> creation timestamp cannot be
    /// parsed lands in the 'needs-attention' bucket. It is ALWAYS reported but NEVER
    /// auto-destroyed — even when <c>dryRun:false</c>. Run twice (dryRun:true and
    /// dryRun:false) and assert that:
    ///   - the row surfaces with <c>reason="needs-attention"</c> in both runs, and
    ///   - the script's destroy gate (<c>if ($reason -eq 'orphan-candidate' -and -not $dryRun)</c>)
    ///     specifically restricts destroy to the 'orphan-candidate' reason — i.e. the
    ///     needs-attention branch can never enter the Stop-VM/Remove-VM block.
    /// We cannot invoke Stop-VM/Remove-VM on the .NET side; instead we assert the
    /// predicate-authoring invariant in the script that makes destroy structurally
    /// impossible for needs-attention rows.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanupOrphansAsync_UnknownAge_AlwaysReported_NeverDestroyed(bool dryRun)
    {
        var (manager, exec) = BuildManager();
        var json = """
        [
          {
            "Id": "22222222-2222-2222-2222-222222222222",
            "Name": "garbled-notes-vm",
            "State": 2,
            "ProcessorCount": 2,
            "MemoryMB": 2048,
            "UptimeSeconds": 7200,
            "Reason": "needs-attention"
          }
        ]
        """;
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(json));

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: dryRun);

        result.Should().HaveCount(1,
            "needs-attention rows are ALWAYS reported regardless of dryRun (CO-D3 fail-closed).");
        result[0].Reason.Should().Be("needs-attention");

        // Predicate-authoring invariant: the destroy gate is restricted to
        // 'orphan-candidate' AND -not $dryRun, so needs-attention can NEVER enter
        // the destroy block, even with dryRun:false. This is the script-side proof
        // that Stop-VM/Remove-VM are not invocable for this row.
        capturedScript.Should().Contain("$reason -eq 'orphan-candidate' -and -not $dryRun",
            "destroy must be gated on reason=='orphan-candidate' so needs-attention can never be destroyed.");
        capturedScript.Should().NotContain("$reason -eq 'needs-attention' -and -not $dryRun",
            "there must be NO destroy branch keyed on 'needs-attention'.");

        var expectedFlag = dryRun ? "$dryRun = $true" : "$dryRun = $false";
        capturedScript.Should().Contain(expectedFlag,
            "the dryRun argument must be wired into the script as the matching PS literal.");
    }

    // ─── (d) Untagged VMs → not in response ─────────────────────────────

    /// <summary>
    /// Scenario (d): VMs with no <c>hyper-v-mcp:</c> tag are filtered out by the
    /// script <i>before</i> classification (the <c>Where-Object Notes -like
    /// '*hyper-v-mcp:*'</c> filter). The predicate therefore returns an empty
    /// list when nothing on the host is tagged — we simulate that and assert
    /// the C# side returns zero rows (no spurious mapping).
    ///
    /// We also assert the script contains the tag filter, proving the
    /// predicate-authoring side enforces this contract structurally.
    /// </summary>
    [Fact]
    public async Task CleanupOrphansAsync_UntaggedVms_NotInResponse()
    {
        var (manager, exec) = BuildManager();
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult("[]"));

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().BeEmpty("untagged VMs are filtered before classification.");
        capturedScript.Should().Contain("hyper-v-mcp:",
            "the script must filter to VMs whose Notes contain the hyper-v-mcp: tag.");
        capturedScript.Should().Contain("Where-Object",
            "the tag filter is applied via Where-Object on $allVms.");

        // LF-D10 regex-shape invariant: the created= capture must exclude both
        // whitespace and ';' so trailing tag segments (e.g. ';type=iso-install'
        // emitted by vm_os_install) don't get glued onto the timestamp capture
        // and force every iso-installed VM into 'needs-attention' forever.
        capturedScript.Should().Contain("(?:^|[\\s;])hyper-v-mcp:created=([^\\s;]+)",
            "the timestamp capture must exclude ';' so multi-segment tag notes parse correctly.");
    }

    // ─── (f) Tagged VM with NO 'created=' segment → needs-attention ─────

    /// <summary>
    /// Scenario (f): a VM whose <c>Notes</c> contain the <c>hyper-v-mcp:</c> tag
    /// but no <c>created=</c> segment at all (separate failure mode from a
    /// garbled timestamp). Per the ratified #207 contract the script must
    /// classify this row as <c>needs-attention</c> — reported but never
    /// auto-destroyed — rather than silently dropping it. We assert two things:
    ///   1. C# mapping side: when the predicate emits such a row the C# layer
    ///      surfaces it with <c>Reason="needs-attention"</c>.
    ///   2. Script-authoring side: the composed script's fail-closed else
    ///      branch assigns <c>$reason = 'needs-attention'</c>, structurally
    ///      guaranteeing the contract instead of falling through to silent-ignore.
    /// </summary>
    [Fact]
    public async Task CleanupOrphansAsync_TaggedWithoutCreatedSegment_MapsToNeedsAttention()
    {
        var (manager, exec) = BuildManager();
        var json = """
        [
          {
            "Id": "33333333-3333-3333-3333-333333333333",
            "Name": "tagged-no-created-vm",
            "State": 2,
            "ProcessorCount": 2,
            "MemoryMB": 2048,
            "UptimeSeconds": 3600,
            "Reason": "needs-attention"
          }
        ]
        """;
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(json));

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: false);

        result.Should().HaveCount(1,
            "tagged VMs missing the 'created=' segment must surface as needs-attention, not be silently dropped.");
        result[0].Reason.Should().Be("needs-attention");

        // Structural proof: the script's fail-closed else branch must assign
        // 'needs-attention' rather than fall through.
        capturedScript.Should().Contain("$reason = 'needs-attention'",
            "the script must fail closed to 'needs-attention' when the row is not a clean ephemeral+aged orphan.");
    }

    // ─── (e) VmInfo.Reason JSON serialization ───────────────────────────

    /// <summary>
    /// Scenario (e1): when <see cref="VmInfo.Reason"/> is <c>null</c> (the
    /// default for non-cleanup-orphans tools like <c>vm_list</c> and
    /// <c>vm_status</c>), the property must be OMITTED from the serialized
    /// JSON — guaranteed by <c>[JsonIgnore(WhenWritingNull)]</c>.
    /// </summary>
    [Fact]
    public void VmInfo_Reason_Null_IsOmittedFromJson()
    {
        var info = new VmInfo
        {
            VmId = "abc",
            Name = "n",
            State = "Running",
            HostId = LocalHostId,
            CpuCount = 2,
            MemoryMB = 1024,
            UptimeSeconds = 0,
            Reason = null,
        };

        var json = JsonSerializer.Serialize(info);

        json.Should().NotContain("\"reason\"",
            "Reason must be absent from the JSON envelope when null (vm_list/vm_status case).");
    }

    /// <summary>
    /// Scenario (e2): when <see cref="VmInfo.Reason"/> is set, it must serialize
    /// as the lowercase JSON property <c>"reason"</c> with the string value
    /// preserved — this is how cleanup-orphans rows convey their classification.
    /// </summary>
    [Theory]
    [InlineData("orphan-candidate")]
    [InlineData("needs-attention")]
    public void VmInfo_Reason_NonNull_SerializesAsLowercaseProperty(string reason)
    {
        var info = new VmInfo
        {
            VmId = "abc",
            Name = "n",
            State = "Running",
            HostId = LocalHostId,
            CpuCount = 2,
            MemoryMB = 1024,
            UptimeSeconds = 0,
            Reason = reason,
        };

        var json = JsonSerializer.Serialize(info);

        json.Should().Contain($"\"reason\":\"{reason}\"",
            "non-null Reason must emit as the lowercase 'reason' JSON property with its string value.");
    }
}
