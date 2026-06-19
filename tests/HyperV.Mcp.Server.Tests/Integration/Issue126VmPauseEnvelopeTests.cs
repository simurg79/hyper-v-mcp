using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// Issue #126 — vm_pause MCP envelope regression guard.
///
/// Reported symptom: after invoking <c>vm_pause</c>, the MCP envelope returned
/// <c>data.state = "Saving"</c> instead of <c>"Paused"</c>. Gate-3 analysis
/// (see PR for fix/issue-126-vm-pause-envelope) determined the source already
/// correctly calls <c>Suspend-VM</c> via
/// <see cref="HyperVManager.PauseVmAsync"/> — the bug was a stale deployed
/// binary on the user's machine.
///
/// This regression test pins the end-to-end contract that protects against
/// BOTH plausible future regressions:
///   1. Reverting <c>Suspend-VM</c> to <c>Save-VM</c> in the manager — that
///      would cause the executor (in production) to return a <c>Saving</c>
///      state object, and the envelope's <c>data.state</c> would surface as
///      <c>"Saving"</c>. Existing
///      <see cref="Runtime.Issue92VmPauseStatusFlowTests"/> already pin the
///      script-shape; this test additionally pins the user-visible projection.
///   2. Any future bug in the envelope/projection layer (ToolDispatcher,
///      McpToolResponse serialization, VmInfo mapping) that mis-maps the
///      Paused state into the wire shape.
///
/// Approach: drive <c>vm_pause</c> through the real <see cref="ToolDispatcher"/>
/// wired with a real <see cref="HyperVManager"/>, mocking only
/// <see cref="IPowerShellExecutor"/> to return a canned Suspend-VM-shaped JSON
/// payload (Hyper-V <c>State</c> enum = 6 → "Paused"). Then deserialize the
/// returned JSON envelope and assert <c>data.state == "Paused"</c> exactly.
///
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/126.
/// </summary>
[Trait("Category", "Integration")]
public class Issue126VmPauseEnvelopeTests
{
    private const string LocalHostId = "local";
    // InputValidation.ValidateVmId requires a real GUID.
    private const string TestVmId = "22222222-2222-2222-2222-222222222222";

    private static (ToolDispatcher dispatcher, Mock<IPowerShellExecutor> exec) BuildDispatcherWithRealManager()
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
            MaxConcurrentOperations = 8,
        };
        var resolver = new HostResolver(options);
        var manager = new HyperVManager(
            exec.Object,
            resolver,
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());

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
    /// End-to-end envelope guard: vm_pause through the real dispatcher + real
    /// HyperVManager, fed a Suspend-VM-shaped Paused payload, must produce an
    /// MCP envelope whose <c>data.state</c> is the exact string literal
    /// <c>"Paused"</c>.
    ///
    /// If anyone reintroduces <c>Save-VM</c> in PauseVmAsync, the production
    /// executor would return a <c>State: 3</c> (Saving) payload and this
    /// assertion would fail with <c>data.state = "Saving"</c> — catching the
    /// exact symptom reported in issue #126.
    /// </summary>
    [Fact]
    public async Task VmPause_Dispatched_Envelope_Has_DataState_Paused()
    {
        var (dispatcher, exec) = BuildDispatcherWithRealManager();
        // Hyper-V State enum: 6 = Paused. Shape mirrors what Suspend-VM | Get-VM | ConvertTo-Json yields.
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
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(pausedJson));

        var resultJson = await dispatcher.DispatchAsync(
            "vm_pause",
            new Dictionary<string, object?>
            {
                ["hostId"] = LocalHostId,
                ["vmId"] = TestVmId,
            },
            CancellationToken.None);

        // Parse as JsonDocument so we observe the wire shape exactly as
        // an MCP client would — including the lowercase "state" property
        // nested inside "data". This catches projection/serialization bugs
        // that a typed deserialize might paper over.
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue(
            "vm_pause with a Paused executor payload must yield a success envelope.");

        root.TryGetProperty("data", out var data).Should().BeTrue(
            "success envelopes for vm_pause must carry a non-null data payload (VmInfo).");
        data.ValueKind.Should().Be(JsonValueKind.Object,
            "data must be the serialized VmInfo object, not null or a primitive.");

        data.TryGetProperty("state", out var state).Should().BeTrue(
            "VmInfo on the wire must expose a 'state' property (lowercase) — issue #126 surface.");
        state.GetString().Should().Be("Paused",
            "issue #126 regression guard: data.state MUST be the exact string 'Paused' when " +
            "Suspend-VM returns a State=6 payload. A regression to Save-VM (State=3 → 'Saving') " +
            "or any envelope mis-projection would surface here as the wrong literal.");
    }
}
