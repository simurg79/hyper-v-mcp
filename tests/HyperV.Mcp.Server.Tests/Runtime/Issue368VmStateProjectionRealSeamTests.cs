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
/// Issue #368: CIM EnabledState mapping misread VMState 9 (Paused) as Saving; pause and settle were correct.
/// Execute the manager's script through PowerShellExecutor against a stub and inspect the payload so
/// a corrected numeric fallback cannot hide a regression from string projection to raw ordinals.
/// </summary>
[Trait("Category", "Runtime")]
[Trait("Category", "RealPowerShell")]
public class Issue368VmStateProjectionRealSeamTests
{
    private readonly ITestOutputHelper _output;

    private const string LocalHostId = "local";
    private const string TestVmId = "36836836-3683-3683-3683-368368368368";

    public Issue368VmStateProjectionRealSeamTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Concrete recording fake rather than a permissive mock, so an unimplemented member fails
    /// loudly instead of silently returning a default.
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
    /// Stub host whose Get-VM returns a real enum instance, exactly as the Hyper-V module does.
    /// The measured VMState ordinals are used so that a raw-ordinal regression would surface the
    /// misleading value (9) rather than a value that happens to look right.
    /// </summary>
    private static string BuildStubHostPrelude(string settledState) => $@"
enum FakeVmState {{ Off = 3; Running = 2; Saved = 6; Paused = 9; PausedCritical = 32769; Saving = 32773; Pausing = 32776 }}
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
    $requested = [int]$Arguments['RequestedState']
    if ($requested -ne 9) {{ return [pscustomobject]@{{ ReturnValue = 32775 }} }}
    $script:FakeState = [FakeVmState]::{settledState}
    return [pscustomobject]@{{ ReturnValue = 0 }}
}}
";

    private static string CaptureProductionPauseScript(out RecordingPowerShellExecutor recorder)
    {
        var settledPausedJson = $$"""
        { "Id": "{{TestVmId}}", "Name": "fake-vm", "State": "Paused", "ProcessorCount": 2, "MemoryMB": 2048, "UptimeSeconds": 60 }
        """;
        recorder = new RecordingPowerShellExecutor(settledPausedJson);
        var manager = BuildManager(recorder);
        manager.PauseVmAsync(LocalHostId, TestVmId).GetAwaiter().GetResult();
        return recorder.LastScript!;
    }

    private async Task<PowerShellResult> ExecuteProductionScriptAsync(string prelude, string productionScript)
    {
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

    // ── Guard 1: the executed projection emits a NAME, never a bare ordinal ──

    /// <summary>
    /// THE #368 guard. Reverting the projection to the raw ordinal makes the executed script emit
    /// State 9, which this test rejects — even though the corrected numeric fallback would decode
    /// 9 back to 'Paused' and leave a name-only assertion green.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ExecutedProjection_EmitsStateName_NotRawOrdinal()
    {
        var script = CaptureProductionPauseScript(out _);
        var result = await ExecuteProductionScriptAsync(BuildStubHostPrelude("Paused"), script);

        result.ExitCode.Should().Be(0, "the executed pause script must settle into Paused");

        using var document = System.Text.Json.JsonDocument.Parse(result.Stdout);
        var state = document.RootElement.GetProperty("State");

        state.ValueKind.Should().Be(System.Text.Json.JsonValueKind.String,
            "#368: State MUST be projected as the enum NAME. A JSON number here means the ordinal " +
            "crossing is reachable again, and the numeric fallback would silently re-decode it.");
        state.GetString().Should().Be("Paused",
            "the projected name must be the state the host actually reached.");
    }

    /// <summary>
    /// The projection is shared by the lifecycle methods, so the stringify MUST live in the shared
    /// projection rather than being patched into the pause path alone.
    /// </summary>
    [Fact]
    public void ComposedScript_StringifiesStateInSharedProjection()
    {
        var script = CaptureProductionPauseScript(out _);

        script.Should().Contain("[string]$_.State",
            "#368: the shared VmInfoProjection MUST stringify State for every lifecycle method.");
    }

    // ── Guard 2: retained numeric fallback still decodes the measured ordinals ──

    /// <summary>
    /// The numeric map is retained for payloads that still arrive as numbers, so its corrected
    /// entries stay covered. 9 is the discriminating case: under the CIM vocabulary it decoded as
    /// 'Saving', which is exactly what made a successful pause look like a failure.
    /// </summary>
    [Theory]
    [InlineData(2, "Running")]
    [InlineData(3, "Off")]
    [InlineData(6, "Saved")]
    [InlineData(9, "Paused")]
    [InlineData(10, "Starting")]
    [InlineData(32773, "Saving")]
    [InlineData(32776, "Pausing")]
    public async Task NumericPayload_DecodesViaCorrectedMeasuredOrdinals(int ordinal, string expectedName)
    {
        var numericJson = $$"""
        { "Id": "{{TestVmId}}", "Name": "fake-vm", "State": {{ordinal}}, "ProcessorCount": 2, "MemoryMB": 2048, "UptimeSeconds": 60 }
        """;
        var manager = BuildManager(new RecordingPowerShellExecutor(numericJson));

        var info = await manager.GetVmStatusAsync(LocalHostId, TestVmId);

        info.State.Should().Be(expectedName,
            "the retained fallback MUST decode the measured PowerShell VMState ordinals, not the " +
            "CIM EnabledState vocabulary that caused #368.");
    }

    /// <summary>
    /// The manager-side settle guard must compare EXACTLY. Substring matching has bitten this
    /// codebase repeatedly, and 'PausedCritical' is a degraded state a caller must not be told is
    /// a clean pause. Asserted at the manager rather than only in-script, because the in-script
    /// stop-set and the C# guard are independent copies.
    /// </summary>
    [Fact]
    public async Task PausedCritical_IsNotAcceptedAsSuccessfulPause()
    {
        var criticalJson = $$"""
        { "Id": "{{TestVmId}}", "Name": "fake-vm", "State": "PausedCritical", "ProcessorCount": 2, "MemoryMB": 2048, "UptimeSeconds": 60 }
        """;
        var manager = BuildManager(new RecordingPowerShellExecutor(criticalJson));

        var act = async () => await manager.PauseVmAsync(LocalHostId, TestVmId);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>(
            "a state that merely CONTAINS 'Paused' MUST NOT satisfy the settle guard.");
        thrown.Which.Message.Should().Contain("PausedCritical",
            "the failure must carry the observed state for diagnosability.");
    }

    /// <summary>An unmapped ordinal stays legible rather than being given a plausible wrong name.</summary>
    [Fact]
    public async Task UnmappedNumericPayload_PreservesUnknownRendering()
    {
        var numericJson = $$"""
        { "Id": "{{TestVmId}}", "Name": "fake-vm", "State": 4242, "ProcessorCount": 2, "MemoryMB": 2048, "UptimeSeconds": 60 }
        """;
        var manager = BuildManager(new RecordingPowerShellExecutor(numericJson));

        var info = await manager.GetVmStatusAsync(LocalHostId, TestVmId);

        info.State.Should().Be("Unknown(4242)",
            "a wrong-but-plausible name would be worse than a legible unknown.");
    }

    // ── Guard 3: a Paused guest is not reported as a failure ──

    /// <summary>
    /// The caller-facing #368 symptom: the guest really reaches Paused, yet vm_pause throws. Driven
    /// through the executed script so the settle comparison and the projection are both real.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ExecutedPause_ReachingPaused_IsNotReportedAsFailure()
    {
        var script = CaptureProductionPauseScript(out _);
        var result = await ExecuteProductionScriptAsync(BuildStubHostPrelude("Paused"), script);

        result.Stderr.Should().NotContain("did not settle",
            "#368: a guest that really reached Paused MUST NOT be reported as a settle failure.");
        result.Stderr.Should().NotContain("Saving",
            "#368: VMState 9 is Paused; decoding it as 'Saving' is the CIM-vocabulary conflation.");
    }
}
