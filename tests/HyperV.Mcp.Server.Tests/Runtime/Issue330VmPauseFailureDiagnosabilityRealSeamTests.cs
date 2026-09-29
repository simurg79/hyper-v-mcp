using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #330 — a vm_pause ending anywhere other than 'Paused' must let the caller tell a permanent
/// terminal divergence from an exhausted bounded wait without parsing message prose.
///
/// The pre-existing #291 real-seam guards stop at raw stderr, so a leading-only sentinel match, a
/// wrong observed-state extraction, or a dropped details projection would stay green. These guards
/// carry a real failure through the real manager to the serialized envelope: most run the
/// production-composed script against a stub host, the rest replay already-observed real stderr.
///
/// Spec: myplans/vm-management/lifecycle/vm-pause-spec.md — FR-9…FR-13, AC-7…AC-10.
/// Design: myplans/vm-management/lifecycle/vm-pause-state-settle-design.md — LF-D34…LF-D37.
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/330.
/// </summary>
[Trait("Category", "Runtime")]
[Trait("Category", "RealPowerShell")]
public class Issue330VmPauseFailureDiagnosabilityRealSeamTests
{
    private readonly ITestOutputHelper _output;

    private const string LocalHostId = "local";
    private const string TestVmId = "33033033-3303-3303-3303-330330330330";

    public Issue330VmPauseFailureDiagnosabilityRealSeamTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ── Seams ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes the production-composed script with the real <see cref="PowerShellExecutor"/>
    /// against a stub host, so the manager consumes genuine stderr rather than a hand-written string.
    /// </summary>
    private sealed class StubHostPowerShellExecutor : IPowerShellExecutor
    {
        private readonly string _prelude;

        public StubHostPowerShellExecutor(string prelude) => _prelude = prelude;

        public string? LastScript { get; private set; }

        public PowerShellResult? LastResult { get; private set; }

        public async Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
        {
            LastScript = script;

            // Import-Module is dropped because the prelude shadows the module's cmdlets; every other
            // line is production text verbatim.
            var body = new StringBuilder();
            foreach (var line in script.Split('\n'))
            {
                if (line.TrimStart().StartsWith("Import-Module", StringComparison.Ordinal))
                {
                    continue;
                }

                body.Append(line).Append('\n');
            }

            var real = new PowerShellExecutor(
                NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>());
            var result = await real.ExecuteAsync(_prelude + body, timeoutSeconds: 90, ct: ct);
            LastResult = result;
            return result;
        }
    }

    /// <summary>
    /// Replays already-observed real stderr through the manager, for variants a stub host cannot
    /// produce on demand (a mutated state token, a corrupted sentinel). Classification, mapping and
    /// serialization under test remain the production ones.
    /// </summary>
    private sealed class ReplayingPowerShellExecutor : IPowerShellExecutor
    {
        private readonly string _stderr;
        private readonly int _exitCode;

        public ReplayingPowerShellExecutor(string stderr, int exitCode = 1)
        {
            _stderr = stderr;
            _exitCode = exitCode;
        }

        public Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true) =>
            Task.FromResult(new PowerShellResult
            {
                ExitCode = _exitCode,
                Stdout = string.Empty,
                Stderr = _stderr,
                DurationMs = 1,
            });
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

    private static HyperVManager BuildManager(IPowerShellExecutor executor)
    {
        var options = BuildOptions();
        return new HyperVManager(
            executor,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());
    }

