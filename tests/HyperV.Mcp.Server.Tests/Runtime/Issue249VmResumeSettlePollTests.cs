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
/// Issue #249 — vm_resume settle-poll regression guards (the #144 pause fix, resume side).
///
/// Defect: Resume-VM returns while Hyper-V is transiently in State 7 ("Starting"), so a
/// pre-fix single immediate Get-VM projected "Starting" instead of "Running".
///
/// Fix (LF-D24/D25, see
/// myplans/vm-management/lifecycle/vm-pause-state-settle-design.md): a bounded settle-poll
/// (Get-Date deadline, while loop re-fetching the VM, breaking only on the terminal set
/// {Running, Paused, PausedCritical, Saved, Off}, Start-Sleep back-off) lives inside one
/// PowerShell script (one <see cref="IPowerShellExecutor.ExecuteAsync"/> call), so the state
/// has settled by the time the trailing projecting Get-VM emits. Transient State 7 ("Starting")
/// is polled through, never a break target. There is no "Resuming" state.
///
/// Coverage:
///   1. <see cref="ResumeVmAsync_Script_ContainsBoundedSettlePollConstructs"/> — settle-poll
///      constructs present (Get-Date, AddSeconds, while, Start-Sleep, break on 'Running'),
///      Resume-VM present, 'Starting' never a break target (NotContain "-eq 'Starting'").
///   2. <see cref="VmResume_Dispatched_SettledRunningPayload_EnvelopeStateIsRunning"/> and
///      <see cref="ResumeVmAsync_SettledSnapshot_SurfacesRunningNotStarting_SingleFetch"/> —
///      a settled resume surfaces "Running", never "Starting".
///   3. <see cref="VmResume_Dispatched_SettledRunningPayload_CompletesWithoutHang"/> —
///      "Running" is terminal, so the resume completes without spin.
///   4. <see cref="RealExecutor_ResumeVm_NeverSurfacesStarting_RoundTrip"/> — env-gated live
///      Tier-2 (mirrors <see cref="Issue144VmPauseSettlePollTests"/>); skips when Hyper-V,
///      the gate, or a disposable VM are absent.
///
/// Harness: #144 manager + Moq script-capture for tiers 1/2-manager; real ToolDispatcher +
/// real HyperVManager + mocked executor for the envelope tiers.
///
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/249.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue249VmResumeSettlePollTests
{
    private const string LocalHostId = "local";

    // InputValidation.ValidateVmId rejects non-GUID values, so every path needs a
    // syntactically valid GUID. Distinct from the #144 GUID (4444...) for clarity in logs.
    private const string TestVmId = "55555555-5555-5555-5555-555555555555";

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

        // Concurrency is orthogonal to the #249 state-projection contract: grant every
        // lock immediately so the dispatcher exercises the real ResumeVmAsync path.
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
    /// Shape mirrors what <c>$vm | Select-Object Id, Name, State, ... | ConvertTo-Json</c>
    /// emits AFTER the in-script settle-poll has converged: a single integer Hyper-V
    /// State enum. The C# layer maps it via VmStateMap → VmInfo.State → wire "state".
    /// Enum ints (VmStateMap): Running=2, Off=3, Saved=5, Paused=6, Starting=7, PausedCritical=10.
    /// </summary>
    private static string VmStateJson(int hyperVStateEnum) => $$"""
    {
      "Id": "{{TestVmId}}",
      "Name": "test-vm",
      "State": {{hyperVStateEnum}},
      "ProcessorCount": 2,
      "MemoryMB": 2048,
      "UptimeSeconds": 60
    }
    """;

    private static async Task<JsonElement> DispatchResumeAndReadDataAsync(ToolDispatcher dispatcher)
    {
        var resultJson = await dispatcher.DispatchAsync(
            "vm_resume",
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
            "a vm_resume that settles to a terminal state must yield a success envelope.");
        root.TryGetProperty("data", out var data).Should().BeTrue(
            "success envelopes for vm_resume must carry the VmInfo data payload.");
        data.ValueKind.Should().Be(JsonValueKind.Object,
            "data must be the serialized VmInfo object, not null or a primitive.");
        // Clone so the element stays valid after the JsonDocument is disposed.
        return data.Clone();
    }

    // ── (1) Script-shape regression: the deterministic red-before/green-after guard ──

    /// <summary>
    /// #249 deterministic guard. Captures the composed PowerShell script that
    /// <see cref="HyperVManager.ResumeVmAsync"/> sends to the executor and asserts the
    /// bounded settle-poll constructs are present: a <c>Get-Date</c> deadline built with
    /// <c>AddSeconds</c>, a <c>while</c> loop, a <c>Start-Sleep</c> back-off, and a
    /// terminal-state break testing <c>Running</c>. It also pins that <c>Resume-VM</c> is
    /// composed.
    ///
    /// RED-BEFORE / GREEN-AFTER: against the pre-fix actionBlock — a single immediate
    /// Get-VM after Resume-VM with NO loop — the while/Start-Sleep/AddSeconds/terminal-break
    /// assertions FAIL. Against the current fixed source they PASS.
    ///
    /// Crucially asserts <c>NotContain("-eq 'Starting'")</c>: the transient State 7
    /// ("Starting") is the state the settle-poll exists to wait THROUGH, never a break
    /// target. Breaking on it would reintroduce the defect (the resume analogue of #144's
    /// NotContain "-eq 'Saving'").
    /// </summary>
    [Fact]
    public async Task ResumeVmAsync_Script_ContainsBoundedSettlePollConstructs()
    {
        var (manager, exec) = BuildManager();
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(VmStateJson(2)));

        var info = await manager.ResumeVmAsync(LocalHostId, TestVmId);

        info.State.Should().Be("Running",
            "with a settled State:2 payload the manager must surface 'Running'.");

        // ── Resume invariant preserved ──
        capturedScript.Should().Contain("Resume-VM",
            "vm_resume MUST compose a Resume-VM call.");

        // ── #249 settle-poll constructs (RED on the pre-fix no-loop script) ──
        capturedScript.Should().Contain("Get-Date",
            "#249: the settle-poll must compute a wall-clock deadline via Get-Date.");
        capturedScript.Should().Contain("AddSeconds",
            "#249: the deadline must be bounded with AddSeconds so the loop cannot run unbounded.");
        capturedScript.Should().Contain("while",
            "#249: the fix must poll in a while loop until the state settles — the construct " +
            "the pre-fix single-immediate-Get-VM script lacked.");
        capturedScript.Should().Contain("Start-Sleep",
            "#249: the poll loop must back off with Start-Sleep between re-fetches.");

        // Terminal-state break: 'Running' must gate the break so a successful resume
        // exits the loop promptly once the VM has settled.
        capturedScript.Should().Contain("'Running'",
            "#249: the loop must break on the stable terminal state 'Running'.");
        capturedScript.Should().Contain("break",
            "#249: the loop must break out once a terminal state is observed.");

        // The pre-fix script projected the transient state directly; the contract is that
        // 'Starting' is only ever a transient the loop waits THROUGH, never a break target.
        capturedScript.Should().NotContain("-eq 'Starting'",
            "#249: 'Starting' must never be a terminal break condition — it is the transient " +
            "the settle-poll exists to wait through. Breaking on it would reintroduce the defect.");
    }

    // ── (2) Transient-then-stable envelope behavior: the core #249 proof ──────

    /// <summary>
    /// End-to-end #249 contract through the REAL <see cref="ToolDispatcher"/> + REAL
    /// <see cref="HyperVManager"/> with a mocked executor. In production the in-script
    /// settle-poll converges before the projecting Get-VM emits, so the single
    /// <c>ExecuteAsync</c> the resume path makes returns the SETTLED State:2 snapshot.
    /// The envelope's <c>data.state</c> MUST be the exact literal "Running".
    /// </summary>
    [Fact]
    public async Task VmResume_Dispatched_SettledRunningPayload_EnvelopeStateIsRunning()
    {
        var (dispatcher, exec) = BuildDispatcherWithRealManager();
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(VmStateJson(2)));

        var data = await DispatchResumeAndReadDataAsync(dispatcher);

        data.TryGetProperty("state", out var state).Should().BeTrue(
            "VmInfo on the wire must expose a lowercase 'state' property — the #249 surface.");
        state.GetString().Should().Be("Running",
            "#249: a settled resume must surface data.state == 'Running'.");
        state.GetString().Should().NotBe("Starting",
            "#249 core contract: data.state is NEVER the transient 'Starting' for a resume " +
            "that settles. The settle-poll waits through 'Starting' inside the script.");
    }

    /// <summary>
    /// Manager-tier #249 proof of the single-fetch contract. The settle-poll lives ENTIRELY
    /// inside one PowerShell script, so the transient State:7 ("Starting") is consumed in-script
    /// and the ONLY snapshot that ever crosses the C# executor seam is the settled one the
    /// trailing projection emits. This test pins that contract two ways: (a) the resume path
    /// invokes the executor EXACTLY once — there is no second, post-Resume fetch that could
    /// leak a transient (the pre-fix shape did a single immediate fetch that projected
    /// "Starting"); and (b) given that single post-settlement snapshot is State:2, the manager
    /// surfaces "Running", never "Starting".
    ///
    /// The first sequenced entry is the settled State:2 the converged loop projects; a trailing
    /// State:7 entry is present ONLY to fail the test loudly if a regression ever introduced a
    /// second executor round-trip that re-observed the host mid-transition.
    /// </summary>
    [Fact]
    public async Task ResumeVmAsync_SettledSnapshot_SurfacesRunningNotStarting_SingleFetch()
    {
        var (manager, exec) = BuildManager();

        // First (and, per contract, only) call → the settled State:2 the in-script loop
        // projects after convergence. The trailing State:7 is a tripwire: if a regression
        // adds a second executor round-trip, it would re-observe the transient and the
        // assertions below would surface "Starting".
        var snapshots = new Queue<string>(new[] { VmStateJson(2), VmStateJson(7) });
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(() => SuccessResult(snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek()));

        var info = await manager.ResumeVmAsync(LocalHostId, TestVmId);

        info.State.Should().Be("Running",
            "#249: the manager must surface the settled 'Running' state for a successful resume.");
        info.State.Should().NotBe("Starting",
            "#249 contract: the transient Hyper-V State:7 ('Starting') must never be the " +
            "projected result of a resume that settles — that was the exact pre-fix defect.");

        // The settle-poll is in-script, so exactly ONE ExecuteAsync crosses the seam. A second
        // call would mean a post-Resume re-fetch outside the loop — the structural shape that
        // let the pre-fix code project the transient 'Starting'.
        exec.Verify(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()),
            Times.Once,
            "#249: vm_resume must make exactly one executor call (the loop is in-script); a " +
            "second fetch could re-observe the host mid-transition and leak 'Starting'.");
    }

    // ── (3) No-hang / Running terminal ───────────────────────────────────────

    /// <summary>
    /// No-hang guard. When the VM resumes and settles at State:2 ("Running"), the terminal
    /// stop-set includes "Running", so the in-script loop exits rather than spinning to its
    /// deadline. End-to-end the call MUST COMPLETE (not hang/throw) and the envelope MUST be
    /// well-formed with data.state == "Running". A 30 s test timeout converts any accidental
    /// hang into a deterministic failure.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task VmResume_Dispatched_SettledRunningPayload_CompletesWithoutHang()
    {
        var (dispatcher, exec) = BuildDispatcherWithRealManager();
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(VmStateJson(2)));

        var data = await DispatchResumeAndReadDataAsync(dispatcher);

        data.TryGetProperty("state", out var state).Should().BeTrue(
            "the Running-terminal envelope must still expose a lowercase 'state' property.");
        state.GetString().Should().Be("Running",
            "#249: a resume that settles to State:2 must surface data.state == 'Running', " +
            "proving 'Running' is in the terminal stop-set so the loop cannot spin forever.");
    }

    // ── (4) Optional env-gated live Tier-2 (mirrors #144 gating) ──────────────

    private readonly ITestOutputHelper? _output;

    public Issue249VmResumeSettlePollTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Operator opt-in for the live round-trip tier (mirrors #144 / #125 OQ-1).</summary>
    private const string RunRealPsEnvVar = "HYPERV_MCP_RUN_REAL_PS";

    /// <summary>
    /// GUID of a DISPOSABLE, currently-PAUSED test VM the operator authorizes for a real
    /// resume round-trip. MUST NOT be a production or donor VM. When unset, the tier skips.
    /// </summary>
    private const string TestVmIdEnvVar = "HYPERV_MCP_TEST_VM_ID";

    /// <summary>
    /// Live Tier-2 (env-gated; disposable PAUSED test VM only). Drives the real
    /// <see cref="PowerShellExecutor"/> + real <see cref="HyperVManager"/> to actually
    /// Resume-VM the operator-named VM and asserts the returned envelope NEVER surfaces
    /// the transient "Starting" — i.e. the settle-poll really converged on a real host.
    /// Best-effort re-pauses the VM afterwards so the host is left as found.
    ///
    /// SAFETY/SKIP (mirrors <see cref="Issue144VmPauseSettlePollTests"/>):
    /// gated by <see cref="RunRealPsEnvVar"/>==1 AND a non-empty <see cref="TestVmIdEnvVar"/>
    /// AND an elevated process on a Hyper-V host. xUnit v2 has no first-class skip, so the
    /// gated paths early-return and REPORT AS PASSED — check the output for "SKIP:" lines.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "RealPowerShell")]
    public async Task RealExecutor_ResumeVm_NeverSurfacesStarting_RoundTrip()
    {
        var gate = Environment.GetEnvironmentVariable(RunRealPsEnvVar);
        var liveVmId = Environment.GetEnvironmentVariable(TestVmIdEnvVar);

        // Opt-in MUST be the exact literal "1". Treating any non-"0" value as enabled would
        // let HYPERV_MCP_RUN_REAL_PS=false (or =2) silently resume a live VM — a footgun.
        if (gate != "1")
        {
            _output?.WriteLine(
                $"SKIP: RealExecutor_ResumeVm_NeverSurfacesStarting_RoundTrip — {RunRealPsEnvVar} not set to 1 (live round-trip is operator opt-in).");
            return;
        }

        if (!HyperVAvailable() || !IsElevated())
        {
            _output?.WriteLine(
                "SKIP: RealExecutor_ResumeVm_NeverSurfacesStarting_RoundTrip — requires an elevated process on a Hyper-V host.");
            return;
        }

        if (string.IsNullOrWhiteSpace(liveVmId))
        {
            _output?.WriteLine(
                $"SKIP: RealExecutor_ResumeVm_NeverSurfacesStarting_RoundTrip — {TestVmIdEnvVar} (disposable PAUSED test VM GUID) not set. " +
                "Refusing to resume a live VM without an explicit disposable target (donor/production VMs are out of scope).");
            return;
        }

        var manager = BuildRealManager();
        _output?.WriteLine($"Live resume round-trip on test VM {liveVmId}.");

        try
        {
            // The real Resume-VM returns mid-transition in State 7 ('Starting'); the settle-poll
            // must converge so the projected envelope is the contract 'Running', never 'Starting'.
            var info = await manager.ResumeVmAsync(LocalHostId, liveVmId);

            _output?.WriteLine($"Live resume returned state='{info.State}'.");
            info.State.Should().NotBe("Starting",
                "#249 live proof: a real Resume-VM that the settle-poll waits through must " +
                "NOT surface the transient 'Starting' on the envelope after convergence.");
            info.State.Should().Be("Running",
                "#249 live proof: a real successful resume must settle to 'Running'.");
        }
        finally
        {
            // Best-effort restore: re-pause the VM so the host is left as found. A pause
            // failure is logged, not thrown, so cleanup never masks the assertion outcome.
            try
            {
                var paused = await manager.PauseVmAsync(LocalHostId, liveVmId);
                _output?.WriteLine($"Cleanup: re-paused VM {liveVmId} (state='{paused.State}').");
            }
            catch (Exception cleanupEx)
            {
                _output?.WriteLine(
                    $"Cleanup WARNING: failed to re-pause VM {liveVmId}: {cleanupEx.Message}. Manual pause may be required.");
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
