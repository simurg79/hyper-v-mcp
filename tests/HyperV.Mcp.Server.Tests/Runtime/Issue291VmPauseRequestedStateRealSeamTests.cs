using System.Security.Principal;
using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #291 — vm_pause failed on every guest because PR #270 passed 32776 (a value from the
/// RequestStateChange RETURN-value ValueMap) as the RequestedState PARAMETER, whose Quiesce value
/// is 9. The host rejected it synchronously with 32775 and the VM stayed Running.
///
/// The pre-existing pause guards capture the composed script and assert on its TEXT without ever
/// running it — the masked-mock anti-pattern that kept the suite green while vm_pause was 100%
/// broken. These tests close that gap: the script the PRODUCTION manager composes is really
/// EXECUTED by the real <see cref="PowerShellExecutor"/> against a stub host whose
/// Invoke-CimMethod enforces the RequestedState parameter ValueMap (only 9 = Quiesce is accepted,
/// anything else returns 32775) exactly as a live guest does. A wrong literal therefore produces a
/// real, observed failure rather than a text-shape mismatch.
///
/// Design: internal documentation — LF-D31/LF-D32/LF-D33.
/// See the internal issue tracker
/// </summary>
[Trait("Category", "Runtime")]
[Trait("Category", "RealPowerShell")]
public class Issue291VmPauseRequestedStateRealSeamTests
{
    private readonly ITestOutputHelper _output;

    private const string LocalHostId = "local";
    private const string TestVmId = "29129129-2912-2912-2912-291291291291";

    public Issue291VmPauseRequestedStateRealSeamTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Records the composed script at the real <see cref="IPowerShellExecutor"/> seam. A concrete
    /// recording fake (not a permissive mock) so an unimplemented member fails loudly instead of
    /// silently returning a default.
    /// </summary>
    private sealed class RecordingPowerShellExecutor : IPowerShellExecutor
    {
        private readonly string _stdout;

        public RecordingPowerShellExecutor(string stdout) => _stdout = stdout;

        public string? LastScript { get; private set; }