    /// <summary>
    /// Stub host. <paramref name="settledState"/> is what Get-VM reports once RequestStateChange is
    /// accepted; transient values ('Saving', 'PausedCritical') therefore drive the loop to budget
    /// expiry, terminal ones ('Saved', 'Off') drive the divergence branch.
    /// <paramref name="vmName"/> is interpolated so quote-bearing names can be probed.
    /// </summary>
    private static string BuildStubHostPrelude(
        string settledState,
        int? forcedReturnValue = null,
        string vmName = "fake-vm",
        string? stateStepBeforeReturn = null)
    {
        var step = stateStepBeforeReturn is null
            ? "# no out-of-band state step"
            : $"$script:FakeState = [FakeVmState]::{stateStepBeforeReturn}";
        var forced = forcedReturnValue.HasValue
            ? $"{step}\n    return [pscustomobject]@{{ ReturnValue = {forcedReturnValue.Value} }}"
            : "# no forced return";

        return $@"
enum FakeVmState {{ Off = 3; Running = 2; Saved = 6; Paused = 9; PausedCritical = 32769; Saving = 32773; Pausing = 32776 }}
$script:FakeState = [FakeVmState]::Running
function Get-VM {{
    param([string]$Id, [string]$ComputerName, [string]$Name)
    [pscustomobject]@{{
        Id = '{TestVmId}'
        Name = '{vmName}'
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
    if ($requested -ne 9) {{ return [pscustomobject]@{{ ReturnValue = 32775 }} }}
    $script:FakeState = [FakeVmState]::{settledState}
    return [pscustomobject]@{{ ReturnValue = 0 }}
}}
";
    }

    private sealed record PauseFailure(Exception Thrown, string RealStderr, JsonElement Envelope);

    /// <summary>
    /// Drives the whole production chain: composed script → real PowerShell execution against the
    /// stub host → HandleError → ClassifyPauseFailure → ErrorMapper → serialized envelope.
    /// </summary>
    private async Task<PauseFailure> RunRealPauseFailureAsync(string prelude)
    {
        var executor = new StubHostPowerShellExecutor(prelude);
        var manager = BuildManager(executor);

        Exception? thrown = null;
        try
        {
            await manager.PauseVmAsync(LocalHostId, TestVmId);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        executor.LastScript.Should().Contain("RequestStateChange",
            "the executed text must be the production pause script, not a test-local rewrite");
        thrown.Should().NotBeNull("a non-'Paused' settle MUST surface as a caller-facing failure");

        var realStderr = executor.LastResult!.Stderr;
        _output.WriteLine($"exit={executor.LastResult!.ExitCode}");
        _output.WriteLine($"real stderr: {realStderr.Trim()}");
        _output.WriteLine($"thrown: {thrown!.GetType().Name}: {thrown.Message}");

        return new PauseFailure(thrown, realStderr, SerializeEnvelope(thrown));
    }

    private static JsonElement SerializeEnvelope(Exception thrown)
    {
        var response = new ErrorMapper().MapException(thrown);
        var json = JsonSerializer.Serialize(response, JsonOptions.Default);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static (string Code, string? PauseOutcome, string? ObservedState) ReadEnvelope(JsonElement envelope)
    {
        var code = envelope.GetProperty("errorCode").GetString()!;
        if (!envelope.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return (code, null, null);
        }

        var outcome = details.TryGetProperty("pauseOutcome", out var outcomeElement) ? outcomeElement.GetString() : null;
        var observed = details.TryGetProperty("observedState", out var observedElement) ? observedElement.GetString() : null;
        return (code, outcome, observed);
    }

    /// <summary>Replays a real-shaped failure message through the production classification chain.</summary>
    private static async Task<JsonElement> ReplayEnvelopeAsync(string stderr)
    {
        var manager = BuildManager(new ReplayingPowerShellExecutor(stderr));
        try
        {
            await manager.PauseVmAsync(LocalHostId, TestVmId);
        }
        catch (Exception ex)
        {
            return SerializeEnvelope(ex);
        }

        throw new InvalidOperationException("The replayed failure did not produce an exception.");
    }

    // ── Guard 1 — divergence reaches the caller as VM_STATE_CONFLICT (FR-9 / AC-7) ──

    [Theory(Timeout = 180_000)]
    [InlineData("Saved")]
    [InlineData("Off")]
    public async Task RealPause_TerminalDivergence_SerializesVmStateConflictWithOutcomeAndObservedState(string terminalState)
    {
        var failure = await RunRealPauseFailureAsync(BuildStubHostPrelude(terminalState));
        var (code, outcome, observed) = ReadEnvelope(failure.Envelope);

        code.Should().Be(ErrorCodes.VmStateConflict,
            "FR-9: a terminal state other than 'Paused' is permanently unsatisfiable as issued and " +
            "MUST NOT be reported as a generic command failure.");
        outcome.Should().Be(PauseOutcomes.ConflictingTerminalState,
            "AC-7: details.pauseOutcome is the caller's machine-readable discriminator.");
        observed.Should().Be(terminalState,
            "AC-7: details.observedState MUST name the state actually observed at abandon.");
    }

    // ── Guard 2 — wait exhaustion reaches the caller as COMMAND_FAILED (FR-10 / AC-8) ──

    [Theory(Timeout = 180_000)]
    [InlineData("PausedCritical")]
    [InlineData("Saving")]
    public async Task RealPause_WaitExhaustion_SerializesCommandFailedWithLastObservedState(string transientState)
    {
        var failure = await RunRealPauseFailureAsync(BuildStubHostPrelude(transientState));
        var (code, outcome, observed) = ReadEnvelope(failure.Envelope);

        code.Should().Be(ErrorCodes.CommandFailed,
            "FR-10: an elapsed bounded wait leaves the outcome UNKNOWN — it is not a proven conflict.");
        outcome.Should().Be(PauseOutcomes.WaitExhausted,
            "AC-8: the exhausted wait MUST be named in details.pauseOutcome.");
        observed.Should().Be(transientState,
            "AC-8: the last observed state MUST be reported so the caller can retry knowingly.");
    }

    /// <summary>
    /// LF-D37: 'Saving' is transient. It must keep the loop polling to budget expiry rather than
    /// being treated as a terminal divergence — asserted above via the wait_exhausted outcome, and
    /// pinned here at the script level so the break-set cannot silently gain 'Saving'.
    /// See myplans/vm-management/lifecycle/vm-pause-state-settle-design.md — LF-D37.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task RealPause_SavingIsTransient_NeverClassifiedAsDivergence()
    {
        var failure = await RunRealPauseFailureAsync(BuildStubHostPrelude("Saving"));
        var (code, outcome, _) = ReadEnvelope(failure.Envelope);

        outcome.Should().NotBe(PauseOutcomes.ConflictingTerminalState,
            "LF-D37: 'Saving' is a transition, not a terminal state.");
        outcome.Should().Be(PauseOutcomes.WaitExhausted);
        code.Should().Be(ErrorCodes.CommandFailed);
    }

    /// <summary>
    /// The live spike proved a genuine exhaustion's fresh read can be 'Saved' — the same token a
    /// divergence reports. Classification MUST come from the recorded loop-exit reason only, so a
    /// wait_exhausted failure naming 'Saved' must still map to COMMAND_FAILED.
    /// See myplans/vm-management/lifecycle/vm-pause-state-settle-design.md — LF-D34, LF-D37.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Exhaustion_ReportingSavedState_StillMapsToCommandFailed_NotDivergence()
    {
        var exhaustion = await RunRealPauseFailureAsync(BuildStubHostPrelude("Saving"));
        var mutated = exhaustion.RealStderr.Replace("'Saving'", "'Saved'", StringComparison.Ordinal);
        mutated.Should().NotBe(exhaustion.RealStderr, "the real exhaustion message must name its observed state");

        var (code, outcome, observed) = ReadEnvelope(await ReplayEnvelopeAsync(mutated));

        code.Should().Be(ErrorCodes.CommandFailed,
            "the state TOKEN must never classify — only the recorded loop-exit reason may.");
        outcome.Should().Be(PauseOutcomes.WaitExhausted);
        observed.Should().Be("Saved");
    }

    // ── Guard 3 — the two outcomes are separable without parsing prose (FR-11 / AC-9) ──

    [Fact(Timeout = 180_000)]
    public async Task Divergence_And_Exhaustion_AreProgrammaticallyDistinguishable()
    {
        var divergence = ReadEnvelope((await RunRealPauseFailureAsync(BuildStubHostPrelude("Saved"))).Envelope);
        var exhaustion = ReadEnvelope((await RunRealPauseFailureAsync(BuildStubHostPrelude("Saving"))).Envelope);

        divergence.PauseOutcome.Should().NotBe(exhaustion.PauseOutcome,
            "FR-11: the discriminator must separate the two classes on its own.");
        divergence.Code.Should().NotBe(exhaustion.Code,
            "FR-11: the taxonomy code must separate them too, so neither channel alone is load-bearing.");

        divergence.PauseOutcome.Should().BeOneOf(PauseOutcomes.ConflictingTerminalState, PauseOutcomes.WaitExhausted);
        exhaustion.PauseOutcome.Should().BeOneOf(PauseOutcomes.ConflictingTerminalState, PauseOutcomes.WaitExhausted);
    }

    // ── Guard 4 — the sentinel is matched ANYWHERE, never as a prefix ──

    /// <summary>
    /// THE Gate 8 regression guard. HandleError prepends "PowerShell execution failed (exit code
    /// N): " and PowerShell adds record decoration, so the sentinel is never leading in the message
    /// the manager classifies. This test fails if IndexOf is ever changed to StartsWith.
    /// See myplans/vm-management/lifecycle/vm-pause-state-settle-design.md — LF-D35.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Sentinel_IsNotLeadingInTheRealMessage_YetStillClassifies()
    {
        var failure = await RunRealPauseFailureAsync(BuildStubHostPrelude("Saved"));
        var message = failure.Thrown.Message;

        message.Should().StartWith("PowerShell execution failed",
            "the manager classifies the message HandleError composed, which always carries a preamble.");
        message.IndexOf(PauseOutcomes.SentinelPrefix, StringComparison.Ordinal).Should().BeGreaterThan(0,
            "a prefix matcher would silently never fire on the real message.");

        ReadEnvelope(failure.Envelope).Code.Should().Be(ErrorCodes.VmStateConflict,
            "classification MUST succeed despite the non-leading sentinel.");
    }

    // ── Guard 5 — unrecognized or absent sentinel falls through with no details ──

    [Theory(Timeout = 60_000)]
    [InlineData("PowerShell execution failed (exit code 1): vm_pause:teleported the VM went sideways; observed state 'Off'.")]
    [InlineData("PowerShell execution failed (exit code 1): vm_pause: the VM went sideways; observed state 'Off'.")]
    [InlineData("PowerShell execution failed (exit code 1): the VM went sideways; observed state 'Off'.")]
    public async Task UnrecognizedOrAbsentSentinel_FallsThroughToCommandFailed_WithNoDetails(string stderr)
    {
        var envelope = await ReplayEnvelopeAsync(stderr);
        var (code, outcome, observed) = ReadEnvelope(envelope);

        code.Should().Be(ErrorCodes.CommandFailed,
            "an unrecognised outcome MUST NOT be guessed into a conflict.");
        outcome.Should().BeNull("no details block may be projected for an unclassified failure.");
        observed.Should().BeNull();
        envelope.TryGetProperty("details", out _).Should().BeFalse(
            "'details' MUST be absent, so callers can test presence rather than null-ness.");
    }

    // ── Guard 6 — the reported state is read at abandon time, not the stale capture (FR-13 / AC-10) ──

    /// <summary>
    /// Both abandon paths must name a FRESH read. The stub leaves the VM 'Running' until
    /// RequestStateChange is accepted, so a stale pre-request capture would report 'Running' — the
    /// one state that implies nothing is wrong (the live-observed failure mode at the 32775 site).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task SettleAbandon_ReportsFreshState_NotTheStalePreRequestCapture()
    {
        var failure = await RunRealPauseFailureAsync(BuildStubHostPrelude("Saved"));
        var (_, _, observed) = ReadEnvelope(failure.Envelope);

        observed.Should().Be("Saved");
        observed.Should().NotBe("Running",
            "FR-13: reporting the stale pre-request state would hide the divergence entirely.");
    }

    /// <summary>
    /// The reworked 32775 throw site must re-read before naming a state as well. The stub rejects a
    /// forced 32775 while its state is already 'Saved', so a missing re-read reports 'Running'.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Return32775Throw_ReportsFreshState_NotTheStalePreRequestCapture()
    {
        // The VM moves to 'Saved' underneath the rejected request — exactly the concurrent-shutdown
        // race FR-13 describes, where the stale pre-request capture still reads 'Running'.
        var failure = await RunRealPauseFailureAsync(
            BuildStubHostPrelude("Saved", forcedReturnValue: 32775, stateStepBeforeReturn: "Saved"));
        var normalized = NormalizeErrorText(failure.Thrown.Message);

        normalized.Should().Contain("returned 32775 (Invalid State Transition)");
        normalized.Should().Contain("observed Get-VM state 'Saved'",
            "FR-13: the 32775 throw MUST re-read Get-VM before naming a state.");
        normalized.Should().NotContain("observed Get-VM state 'Running'",
            "the stale pre-request capture reads 'Running' during a concurrent shutdown.");
    }

    /// <summary>
    /// The unclassified pause failure must keep the stack it was thrown with. Rethrowing the caught
    /// variable would re-anchor the trace at the catch site and erase the HandleError origin frame —
    /// the only evidence of where an unexpected pause failure actually came from. The serialized
    /// envelope is byte-identical either way, so the exception itself is inspected here.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task UnclassifiedRealPauseFailure_PreservesOriginStack_ThroughHandleErrorFrame()
    {
        var failure = await RunRealPauseFailureAsync(
            BuildStubHostPrelude("Saved", forcedReturnValue: 32775, stateStepBeforeReturn: "Saved"));

        failure.Thrown.GetType().Should().Be(typeof(InvalidOperationException),
            "the forced 32775 failure carries no recognised sentinel, so it MUST fall through " +
            "unclassified rather than being promoted to VmStateConflictException.");

        var originFrames = new StackTrace(failure.Thrown, fNeedFileInfo: false)
            .GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}")
            .ToList();

        _output.WriteLine("frames: " + string.Join(" | ", originFrames));

        originFrames.Should().Contain(
            $"{typeof(HyperVManager).FullName}.HandleError",
            "the throw origin frame survives only under a bare rethrow; rethrowing the caught " +
            "variable truncates the stack to the catch site.");
    }

    // ── Guard 7 — ExtractObservedState robustness probe ──

    /// <summary>
    /// The observed state is lifted as the LAST single-quoted token. These cases pin the behaviour
    /// the production parse actually has, including where it is fragile — a wrong observedState is
    /// more insidious than no observedState, so the boundaries are made visible rather than hidden.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(
        "PowerShell execution failed (exit code 1): vm_pause:conflicting_terminal_state the VM reached a terminal state other than 'Paused'; observed state 'Saved'. The pause cannot succeed as issued.",
        "Saved")]
    [InlineData(
        "PowerShell execution failed (exit code 1): vm_pause:wait_exhausted the bounded wait elapsed without the VM settling; state observed when the wait was abandoned was 'Saving'.",
        "Saving")]
    [InlineData(
        "PowerShell execution failed (exit code 1): vm 'bob's-vm' failed. vm_pause:wait_exhausted state observed when the wait was abandoned was 'Off'.",
        "Off")]
    public async Task ExtractObservedState_LiftsTheLastQuotedToken(string stderr, string expectedObservedState)
    {
        var (_, _, observed) = ReadEnvelope(await ReplayEnvelopeAsync(stderr));
        observed.Should().Be(expectedObservedState);
    }

    /// <summary>
    /// Documented fragility: any quoted token appearing AFTER the state token is lifted instead.
    /// Stderr spill and PowerShell record decoration are both appended after the thrown text, so
    /// this is reachable in principle. Recorded as an observed limitation, not a sanctioned design.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ExtractObservedState_IsFragile_WhenAQuotedTokenTrailsTheStateToken()
    {
        const string stderr =
            "PowerShell execution failed (exit code 1): vm_pause:wait_exhausted state observed when the " +
            "wait was abandoned was 'Saving'. At line:1 char:1 in 'C:\\Temp\\script.ps1'";

        var (code, outcome, observed) = ReadEnvelope(await ReplayEnvelopeAsync(stderr));

        code.Should().Be(ErrorCodes.CommandFailed, "the outcome classification is unaffected by the parse");
        outcome.Should().Be(PauseOutcomes.WaitExhausted);
        observed.Should().Be("C:\\Temp\\script.ps1",
            "OBSERVED LIMITATION: a trailing quoted token displaces the state token. Reported to the " +
            "issue rather than papered over — the classification channel is unaffected.");
    }

    [Fact(Timeout = 60_000)]
    public async Task ExtractObservedState_WithNoQuotedToken_YieldsUnknown_NotAnEmptyString()
    {
        const string stderr =
            "PowerShell execution failed (exit code 1): vm_pause:wait_exhausted the bounded wait elapsed.";

        var (_, outcome, observed) = ReadEnvelope(await ReplayEnvelopeAsync(stderr));

        outcome.Should().Be(PauseOutcomes.WaitExhausted, "extraction failure MUST NOT suppress classification");
        observed.Should().Be("Unknown", "an absent state must be explicit, never blank or null.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// PowerShell wraps long error text across lines with '|' gutters; strip them so assertions
    /// test the message rather than the console's wrap width.
    /// </summary>
    private static string NormalizeErrorText(string text) =>
        string.Join(' ', text.Replace("|", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
