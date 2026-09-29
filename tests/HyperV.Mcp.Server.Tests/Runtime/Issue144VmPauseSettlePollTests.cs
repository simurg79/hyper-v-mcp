using System.Security.Principal;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #144 / #267 — vm_pause settle-poll and Paused-classification regression guards.
///
/// #267 / LF-D28 moved vm_pause to the WMI RequestStateChange(32776 = Quiesce/Paused) request
/// whose only success terminal is in-memory "Paused" (6). The #144 settle-poll waits through the
/// transient "Saving" (9); "Saved" (5)/"Off" (3)/timeout are contract FAILURES. The bounded poll
/// lives inside one PowerShell script (one <see cref="IPowerShellExecutor.ExecuteAsync"/> call), so
/// state has settled before the trailing projecting Get-VM emits. The authoritative not-Paused
/// classification is a manager-side guard, making Saved/Off/timeout regression-testable with a
/// mocked executor.
/// See myplans/vm-management/lifecycle/vm-pause-state-settle-design.md — LF-D28/LF-D29/LF-D30.
///
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/144 and /issues/267.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue144VmPauseSettlePollTests
{
    private const string LocalHostId = "local";

    // InputValidation.ValidateVmId rejects non-GUID values, so every path needs a
    // syntactically valid GUID. Distinct from the #92/#126 GUIDs for clarity in logs.
    private const string TestVmId = "44444444-4444-4444-4444-444444444444";

    // ── Shared fixtures ──────────────────────────────────────────────────────

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

        // Concurrency is orthogonal to the #144 state-projection contract: grant every
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
    /// Shape mirrors what the production projection emits AFTER the in-script settle-poll has
    /// converged: the stringified Get-VM state NAME, consumed directly by the deserializer.
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

    private static async Task<JsonElement> DispatchPauseAndReadDataAsync(ToolDispatcher dispatcher)
    {
        var resultJson = await dispatcher.DispatchAsync(
            "vm_pause",
            new Dictionary<string, object?>
            {
                ["hostId"] = LocalHostId,
                ["vmId"] = TestVmId,
            },
            CancellationToken.None);

        // Parse as a raw JsonDocument so the assertions observe the wire shape exactly
        // as an MCP client would (lowercase "state" nested under "data"), catching any
        // projection/serialization mis-map a typed deserialize might paper over.
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue(
            "a vm_pause that settles to a terminal state must yield a success envelope.");
        root.TryGetProperty("data", out var data).Should().BeTrue(
            "success envelopes for vm_pause must carry the VmInfo data payload.");
        data.ValueKind.Should().Be(JsonValueKind.Object,
            "data must be the serialized VmInfo object, not null or a primitive.");
        // Clone so the element stays valid after the JsonDocument is disposed.
        return data.Clone();
    }

    // ── (1) Script-shape regression: the deterministic guard #126 lacked ──────

    /// <summary>
    /// #144/#267 deterministic script-shape guard. Captures the composed PowerShell script that
    /// <see cref="HyperVManager.PauseVmAsync"/> sends to the executor and asserts the bounded
    /// settle-poll constructs are present (a <c>Get-Date</c>/<c>AddSeconds</c> deadline, a
    /// <c>while</c> loop, a <c>Start-Sleep</c> back-off, a terminal-state break on <c>Paused</c>),
    /// plus the #267/LF-D28 mechanism invariants: <c>RequestStateChange</c> present, the superseded
    /// save-to-disk verbs absent.
    /// </summary>
    [Fact]
    public async Task PauseVmAsync_Script_ContainsBoundedSettlePollConstructs()
    {
        var (manager, exec) = BuildManager();
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(VmStateJson("Paused")));

        var info = await manager.PauseVmAsync(LocalHostId, TestVmId);

        info.State.Should().Be("Paused",
            "with a settled 'Paused' payload the manager must surface 'Paused'.");

        // ── #267 / LF-D28 invariants: in-memory pause, not a save-to-disk verb ──
        capturedScript.Should().Contain("RequestStateChange",
            "#267 / LF-D28: vm_pause MUST use the WMI RequestStateChange(32776 = Quiesce/Paused) " +
            "mechanism to reach in-memory Paused (6).");
        capturedScript.Should().NotContain("Suspend-VM",
            "#267 / LF-D28: Suspend-VM targets Saved (5), the root cause of #267; it MUST be gone.");
        capturedScript.Should().NotContain("Save-VM",
            "vm_pause MUST NOT invoke Save-VM — that is the #92 bug-1 regression trigger.");

        // ── #144 settle-poll constructs (RED on the pre-fix no-loop script) ──
        capturedScript.Should().Contain("Get-Date",
            "#144: the settle-poll must compute a wall-clock deadline via Get-Date.");
        capturedScript.Should().Contain("AddSeconds",
            "#144: the deadline must be bounded with AddSeconds so the loop cannot run unbounded.");
        capturedScript.Should().Contain("while",
            "#144: the fix must poll in a while loop until the state settles — the construct " +
            "the pre-fix single-immediate-Get-VM script lacked (and the #126 mock could not see).");
        capturedScript.Should().Contain("Start-Sleep",
            "#144: the poll loop must back off with Start-Sleep between re-fetches.");

        // #267 / LF-D29: only 'Paused' is a success terminal; the loop must break on it.
        capturedScript.Should().Contain("'Paused'",
            "#267 / LF-D29: the loop must break on the success terminal state 'Paused'.");
        capturedScript.Should().Contain("break",
            "#144: the loop must break out once a terminal state is observed.");

        // The pre-fix script projected the transient state directly; the contract is that
        // 'Saving' is only ever a transient the loop waits THROUGH, never a break target.
        capturedScript.Should().NotContain("-eq 'Saving'",
            "#144: 'Saving' must never be a terminal break condition — it is the transient " +
            "the settle-poll exists to wait through. Breaking on it would reintroduce the defect.");
    }

    // ── (2) Transient-then-stable envelope behavior: the core #144 proof ──────

    /// <summary>
    /// End-to-end #144 contract through the REAL <see cref="ToolDispatcher"/> + REAL
    /// <see cref="HyperVManager"/> with a mocked executor. In production the in-script
    /// settle-poll converges before the projecting Get-VM emits, so the single
    /// <c>ExecuteAsync</c> the pause path makes returns the SETTLED State:6 snapshot.
    /// The envelope's <c>data.state</c> MUST be the exact literal "Paused".
    /// </summary>
    [Fact]
    public async Task VmPause_Dispatched_SettledPausedPayload_EnvelopeStateIsPaused()
    {
        var (dispatcher, exec) = BuildDispatcherWithRealManager();
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(VmStateJson("Paused")));

        var data = await DispatchPauseAndReadDataAsync(dispatcher);

        data.TryGetProperty("state", out var state).Should().BeTrue(
            "VmInfo on the wire must expose a lowercase 'state' property — the #144 surface.");
        state.GetString().Should().Be("Paused",
            "#144: a settled pause must surface data.state == 'Paused'.");
        state.GetString().Should().NotBe("Saving",
            "#144 core contract: data.state is NEVER the transient 'Saving' for a pause " +
            "that settles. The settle-poll waits through 'Saving' inside the script.");
    }

    /// <summary>
    /// Manager-tier #144 proof of the single-fetch contract. The settle-poll lives ENTIRELY
    /// inside one PowerShell script, so the transient State:9 ("Saving") is consumed in-script
    /// and the ONLY snapshot that ever crosses the C# executor seam is the settled one the
    /// trailing projection emits. This test pins that contract two ways: (a) the pause path
    /// invokes the executor EXACTLY once — there is no second, post-Suspend fetch that could
    /// leak a transient (the pre-fix shape did a single immediate fetch that projected
    /// "Saving"); and (b) given that single post-settlement snapshot is State:6, the manager
    /// surfaces "Paused", never "Saving".
    ///
    /// The first sequenced entry is the settled State:6 the converged loop projects; a trailing
    /// State:9 entry is present ONLY to fail the test loudly if a regression ever introduced a
    /// second executor round-trip that re-observed the host mid-transition.
    /// </summary>
    [Fact]
    public async Task PauseVmAsync_SettledSnapshot_SurfacesPausedNotSaving_SingleFetch()
    {
        var (manager, exec) = BuildManager();

        // First (and, per contract, only) call → the settled 'Paused' the in-script loop
        // projects after convergence. The trailing 'Saving' is a tripwire: if a regression
        // adds a second executor round-trip, it would re-observe a transient and the
        // assertions below would surface "Saving".
        var snapshots = new Queue<string>(new[] { VmStateJson("Paused"), VmStateJson("Saving") });
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(() => SuccessResult(snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek()));

        var info = await manager.PauseVmAsync(LocalHostId, TestVmId);

        info.State.Should().Be("Paused",
            "#144: the manager must surface the settled 'Paused' state for a successful pause.");
        info.State.Should().NotBe("Saving",
            "#144 contract: the transient 'Saving' state must never be the " +
            "projected result of a pause that settles — that was the exact pre-fix defect.");

        // The settle-poll is in-script, so exactly ONE ExecuteAsync crosses the seam. A second
        // call would mean a post-Suspend re-fetch outside the loop — the structural shape that
        // let the pre-fix code project the transient 'Saving'.
        exec.Verify(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()),
            Times.Once,
            "#144: vm_pause must make exactly one executor call (the loop is in-script); a " +
            "second fetch could re-observe the host mid-transition and leak 'Saving'.");
    }

    // ── (3) No-hang / Saved is now a FAILURE terminal (#267 LF-D29/LF-D30) ────

    /// <summary>
    /// #267 LF-D30 no-hang + failure-classification guard. Under Option A a pause that
    /// settles to State:5 ("Saved") — suspend-to-disk — is a CONTRACT FAILURE, not a
    /// success: only in-memory "Paused" (6) is a success terminal. The in-script loop
    /// still breaks on "Saved" (no-hang), but the manager's trailing not-Paused guard
    /// throws, so the dispatched envelope MUST be a FAILURE (success:false). A 30 s test
    /// timeout converts any accidental hang into a deterministic failure.
    ///
    /// NOTE: with a mocked executor the in-script not-Paused throw cannot run, so the
    /// failure is surfaced by the manager-side LF-D30 check instead. This test pins the
    /// caller-facing FAILURE classification (never a benign "Saved" success projection);
    /// the real-executor State-6 settle is covered by the env-gated live tier.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task VmPause_Dispatched_SavedPayload_IsClassifiedAsFailure()
    {
        var (dispatcher, exec) = BuildDispatcherWithRealManager();
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(VmStateJson("Saved")));

        var resultJson = await dispatcher.DispatchAsync(
            "vm_pause",
            new Dictionary<string, object?>
            {
                ["hostId"] = LocalHostId,
                ["vmId"] = TestVmId,
            },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeFalse(
            "#267 / LF-D30: a pause that settles to 'Saved' (suspend-to-disk) instead of " +
            "'Paused' is a contract FAILURE, never a success envelope.");
    }

    // ── (4) Optional env-gated live Tier-2 (mirrors #125 gating) ──────────────

    private readonly ITestOutputHelper? _output;

    public Issue144VmPauseSettlePollTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Operator opt-in for the live round-trip tier (mirrors #125 OQ-1).</summary>
    private const string RunRealPsEnvVar = "HYPERV_MCP_RUN_REAL_PS";

    /// <summary>
    /// GUID of a DISPOSABLE, currently-RUNNING test VM the operator authorizes for a real
    /// pause→resume round-trip. MUST NOT be a production or donor VM. When unset, the
    /// tier skips.
    /// </summary>
    private const string TestVmIdEnvVar = "HYPERV_MCP_TEST_VM_ID";

    /// <summary>
    /// #267 / LF-D28 live Tier-2 (env-gated; disposable RUNNING test VM only). Drives the real
    /// <see cref="PowerShellExecutor"/> + real <see cref="HyperVManager"/> to actually pause the
    /// operator-named VM via the Option A WMI RequestStateChange(32776) path and asserts it reaches
    /// in-memory "Paused" (6), never "Saved" (5) or the transient "Saving" (9) — i.e. Option A really
    /// converges on a real host. Best-effort resumes the VM afterwards so the host is left as found.
    ///
    /// SCOPE: this validates the HOST-SIDE CIM pause path (Msvm_ComputerSystem by GUID), which is
    /// guest-OS-agnostic, so it covers both TC-W10 (Windows guest) and TC-L09 (Linux-host guest) —
    /// the transition is driven by the virtualization layer, not the guest OS. Tester expands
    /// per-guest live coverage at Gate 10.
    ///
    /// SAFETY/SKIP (mirrors <see cref="Issue125CheckpointVmRealExecutorRegressionTests"/>):
    /// gated by <see cref="RunRealPsEnvVar"/>==1 AND a non-empty <see cref="TestVmIdEnvVar"/>
    /// AND an elevated process on a Hyper-V host. xUnit v2 has no first-class skip, so the
    /// gated paths early-return and REPORT AS PASSED — check the output for "SKIP:" lines.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "RealPowerShell")]
    public async Task RealExecutor_Pause_ReachesPausedNeverSaved_RoundTrip()
    {
        var gate = Environment.GetEnvironmentVariable(RunRealPsEnvVar);
        var liveVmId = Environment.GetEnvironmentVariable(TestVmIdEnvVar);

        // Opt-in MUST be the exact literal "1". Treating any non-"0" value as enabled would
        // let HYPERV_MCP_RUN_REAL_PS=false (or =2) silently pause a live VM — a footgun.
        if (gate != "1")
        {
            _output?.WriteLine(
                $"SKIP: RealExecutor_Pause_ReachesPausedNeverSaved_RoundTrip — {RunRealPsEnvVar} not set to 1 (live round-trip is operator opt-in).");
            return;
        }

        if (!HyperVAvailable() || !IsElevated())
        {
            _output?.WriteLine(
                "SKIP: RealExecutor_Pause_ReachesPausedNeverSaved_RoundTrip — requires an elevated process on a Hyper-V host.");
            return;
        }

        if (string.IsNullOrWhiteSpace(liveVmId))
        {
            _output?.WriteLine(
                $"SKIP: RealExecutor_Pause_ReachesPausedNeverSaved_RoundTrip — {TestVmIdEnvVar} (disposable RUNNING test VM GUID) not set. " +
                "Refusing to pause a live VM without an explicit disposable target (donor/production VMs are out of scope).");
            return;
        }

        var manager = BuildRealManager();
        _output?.WriteLine($"Live pause round-trip on test VM {liveVmId}.");

        try
        {
            // The real RequestStateChange(32776) request may transit State 9 ('Saving'); the
            // settle-poll must converge on in-memory 'Paused' (6), never 'Saved' (5)/'Saving'.
            var info = await manager.PauseVmAsync(LocalHostId, liveVmId);

            _output?.WriteLine($"Live pause returned state='{info.State}'.");
            info.State.Should().NotBe("Saving",
                "#267 live proof: the transient 'Saving' must never survive to the envelope after " +
                "the settle-poll converges.");
            info.State.Should().NotBe("Saved",
                "#267 / LF-D28 live proof: Option A is an in-memory pause; reaching save-to-disk " +
                "'Saved' would mean the wrong mechanism ran.");
            info.State.Should().Be("Paused",
                "#267 / LF-D28 live proof: a real successful Option A pause must settle to in-memory 'Paused'.");
        }
        finally
        {
            // Best-effort restore: resume the VM so the host is left as found. A resume
            // failure is logged, not thrown, so cleanup never masks the assertion outcome.
            try
            {
                var resumed = await manager.ResumeVmAsync(LocalHostId, liveVmId);
                _output?.WriteLine($"Cleanup: resumed VM {liveVmId} (state='{resumed.State}').");
            }
            catch (Exception cleanupEx)
            {
                _output?.WriteLine(
                    $"Cleanup WARNING: failed to resume VM {liveVmId}: {cleanupEx.Message}. Manual resume may be required.");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HyperVManager BuildRealManager()
    {
        var psExecutor = new PowerShellExecutor(
            NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>());
        var options = BuildOptions();
        var resolver = new HostResolver(options);
        return new HyperVManager(
            psExecutor,
            resolver,
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());
    }

    /// <summary>True when the Hyper-V management service (vmms) exists and is Running.</summary>
    private static bool HyperVAvailable()
    {
        try
        {
            var vmms = System.ServiceProcess.ServiceController
                .GetServices()
                .FirstOrDefault(service => string.Equals(service.ServiceName, "vmms", StringComparison.OrdinalIgnoreCase));
            return vmms?.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
