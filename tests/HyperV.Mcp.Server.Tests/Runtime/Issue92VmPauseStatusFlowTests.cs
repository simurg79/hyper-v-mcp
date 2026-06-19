using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #92 — vm_pause/vm_status regression guards.
///
/// Two distinct bugs were reported in
/// https://github.com/simurg79/hyper-v-mcp-server/issues/92:
///
///  1. <b>vm_pause performed Save-VM, not Suspend-VM</b>: the composed
///     PowerShell script for <see cref="HyperVManager.PauseVmAsync"/> invoked
///     <c>Save-VM</c> (which serializes guest state to disk and leaves the VM
///     in <c>Saving</c> for many seconds) instead of the documented
///     <c>Suspend-VM</c> (which freezes vCPUs in-memory, transitioning to
///     <c>Paused</c> within ~100 ms).
///  2. <b>vm_status returned a stale state mid-transition</b>: the reporter
///     observed <c>state=Saving</c> persisting for 15 s after a pause call,
///     suggesting either a cached last-known-state field shadowing live host
///     state, or the bug-1 Save-VM behavior masquerading as a status cache.
///     The Architect's Gate-1 verdict concluded the apparent "cache" was just
///     bug-1's slow Save-VM transition observed through a fresh Get-VM, and
///     no caching layer exists in <see cref="HyperVManager.GetVmStatusAsync"/>.
///
/// These tests pin both invariants so they cannot regress unnoticed:
///
///   • <see cref="PauseVmAsync_Script_InvokesSuspendVmNotSaveVm"/> captures
///     the composed script and asserts <c>Suspend-VM</c> is present and
///     <c>Save-VM</c> is absent. This is the bug-1 guard.
///   • <see cref="GetVmStatusAsync_AlwaysQueriesLiveHost_NoCache"/> calls
///     status twice with two different mock payloads and asserts the executor
///     is invoked twice AND the second result reflects the second payload —
///     proving no caching layer is shadowing live state. This is the bug-2
///     guard.
///
/// Pattern mirrors <see cref="Issue93OrphanClassifierTests"/>: script-capture
/// via Moq <c>Callback</c> on <see cref="IPowerShellExecutor.ExecuteAsync"/>.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue92VmPauseStatusFlowTests
{
    private const string LocalHostId = "local";
    // Issue #92: a fixed valid GUID is required because PauseVmAsync/GetVmStatusAsync
    // route through InputValidation.ValidateVmId which rejects non-GUID values.
    private const string TestVmId = "11111111-1111-1111-1111-111111111111";

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
        var manager = new HyperVManager(
            exec.Object,
            resolver,
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());
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

    /// <summary>
    /// Bug-1 regression guard: the script composed by <see cref="HyperVManager.PauseVmAsync"/>
    /// must invoke <c>Suspend-VM</c> (in-memory freeze, fast transition to Paused)
    /// and must NOT invoke <c>Save-VM</c> (which is the legacy bug from issue #92
    /// that left VMs stuck in <c>Saving</c> for many seconds and looked like a
    /// status caching bug to the reporter).
    ///
    /// We capture the composed script via Moq <c>Callback</c> rather than executing
    /// it (no live Hyper-V in unit tests). The mock returns a Paused VmInfo payload
    /// so the C# parser path also runs, but the assertion is purely on the script
    /// content. See https://github.com/simurg79/hyper-v-mcp-server/issues/92.
    /// </summary>
    [Fact]
    public async Task PauseVmAsync_Script_InvokesSuspendVmNotSaveVm()
    {
        var (manager, exec) = BuildManager();
        // Hyper-V State enum: 6 = Paused (mapped via VmStateMap in MapJsonToVmInfo).
        var pausedJson = $$"""
        {
          "Id": "{{TestVmId}}",
          "Name": "test-vm",
          "State": 6,
          "ProcessorCount": 2,
          "MemoryMB": 2048,
          "UptimeSeconds": 60
        }
        """;
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(pausedJson));

        var info = await manager.PauseVmAsync(LocalHostId, TestVmId);

        info.State.Should().Be("Paused",
            "the manager must surface the Hyper-V state-9 enum as the string 'Paused'.");

        capturedScript.Should().Contain("Suspend-VM",
            "vm_pause MUST compose a Suspend-VM call (in-memory freeze, fast transition). " +
            "Issue #92 bug-1: the original implementation used Save-VM, which serializes " +
            "guest state to disk and leaves the VM in 'Saving' for many seconds — visible " +
            "to clients as an apparent status-cache bug.");
        capturedScript.Should().NotContain("Save-VM",
            "vm_pause MUST NOT invoke Save-VM. Save-VM is the issue #92 bug-1 regression " +
            "trigger; reintroducing it will reproduce the multi-second 'Saving' window the " +
            "reporter observed.");
    }

    /// <summary>
    /// Bug-2 regression guard: <see cref="HyperVManager.GetVmStatusAsync"/> must
    /// always query the live host via a fresh <c>Get-VM</c> call and must not
    /// consult any in-process state cache, last-known-state field, or TTL.
    ///
    /// We invoke status twice in succession with the same VM id. The mock returns
    /// two DIFFERENT payloads (Running, then Paused). If a caching layer were
    /// silently shadowing the call, either:
    ///   - the executor would be invoked fewer than 2 times, or
    ///   - the second result would echo the FIRST payload (stale state).
    ///
    /// Both outcomes would have masked the bug-1 Save-VM transition window the
    /// reporter saw. See https://github.com/simurg79/hyper-v-mcp-server/issues/92.
    /// </summary>
    [Fact]
    public async Task GetVmStatusAsync_AlwaysQueriesLiveHost_NoCache()
    {
        var (manager, exec) = BuildManager();
        // Hyper-V State enum: 2 = Running, 6 = Paused.
        var runningJson = $$"""
        {
          "Id": "{{TestVmId}}",
          "Name": "test-vm",
          "State": 2,
          "ProcessorCount": 2,
          "MemoryMB": 2048,
          "UptimeSeconds": 30
        }
        """;
        var pausedJson = $$"""
        {
          "Id": "{{TestVmId}}",
          "Name": "test-vm",
          "State": 6,
          "ProcessorCount": 2,
          "MemoryMB": 2048,
          "UptimeSeconds": 60
        }
        """;

        // SequenceQueue: first call → Running, second call → Paused.
        // If a cache existed, the second call would either skip the executor
        // entirely or return the first (Running) payload again.
        var responses = new Queue<string>(new[] { runningJson, pausedJson });
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(() => SuccessResult(responses.Dequeue()));

        var first = await manager.GetVmStatusAsync(LocalHostId, TestVmId);
        var second = await manager.GetVmStatusAsync(LocalHostId, TestVmId);

        first.State.Should().Be("Running",
            "the first status call must reflect the first mock payload.");
        second.State.Should().Be("Paused",
            "the second status call must reflect the SECOND mock payload — proving no " +
            "caching layer is shadowing live host state. Issue #92 bug-2 guard.");

        exec.Verify(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()),
            Times.Exactly(2),
            "GetVmStatusAsync must hit the executor once per call. Any short-circuit " +
            "caching (call count < 2) would mean stale state can be served — the exact " +
            "failure mode reported in issue #92.");
    }
}
