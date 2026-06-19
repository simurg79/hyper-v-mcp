using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for SM-D7 (issue #52): vm_destroy must evict any persistent PSSession
/// for the (hostId, vmId) BEFORE calling <c>HyperVManager.DestroyVmAsync</c>.
/// Eviction is best-effort — non-cancellation exceptions must NOT block the
/// destroy operation. <see cref="OperationCanceledException"/> from the channel
/// must propagate so caller cancellation is honored.
///
/// See ToolDispatcher.HandleDestroyAsync.
/// </summary>
[Trait("Category", "Runtime")]
public class VmDestroyEvictsSessionTests
{
    private const string HostId = "local";
    private const string VmId = "12345678-1234-1234-1234-123456789abc";

    private static (Mock<IHyperVManager> hv, Mock<IPowerShellDirectChannel> channel, ToolDispatcher dispatcher)
        CreateDispatcher()
    {
        var hv = new Mock<IHyperVManager>();
        var channel = new Mock<IPowerShellDirectChannel>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        var dispatcher = new ToolDispatcher(
            hv.Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            channel.Object,
            new ServerOptions { DefaultHostId = HostId });

        return (hv, channel, dispatcher);
    }

    private static Dictionary<string, object?> DestroyArgs() => new()
    {
        ["vmId"] = VmId,
        ["hostId"] = HostId,
    };

    [Fact]
    public async Task Destroy_HappyPath_CallsEvictBeforeDestroy()
    {
        var (hv, channel, dispatcher) = CreateDispatcher();

        var sequence = new MockSequence();
        channel.InSequence(sequence)
            .Setup(c => c.EvictSessionAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        hv.InSequence(sequence)
            .Setup(m => m.DestroyVmAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var json = await dispatcher.DispatchAsync("vm_destroy", DestroyArgs(), CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeTrue();

        channel.Verify(c => c.EvictSessionAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once);
        hv.Verify(m => m.DestroyVmAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Destroy_ChannelEvictThrows_NonCancellation_StillCallsDestroy()
    {
        var (hv, channel, dispatcher) = CreateDispatcher();

        channel.Setup(c => c.EvictSessionAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("session removal failed"));
        hv.Setup(m => m.DestroyVmAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var json = await dispatcher.DispatchAsync("vm_destroy", DestroyArgs(), CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeTrue("destroy is the priority — eviction failure must be swallowed");

        channel.Verify(c => c.EvictSessionAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once);
        hv.Verify(m => m.DestroyVmAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Destroy_ChannelEvictThrowsOperationCanceled_PropagatesAndDoesNotDestroy()
    {
        var (hv, channel, dispatcher) = CreateDispatcher();

        // Use a non-cancelled token at dispatch entry so we get past the upfront
        // ct.ThrowIfCancellationRequested() check inside DispatchAsync. The channel
        // mock then throws OperationCanceledException to simulate caller-initiated
        // cancellation propagating out of the eviction step. The ToolDispatcher's
        // HandleDestroyAsync re-throws OCE (does NOT swallow it like other exceptions),
        // so it bubbles out of DispatchAsync. The key invariant: DestroyVmAsync was
        // never invoked.
        channel.Setup(c => c.EvictSessionAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated caller cancel"));
        hv.Setup(m => m.DestroyVmAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Func<Task> act = async () =>
            await dispatcher.DispatchAsync("vm_destroy", DestroyArgs(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "OperationCanceledException from channel eviction must propagate (not be swallowed " +
            "by the best-effort try/catch around EvictSessionAsync).");

        hv.Verify(m => m.DestroyVmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "destroy must not run after eviction was cancelled");
    }
}
