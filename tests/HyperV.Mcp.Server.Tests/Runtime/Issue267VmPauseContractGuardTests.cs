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
/// Issue #267 (SEV-2) — vm_pause returned "Saving"/"Saved" instead of "Paused".
///
/// Root cause: the pre-fix path used a save-to-disk verb (Suspend-VM/Save-VM) whose terminal is
/// "Saved" (5) — polling never reaches in-memory "Paused" (6). Prior tests fed a mocked
/// <see cref="IPowerShellExecutor"/> an already-settled State:6 payload and asserted the mock,
/// not real cmdlet semantics; that blind spot let #267 ship.
///
/// This suite closes the gap without a live host along three axes:
///
///   (1) Contract/stop-set guard — the composed pause script invokes RequestStateChange(9 = Quiesce),
///       does NOT contain Suspend-VM/Save-VM, and accepts "Paused" (not "Saved") as the success
///       terminal. Settle constants (10 sec, 250 ms), the [uint16]9 literal, and accepted CIM return
///       values (0, 4096) are pinned. The requested-state literal was corrected from 32776 (a
///       return-value code, rejected with 32775 on every guest) to 9 by issue #291.
///
///   (2) Manager-side failure classification — a mocked executor returning a success payload whose
///       state is NOT "Paused" yields success:false / COMMAND_FAILED carrying the observed state.
///       Covers Off (3) and a transient/timeout projection (Saving, 9); Saved (5) is covered in
///       <see cref="Issue144VmPauseSettlePollTests"/>.
///
///   (3) Happy path — Paused (6) → success:true, state == "Paused" (AC-1/AC-2/AC-3).
///
/// RED-BEFORE / GREEN-AFTER: against the pre-#267 Suspend-VM script (with "Saved" in the success
/// stop-set) the RequestStateChange / NotContain "Suspend-VM" / "Saved-is-not-success" assertions
/// FAIL; against the fixed source they PASS.
///
/// Spec AC-1..AC-6: internal documentation
/// Design: internal documentation — governing decisions
/// LF-D31/LF-D32/LF-D33 (which supersede the now-obsolete LF-D28/LF-D29/LF-D30).
/// See the internal issue tracker
/// </summary>
[Trait("Category", "Runtime")]
public class Issue267VmPauseContractGuardTests
{
    private const string LocalHostId = "local";

    // InputValidation.ValidateVmId rejects non-GUID values. Distinct from the
    // #92/#126/#144/#249 GUIDs for clarity in logs.
    private const string TestVmId = "26726726-2672-2672-2672-267267267267";

