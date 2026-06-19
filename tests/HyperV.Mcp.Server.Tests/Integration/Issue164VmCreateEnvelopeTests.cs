using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// Issue #164 / LF-D17: integration test that drives <c>vm_create</c> end-to-end
/// through the real <see cref="ToolDispatcher"/> + real <see cref="HyperVManager"/>
/// with a fake <see cref="IPowerShellExecutor"/> that simulates a <c>New-VM</c>
/// failure mid-flight.
///
/// Asserts the returned envelope JSON satisfies AC#2:
/// - <c>success == false</c>
/// - <c>errorCode</c> is set (COMMAND_FAILED for the simulated New-VM failure)
/// - <c>details.rollback.performed == true</c>
/// - <c>details.rollback.succeeded == true</c>
/// - <c>details.rollback.residualArtifacts == []</c>
///
/// The live-filesystem cross-check from AC#2 is the live smoke-test concern;
/// mock-based assertions on <c>Details</c> are sufficient here.
/// </summary>
[Trait("Category", "Integration")]
public class Issue164VmCreateEnvelopeTests
{
    private const string LocalHostId = "local";

    /// <summary>
    /// Two-call fake executor: first call (primary New-VM script) fails;
    /// second call (rollback) returns a clean JSON document.
    /// </summary>
    private sealed class TwoStageExecutor : IPowerShellExecutor
    {
        private int _callCount;

        public int PrimaryCalls { get; private set; }
        public int RollbackCalls { get; private set; }

        public Task<PowerShellResult> ExecuteAsync(string script, int timeoutSeconds = 300,
            CancellationToken ct = default, bool allowDump = true)
        {
            // Issue #203 / LF-D19: the new pre-create existence probe runs
            // BEFORE the primary pipeline. Recognise it by its distinctive
            // shape (Get-VM + 'present'/'absent' literals, no state-mutating
            // verbs) and return authoritative 'absent' so this test continues
            // to exercise the primary-failure → rollback path.
            if (IsLfD19ProbeScript(script))
            {
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = "absent",
                    DurationMs = 1,
                });
            }

            // Post-registration failure (Set-VM phase) so VC-DUP-D3 ownership
            // inference marks the VM as owned and full rollback semantics
            // apply — same envelope shape this test was originally written to
            // assert.
            var idx = Interlocked.Increment(ref _callCount);
            if (idx == 1)
            {
                PrimaryCalls++;
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 1,
                    Stdout = string.Empty,
                    Stderr = "Set-VM : The operation failed because of a simulated mid-flight error.",
                    TimedOut = false,
                    Cancelled = false,
                    DurationMs = 50,
                });
            }

            RollbackCalls++;
            return Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = """{"removed":[],"failed":[],"residual":[]}""",
                Stderr = string.Empty,
                DurationMs = 5,
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

    private static ServerOptions BuildOptions(string baseVhdx, string storageRoot) => new()
    {
        DefaultHostId = LocalHostId,
        Hosts = new Dictionary<string, HostProfile>
        {
            [LocalHostId] = new HostProfile
            {
                HostId = LocalHostId,
                ComputerName = "localhost",
                TrustPolicy = "local",
                BaseVhdxPath = baseVhdx,
                StorageRoot = storageRoot,
            },
        },
    };

    private static ToolDispatcher BuildDispatcher(HyperVManager manager, ServerOptions options)
    {
        var gate = new Mock<IConcurrencyGate>();
        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        return new ToolDispatcher(
            manager,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new HostResolver(options),
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);
    }

    [Fact]
    public async Task VmCreate_NewVmFailure_Returns_FailureEnvelope_WithRollbackDetails()
    {
        // Non-existent base VHDX + non-existent storage root ⇒ clean filesystem
        // so the host-side cross-check inside RunCreateRollbackAsync sees no residual.
        var baseVhdx = Path.Combine(Path.GetTempPath(),
            "issue164-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var storage = Path.Combine(Path.GetTempPath(),
            "issue164-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        var exec = new TwoStageExecutor();
        var manager = new HyperVManager(
            exec,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new Runtime.TestIsoInspector(),
            fileSystemProbe: null,
            baseImageHashCache: null /* skip pre-hash (no real base VHDX) */);

        var dispatcher = BuildDispatcher(manager, options);

        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["name"] = "issue164-integration-vm",
                ["hostId"] = LocalHostId,
                ["baseVhdxPath"] = baseVhdx,
                ["cpuCount"] = 2,
                ["memoryMB"] = 4096,
                ["autoStart"] = false,
            },
            CancellationToken.None);

        // Both primary + rollback PowerShell calls must have happened.
        exec.PrimaryCalls.Should().Be(1, "primary New-VM script must be invoked once");
        exec.RollbackCalls.Should().Be(1, "rollback script must be invoked exactly once");

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.TryGetProperty("errorCode", out var errorCode).Should().BeTrue();
        errorCode.GetString().Should().NotBeNullOrWhiteSpace(
            "AC#2: failure envelope must carry an errorCode");
        // The simulated failure is a non-cancel, non-timeout PowerShell exit ⇒ COMMAND_FAILED.
        errorCode.GetString().Should().Be(ErrorCodes.CommandFailed);

        root.TryGetProperty("details", out var details).Should().BeTrue(
            "LF-D17 requires the failure envelope to carry a details block");
        details.ValueKind.Should().Be(JsonValueKind.Object);

        details.GetProperty("vmName").GetString().Should().Be("issue164-integration-vm");
        details.GetProperty("phase").GetString().Should().NotBeNullOrWhiteSpace();

        var rollback = details.GetProperty("rollback");
        rollback.GetProperty("performed").GetBoolean().Should().BeTrue(
            "AC#2: rollback.performed must be true after a primary failure");
        rollback.GetProperty("succeeded").GetBoolean().Should().BeTrue(
            "AC#2: rollback.succeeded must be true when the filesystem is clean");
        rollback.GetProperty("residualArtifacts").ValueKind
            .Should().Be(JsonValueKind.Array);
        rollback.GetProperty("residualArtifacts").GetArrayLength()
            .Should().Be(0, "AC#2: residualArtifacts must be [] on a clean rollback");
    }
}