        public Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
        {
            LastScript = script;
            return Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = _stdout,
                Stderr = string.Empty,
                DurationMs = 1,
            });
        }
    }

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
            },
        },
        MaxConcurrentOperations = 8,
    };

    private static string SettledPausedJson() => $$"""
    { "Id": "{{TestVmId}}", "Name": "fake-vm", "State": "Paused", "ProcessorCount": 2, "MemoryMB": 2048, "UptimeSeconds": 60 }
    """;

    /// <summary>Composes the production pause script through the real HyperVManager.</summary>
    private static string CaptureProductionPauseScript()
    {
        var recorder = new RecordingPowerShellExecutor(SettledPausedJson());
        var options = BuildOptions();
        var manager = new HyperVManager(
            recorder,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());

        manager.PauseVmAsync(LocalHostId, TestVmId).GetAwaiter().GetResult();
        return recorder.LastScript!;
    }

    /// <summary>
    /// Stub-host prelude. Shadows the Hyper-V/CIM cmdlets with functions so the REAL composed
    /// script runs end-to-end off-host. <paramref name="acceptedRequestedState"/> models the
    /// RequestedState parameter ValueMap the live host enforces: a request carrying any other
    /// value is rejected with 32775 ("Invalid State Transition") — the exact #291 symptom.
    /// <paramref name="forcedReturnValue"/> overrides the return unconditionally so the decode
    /// table can be exercised on its own.
    /// </summary>
    private static string BuildStubHostPrelude(
        string settledState,
        int acceptedRequestedState = 9,
        int? forcedReturnValue = null)
    {
        var forced = forcedReturnValue.HasValue
            ? $"return [pscustomobject]@{{ ReturnValue = {forcedReturnValue.Value} }}"
            : "# no forced return";

        return $@"
enum FakeVmState {{ Off = 3; Running = 2; Saved = 5; Paused = 6; PausedCritical = 7; Saving = 9 }}
$script:FakeState = [FakeVmState]::Running
function Get-VM {{
    param([string]$Id, [string]$ComputerName, [string]$Name)
    [pscustomobject]@{{
        Id = '{TestVmId}'
        Name = 'fake-vm'
        State = $script:FakeState
        ProcessorCount = 2
        MemoryStartup = 2147483648
        Uptime = [timespan]::FromSeconds(60)
    }}
}}
function Get-CimInstance {{
    param($Namespace, $ClassName, $Filter)
    [pscustomobject]@{{ Name = '{TestVmId}' }}
}}
function Invoke-CimMethod {{
    param($InputObject, $MethodName, $Arguments)
    {forced}
    $requested = [int]$Arguments['RequestedState']
    if ($requested -ne {acceptedRequestedState}) {{
        # Live-host behaviour: a value outside the RequestedState parameter ValueMap is refused
        # synchronously with 32775 and no Job is created (#291).
        return [pscustomobject]@{{ ReturnValue = 32775 }}
    }}
    $script:FakeState = [FakeVmState]::{settledState}
    return [pscustomobject]@{{ ReturnValue = 0 }}
}}
";
    }

    /// <summary>
    /// Executes the production-composed pause script against the stub host with the real
    /// PowerShell executor. Import-Module is dropped because the stub replaces the module's
    /// cmdlets; every other line — including the RequestedState literal under test — is the
    /// production text verbatim.
    /// </summary>
    /// <summary>
    /// PowerShell wraps long error text across lines with '|' gutters, which would break plain
    /// substring assertions on the thrown message. Strip the gutters and collapse whitespace so
    /// the assertions test the MESSAGE, not the console's wrap width.
    /// </summary>
    private static string NormalizeErrorText(string text) =>
        string.Join(' ', text.Replace("|", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private async Task<PowerShellResult> RunProductionPauseScriptAsync(string prelude)
    {
        var productionScript = CaptureProductionPauseScript();
        productionScript.Should().Contain("RequestStateChange",
            "the captured text must be the production pause script");

        var body = new StringBuilder();
        foreach (var line in productionScript.Split('\n'))
        {
            if (line.TrimStart().StartsWith("Import-Module", StringComparison.Ordinal))
            {
                continue;
            }

            body.Append(line).Append('\n');
        }

        var executor = new PowerShellExecutor(
            NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>());
        var result = await executor.ExecuteAsync(prelude + body, timeoutSeconds: 90);
        _output.WriteLine($"exit={result.ExitCode} stdout='{result.Stdout.Trim()}'");
        _output.WriteLine($"stderr='{result.Stderr.Trim()}'");
        return result;
    }

    // ── Guard 1: real-seam RequestedState regression (#291) ──────────────────

    /// <summary>
    /// THE #291 guard. The stub host accepts only RequestedState 9 (Quiesce), as the parameter
    /// ValueMap dictates. Running the production script must actually reach Paused. With the
    /// defect restored ([uint16]32776) the stub rejects the call with 32775 and this test fails
    /// on a real observed failure — not on script-text shape.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ProductionPauseScript_ReallyExecuted_ReachesPaused_WhenRequestedStateIsQuiesce()
    {
        var result = await RunProductionPauseScriptAsync(
            BuildStubHostPrelude(settledState: "Paused"));

        result.Stderr.Should().NotContain("32775",
            "#291: RequestedState MUST be the parameter-ValueMap Quiesce value 9. A return-value " +
            "code such as 32776 is refused by the host with 32775 ('Invalid State Transition') " +
            "and the guest is never paused.");
        result.ExitCode.Should().Be(0,
            "the executed pause script must complete and settle into Paused against a host that " +
            "enforces the RequestedState parameter ValueMap");
        result.Stdout.Should().Contain("\"State\": \"Paused\"",
            "the projected snapshot must report the Get-VM state NAME; a bare ordinal here means " +
            "the numeric projection returned and the CIM/VMState conflation is reachable again.");
    }

    /// <summary>
    /// The accepted-return contract, really executed: 4096 (job started) must be accepted just
    /// like 0, so a host that dispatches the transition asynchronously is not misreported.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ProductionPauseScript_ReallyExecuted_AcceptsJobStartedReturn4096()
    {
        // Forced 4096 leaves the stub state at Running, so the settle guard must be the only
        // thing that can fail here — the return itself must NOT be treated as an error.
        var result = await RunProductionPauseScriptAsync(
            BuildStubHostPrelude(settledState: "Paused", forcedReturnValue: 4096));

        NormalizeErrorText(result.Stderr).Should().NotContain("returned 4096",
            "4096 means the host started a job; it MUST be accepted, never thrown on.");
    }

    // ── Guard 2: decode table and thrown-message content ─────────────────────

    /// <summary>
    /// A 32775 return must produce the DECODED meaning and the observed Get-VM state, so the
    /// #291 failure is diagnosable from the message alone.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ProductionPauseScript_ReallyExecuted_Return32775_DecodesInvalidStateTransition()
    {
        var result = await RunProductionPauseScriptAsync(
            BuildStubHostPrelude(settledState: "Paused", forcedReturnValue: 32775));

        var stderr = NormalizeErrorText(result.Stderr);
        result.ExitCode.Should().NotBe(0, "an unaccepted return must fail the script");
        stderr.Should().Contain("returned 32775 (Invalid State Transition)",
            "32775 MUST be decoded from the RequestStateChange return ValueMap.");
        stderr.Should().Contain("observed Get-VM state 'Running'",
            "the diagnostic MUST report the state actually observed via Get-VM.");
    }

    /// <summary>
    /// An unlisted return code must fall back to 'Unrecognized return code' rather than being
    /// mis-decoded as one of the two known meanings.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ProductionPauseScript_ReallyExecuted_UnlistedReturn_UsesUnrecognizedFallback()
    {
        var result = await RunProductionPauseScriptAsync(
            BuildStubHostPrelude(settledState: "Paused", forcedReturnValue: 32769));

        var stderr = NormalizeErrorText(result.Stderr);
        result.ExitCode.Should().NotBe(0, "an unaccepted return must fail the script");
        stderr.Should().Contain("returned 32769 (Unrecognized return code)",
            "a code outside the decode table MUST use the explicit fallback.");
        stderr.Should().NotContain("Invalid State Transition",
            "an unlisted code MUST NOT be mis-decoded as a listed meaning.");
        stderr.Should().NotContain("Use of Timeout Parameter Not Supported",
            "an unlisted code MUST NOT be mis-decoded as a listed meaning.");
    }

    // ── Guard 3: settle source and exact 'Paused' matching ───────────────────

    /// <summary>
    /// Issue #289 convention (substring matching has bitten this codebase three times):
    /// 'PausedCritical' MUST NOT satisfy the success condition. The stub settles the Get-VM state
    /// to PausedCritical after an ACCEPTED RequestStateChange, so only the exactness of the
    /// comparison decides the outcome.
    /// </summary>
    /// <remarks>
    /// 'PausedCritical' is in neither break-set, so the loop polls it to budget expiry and the
    /// failure is classified as an exhausted wait — the conservative bucket every unmodelled exit
    /// path falls into by design.
    /// See internal documentation — LF-D34.
    /// </remarks>
    [Fact(Timeout = 120_000)]
    public async Task ProductionPauseScript_ReallyExecuted_PausedCriticalDoesNotSatisfySuccess()
    {
        var result = await RunProductionPauseScriptAsync(
            BuildStubHostPrelude(settledState: "PausedCritical"));

        var stderr = NormalizeErrorText(result.Stderr);
        result.ExitCode.Should().NotBe(0,
            "'PausedCritical' MUST NOT be accepted as a successful pause settle.");
        stderr.Should().Contain("vm_pause:wait_exhausted",
            "the settle guard MUST reject a state that merely CONTAINS 'Paused'.");
        stderr.Should().Contain("PausedCritical",
            "the failure MUST carry the observed state for diagnosability.");
    }

    /// <summary>
    /// The settle decision must derive from Get-VM .State only. CIM EnabledState is a different
    /// enum (6 means 'Enabled' there, not 'Paused'); reading it was the conflation class that
    /// produced #291.
    /// </summary>
    [Fact]
    public void PauseScript_SettleReadsGetVmStateOnly_NeverCimEnabledState()
    {
        var script = CaptureProductionPauseScript();

        script.Should().Contain("$vm.State -eq 'Paused'",
            "the settle-poll MUST compare Get-VM .State exactly against 'Paused'.");
        script.Should().NotContain("EnabledState",
            "the settle decision MUST NOT read the CIM EnabledState enum (LF-D32).");
    }

    // ── Guard 4: in-script vs manager-side message parity ────────────────────

    /// <summary>
    /// The settle-failure text exists twice — the in-script throw and the manager-side
    /// InvalidOperationException — and can drift (Gate 8 finding). Pin them to one shape.
    /// </summary>
    [Fact]
    public async Task SettleFailureMessage_IsIdenticalInScriptAndInManager()
    {
        const string observedState = "Off";
        var recorder = new RecordingPowerShellExecutor($$"""
        { "Id": "{{TestVmId}}", "Name": "fake-vm", "State": "Off", "ProcessorCount": 2, "MemoryMB": 2048, "UptimeSeconds": 60 }
        """);
        var options = BuildOptions();
        var manager = new HyperVManager(
            recorder,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());

        var act = async () => await manager.PauseVmAsync(LocalHostId, TestVmId);
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        var managerMessage = thrown.Which.Message;

        managerMessage.Should().Be(
            $"vm_pause did not settle into 'Paused'; observed state '{observedState}'.",
            "the manager-side guard message is the canonical shape.");

        // The in-script throw is the same sentence with the PowerShell interpolation token in
        // place of the C# one; anything else means the two copies have drifted.
        var script = recorder.LastScript!;
        script.Should().Contain(
            "throw \"vm_pause did not settle into 'Paused'; observed state '$($vm.State)'.\"",
            "the in-script throw MUST stay byte-parity with the manager-side message.");
    }

    // ── Guard 5: live round-trip (environment-gated, skipped by default) ─────

    /// <summary>
    /// Live pause → assert Paused → resume round-trip. #291 is host-dependent to VERIFY, so this
    /// is the only tier that observes a real guest. Gated on HYPERV_MCP_RUN_REAL_PS==1; discovers
    /// a Running guest rather than hard-coding a GUID (the guests named in #291 no longer exist).
    /// xUnit v2 has no first-class skip, so a gated-off run early-returns and REPORTS AS PASSED —
    /// look for the "SKIP:" line in the output.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task LivePauseResumeRoundTrip_ReachesPausedThenRunning()
    {
        if (Environment.GetEnvironmentVariable("HYPERV_MCP_RUN_REAL_PS") != "1")
        {
            _output.WriteLine(
                "SKIP: LivePauseResumeRoundTrip_ReachesPausedThenRunning — HYPERV_MCP_RUN_REAL_PS not set to 1 " +
                "(pausing a real guest is operator opt-in).");
            return;
        }

        if (!HyperVAvailable() || !IsElevated())
        {
            _output.WriteLine(
                "SKIP: LivePauseResumeRoundTrip_ReachesPausedThenRunning — requires an elevated process on a Hyper-V host.");
            return;
        }

        var executor = new PowerShellExecutor(
            NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>());

        var discovery = await executor.ExecuteAsync(
            "Import-Module Hyper-V -ErrorAction Stop; " +
            "$vm = Get-VM -ComputerName localhost | Where-Object { $_.State -eq 'Running' } | Select-Object -First 1; " +
            "if ($null -eq $vm) { Write-Output 'NO-RUNNING-VM' } else { Write-Output $vm.Id.Guid }",
            timeoutSeconds: 60);

        var vmId = discovery.Stdout.Trim();
        if (discovery.ExitCode != 0 || vmId.Length == 0 || vmId == "NO-RUNNING-VM")
        {
            _output.WriteLine(
                $"SKIP: LivePauseResumeRoundTrip_ReachesPausedThenRunning — no Running guest discovered (exit={discovery.ExitCode}).");
            return;
        }

        _output.WriteLine($"Live pause round-trip on discovered running guest {vmId}.");

        var options = BuildOptions();
        var manager = new HyperVManager(
            executor,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());

        try
        {
            var paused = await manager.PauseVmAsync(LocalHostId, vmId);
            paused.State.Should().Be("Paused",
                "#291: a real vm_pause MUST reach in-memory Paused on a live guest.");
        }
        finally
        {
            // Always attempt to hand the guest back Running — a test must not leave an
            // operator's VM paused.
            try
            {
                var resumed = await manager.ResumeVmAsync(LocalHostId, vmId);
                _output.WriteLine($"Resume returned state '{resumed.State}'.");
            }
            catch (Exception resumeEx)
            {
                _output.WriteLine(
                    $"Cleanup WARNING: failed to resume VM {vmId}: {resumeEx.Message}. Manual resume may be required.");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool HyperVAvailable()
    {
        try
        {
            var vmms = System.ServiceProcess.ServiceController
                .GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, "vmms", StringComparison.OrdinalIgnoreCase));
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