    private static ServerOptions BuildOptions() => new()
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
        MaxConcurrentOperations = 8,
    };

    private static (HyperVManager manager, Mock<IPowerShellExecutor> exec) BuildManager()
    {
        var exec = new Mock<IPowerShellExecutor>();
        var options = BuildOptions();
        var resolver = new HostResolver(options);
        var manager = new HyperVManager(
            exec.Object,
            resolver,
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());
        return (manager, exec);
    }

    private static (ToolDispatcher dispatcher, Mock<IPowerShellExecutor> exec) BuildDispatcherWithRealManager()
    {
        var exec = new Mock<IPowerShellExecutor>();
        var options = BuildOptions();
        var resolver = new HostResolver(options);
        var manager = new HyperVManager(
            exec.Object,
            resolver,
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());

        // Concurrency is orthogonal to the #267 state-projection contract: grant every
        // lock immediately so the dispatcher exercises the real PauseVmAsync path.
        var gate = new Mock<IConcurrencyGate>();
        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        var dispatcher = new ToolDispatcher(
            manager,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            resolver,
            new ErrorMapper(),
            gate.Object,
            exec.Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);
        return (dispatcher, exec);
    }

    private static PowerShellResult SuccessResult(string stdout) => new()
    {
        ExitCode = 0,
        Stdout = stdout,
        Stderr = string.Empty,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 50,
    };

    /// <summary>
    /// Shape mirrors what the trailing projecting Get-VM emits AFTER the in-script settle-poll
    /// converges: the stringified state NAME.
    /// </summary>
    private static string VmStateJson(string vmStateName) => $$"""
    {
      "Id": "{{TestVmId}}",
      "Name": "test-vm",
      "State": "{{vmStateName}}",
      "ProcessorCount": 2,
      "MemoryMB": 2048,
      "UptimeSeconds": 60
    }
    """;

    private static string CaptureComposedPauseScript(string settledStateName, out VmInfo info)
    {
        var (manager, exec) = BuildManager();
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(VmStateJson(settledStateName)));

        info = manager.PauseVmAsync(LocalHostId, TestVmId).GetAwaiter().GetResult();
        return capturedScript;
    }

    // ── (1) Non-live contract / stop-set guard ───────────────────────────────

    /// <summary>
    /// Mechanism invariants. The composed pause script MUST invoke the in-memory
    /// RequestStateChange(9 = Quiesce) mechanism and MUST NOT mention either save-to-disk verb
    /// (Suspend-VM / Save-VM). This is the assertion that would have caught #267 pre-fix
    /// without a live host: the pre-#267 Suspend-VM script fails both the RequestStateChange
    /// presence check and the NotContain("Suspend-VM") check.
    /// </summary>
    [Fact]
    public void PauseScript_UsesInMemoryPause_NotSaveToDiskVerbs()
    {
        var script = CaptureComposedPauseScript(settledStateName: "Paused", out var info);

        info.State.Should().Be("Paused",
            "with a settled 'Paused' payload the manager must surface 'Paused'.");

        script.Should().Contain("RequestStateChange",
            "pause MUST drive the WMI Msvm_ComputerSystem.RequestStateChange mechanism to " +
            "reach in-memory Paused (6).");
        script.Should().NotContain("Suspend-VM",
            "Suspend-VM targets Saved (5), the root cause of #267; it MUST be gone.");
        script.Should().NotContain("Save-VM",
            "vm_pause MUST NOT invoke Save-VM — the #92 bug-1 save-to-disk regression trigger.");
    }

    /// <summary>
    /// Pins the exact mechanism constants so a future edit that changes the requested-state
    /// literal, the settle bound, or the poll back-off is caught deterministically. Governed by
    /// internal documentation LF-D31/LF-D32/LF-D33.
    /// </summary>
    [Fact]
    public void PauseScript_PinsExactSettleAndRequestConstants()
    {
        var script = CaptureComposedPauseScript(settledStateName: "Paused", out _);

        script.Should().Contain("[uint16]9",
            "the requested-state literal MUST be [uint16]9 = Quiesce — 32776 is a " +
            "RequestStateChange RETURN-value code and is rejected with 32775 on every guest.");
        script.Should().NotContain("[uint16]32776",
            "reverting to the return-value code 32776 as a RequestedState is the defect itself " +
            "and MUST NOT reappear.");
        script.Should().Contain("$pauseReturn.ReturnValue -ne 0 -and $pauseReturn.ReturnValue -ne 4096",
            "only CIM ReturnValue 0 (completed) or 4096 (job started) are accepted; any other " +
            "immediate nonzero return MUST throw early for diagnosability.");
        script.Should().Contain(").AddSeconds(10)",
            "the settle deadline MUST be bounded at exactly 10 seconds.");
        script.Should().Contain("Start-Sleep -Milliseconds 250",
            "the settle-poll MUST back off exactly 250 ms between re-fetches.");
    }

    /// <summary>
    /// Terminal stop-set contract. "Paused" MUST be the success terminal the
    /// loop breaks on; "Saved" MUST NOT be accepted as a success. The in-script authoritative
    /// guard rejects any non-Paused settle (the byte-preserved throw literal). Against the
    /// pre-#267 script — which accepted "Saved" as the successful pause terminal — the
    /// not-a-success-terminal assertions below fail (RED); against the fixed source they pass.
    /// </summary>
    [Fact]
    public void PauseScript_StopSet_PausedIsSuccess_SavedIsNotAccepted()
    {
        var script = CaptureComposedPauseScript(settledStateName: "Paused", out _);

        script.Should().Contain("$vm.State -eq 'Paused'",
            "'Paused' (6) MUST be the success terminal the settle-poll breaks on.");

        script.Should().Contain("$vm.State -ne 'Paused'",
            "the authoritative in-script guard MUST reject any state that is not exactly " +
            "'Paused', so 'Saved' can never be surfaced as a successful pause.");
        script.Should().Contain("$vm.State -eq 'Saved' -or $vm.State -eq 'Off'",
            "'Saved' (5) and 'Off' (3) are failure terminals the loop stops on (no-hang), " +
            "NOT success acceptances.");

        // The pre-#267 contract treated 'Saved' as the successful pause terminal.
        script.Should().NotContain("$vm.State -eq 'Saved' -and",
            "'Saved' must never be composed into a success-acceptance predicate.");
    }

    // ── (2) Manager-side deterministic failure classification ────────────────

    private async Task<JsonElement> DispatchPauseAsync(ToolDispatcher dispatcher)
    {
        var resultJson = await dispatcher.DispatchAsync(
            "vm_pause",
            new Dictionary<string, object?>
            {
                ["hostId"] = LocalHostId,
                ["vmId"] = TestVmId,
            },
            CancellationToken.None);
        using var doc = JsonDocument.Parse(resultJson);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// A mocked executor returns a SUCCESS payload whose
    /// settled state is "Off" (3) — a failure terminal. The manager-side authoritative guard MUST
    /// classify this as a caller-facing FAILURE (success:false / COMMAND_FAILED) carrying the
    /// observed state, never a benign success projection.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task VmPause_Dispatched_OffPayload_IsClassifiedAsFailure()
    {
        var (dispatcher, exec) = BuildDispatcherWithRealManager();
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(VmStateJson("Off")));

        var root = await DispatchPauseAsync(dispatcher);

        root.GetProperty("success").GetBoolean().Should().BeFalse(
            "a pause that settles to 'Off' (3) is a contract FAILURE, not success.");
        root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.CommandFailed,
            "a non-Paused settle maps to COMMAND_FAILED.");
        root.GetProperty("error").GetString().Should().Contain("Off",
            "the failure message MUST carry the observed non-Paused state ('Off').");
    }

    /// <summary>
    /// Models a settle that never converges:
    /// the projected snapshot is still the transient "Saving" (9) — i.e. the poll ran to its
    /// deadline. The manager-side guard MUST classify this timeout projection as a FAILURE, never
    /// leak the transient 'Saving' as a success (the exact #267 symptom).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task VmPause_Dispatched_TransientSavingTimeoutPayload_IsClassifiedAsFailure()
    {
        var (dispatcher, exec) = BuildDispatcherWithRealManager();
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(VmStateJson("Saving")));

        var root = await DispatchPauseAsync(dispatcher);

        root.GetProperty("success").GetBoolean().Should().BeFalse(
            "a pause whose projection is still the transient 'Saving' (settle timeout) is a " +
            "contract FAILURE — the exact #267 symptom must never surface as success.");
        root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.CommandFailed,
            "a non-Paused (timeout) settle maps to COMMAND_FAILED.");
        root.GetProperty("error").GetString().Should().Contain("Saving",
            "the failure message MUST carry the observed transient state ('Saving').");
    }

    // ── (3) Happy path (AC-1/AC-2/AC-3) ──────────────────────────────────────

    /// <summary>
    /// AC-1/AC-2/AC-3 happy path. A settled Paused (6) payload dispatched through the real
    /// ToolDispatcher + real HyperVManager MUST yield success:true and data.state == "Paused".
    /// </summary>
    [Fact]
    public async Task VmPause_Dispatched_PausedPayload_SucceedsWithStatePaused()
    {
        var (dispatcher, exec) = BuildDispatcherWithRealManager();
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(VmStateJson("Paused")));

        var root = await DispatchPauseAsync(dispatcher);

        root.GetProperty("success").GetBoolean().Should().BeTrue(
            "AC-1: a pause that settles to 'Paused' (6) must yield a success envelope.");
        root.TryGetProperty("data", out var data).Should().BeTrue(
            "AC-2: a successful pause must carry the VmInfo data payload.");
        data.GetProperty("state").GetString().Should().Be("Paused",
            "AC-3: a settled pause must surface data.state == 'Paused'.");
    }
}
