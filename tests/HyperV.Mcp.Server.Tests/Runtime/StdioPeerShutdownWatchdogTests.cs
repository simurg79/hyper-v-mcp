using FluentAssertions;
using HyperV.Mcp.Server;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>Exit codes must distinguish clean shutdown from disposal hangs; force-exit must not fire on healthy shutdown.</summary>
[Trait("Category", "Runtime")]
public class StdioPeerShutdownWatchdogTests
{
    private static StdioPeerShutdownWatchdog CreateWatchdog() =>
        new(NullLogger.Instance, TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task CleanEof_StopsApplication_And_DoesNotForceExit()
    {
        var stopRequested = false;
        var exitCodes = new List<int>();

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => stopRequested = true,
            _ => Task.FromResult(true),
            exitCodes.Add);

        stopRequested.Should().BeTrue("stdin EOF must still request a graceful shutdown.");
        forced.Should().BeFalse("a shutdown that completes in time must not be force-exited.");
        exitCodes.Should().BeEmpty("the pre-fix bug called Environment.Exit(0) even after a clean shutdown.");
    }

    /// <summary>A real disposal hang must exit non-zero so it remains diagnosable.</summary>
    [Fact]
    public async Task ShutdownHang_ForceExits_NonZero()
    {
        var exitCodes = new List<int>();

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => { },
            _ => Task.FromResult(false),
            exitCodes.Add);

        forced.Should().BeTrue("an incomplete shutdown must hit the force-exit backstop.");
        exitCodes.Should().ContainSingle().Which
            .Should().Be(StdioPeerShutdownWatchdog.ShutdownHangExitCode)
            .And.NotBe(0, "exit code 0 would mask a real hang as a clean exit.");
    }

    [Fact]
    public async Task FaultedTransport_DoesNotThrow_And_StopsApplication()
    {
        var stopRequested = false;

        var forced = await CreateWatchdog().WatchAsync(
            Task.FromException(new IOException("pipe broken")),
            () => false,
            () => stopRequested = true,
            _ => Task.FromResult(true),
            _ => { });

        stopRequested.Should().BeTrue("a transport fault must still shut the host down cleanly.");
        forced.Should().BeFalse();
    }

    [Fact]
    public async Task ShutdownAlreadyRequested_DoesNotStopAgain_ButArmsBackstop()
    {
        var stopCallCount = 0;
        var gracePeriodsObserved = new List<TimeSpan>();
        var exitCodes = new List<int>();

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => true,
            () => stopCallCount++,
            grace =>
            {
                gracePeriodsObserved.Add(grace);
                return Task.FromResult(false);
            },
            exitCodes.Add);

        stopCallCount.Should().Be(0, "a second shutdown must not be started.");
        gracePeriodsObserved.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(200));
        exitCodes.Should().ContainSingle().Which.Should().Be(StdioPeerShutdownWatchdog.ShutdownHangExitCode);
        forced.Should().BeTrue("peer disconnect must arm the backstop even when another route requested shutdown.");
    }

    /// <summary>Grace must include host and DI disposal, where SessionStore and PowerShellHost dispose. Completing at ApplicationStopped
    /// would misreport disposal hangs as success.</summary>
    [Fact]
    public async Task DisposalStillRunning_ForceExits_NonZero()
    {
        var hostDisposalCompleted = new TaskCompletionSource();
        var exitCodes = new List<int>();

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => { },
            StdioPeerShutdownWatchdog.CreateDisposalAwareWaiter(hostDisposalCompleted.Task, NullLogger.Instance),
            exitCodes.Add);

        forced.Should().BeTrue("disposal never finished, so the backstop must fire.");
        exitCodes.Should().ContainSingle().Which
            .Should().Be(StdioPeerShutdownWatchdog.ShutdownHangExitCode);
    }

    [Fact]
    public async Task DisposalCompletesWithinGrace_DoesNotForceExit()
    {
        var hostDisposalCompleted = new TaskCompletionSource();
        var exitCodes = new List<int>();
        var waiter = StdioPeerShutdownWatchdog.CreateDisposalAwareWaiter(
            hostDisposalCompleted.Task, NullLogger.Instance);

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => { },
            grace =>
            {
                var completion = waiter(grace);
                completion.IsCompleted.Should().BeFalse("the waiter must observe disposal still in progress.");
                // Complete inline after the real waiter starts; thread-pool load must not simulate a disposal hang.
                hostDisposalCompleted.SetResult();
                return completion;
            },
            exitCodes.Add);

        forced.Should().BeFalse("disposal finished in time.");
        exitCodes.Should().BeEmpty();
    }

    /// <summary>Faulted shutdown is not clean: reporting success would exit 0 and leave the exception unobserved.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedDisposal_IsNotCleanSuccess_And_IsLogged(bool alreadyFaultedBeforeWait)
    {
        var hostDisposalCompleted = new TaskCompletionSource();
        var disposalFailure = new InvalidOperationException("SessionStore.Dispose blew up");
        var logger = new RecordingLogger<Program>();
        var exitCodes = new List<int>();

        if (alreadyFaultedBeforeWait)
        {
            hostDisposalCompleted.TrySetException(disposalFailure);
        }
        var waiter = StdioPeerShutdownWatchdog.CreateDisposalAwareWaiter(hostDisposalCompleted.Task, logger);

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => { },
            grace =>
            {
                var completion = waiter(grace);
                if (!alreadyFaultedBeforeWait)
                {
                    completion.IsCompleted.Should().BeFalse("disposal has not faulted yet.");
                    hostDisposalCompleted.SetException(disposalFailure);
                }
                return completion;
            },
            exitCodes.Add);

        forced.Should().BeTrue("a shutdown that threw must not be reported as a clean, timely completion.");
        exitCodes.Should().ContainSingle().Which
            .Should().Be(StdioPeerShutdownWatchdog.ShutdownHangExitCode)
            .And.NotBe(0, "exiting 0 would manufacture a false success signal for a failed shutdown.");
        logger.At(LogLevel.Error).Should().Contain(message => message.Contains("disposal failed"),
            "the failure must be diagnosable, not swallowed.");
    }

    [Fact]
    public async Task CanceledDisposal_IsNotCleanSuccess()
    {
        var hostDisposalCompleted = new TaskCompletionSource();
        hostDisposalCompleted.TrySetCanceled();
        var exitCodes = new List<int>();

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => { },
            StdioPeerShutdownWatchdog.CreateDisposalAwareWaiter(hostDisposalCompleted.Task, NullLogger.Instance),
            exitCodes.Add);

        forced.Should().BeTrue("a canceled shutdown is not a verified clean shutdown.");
        exitCodes.Should().ContainSingle().Which.Should().Be(StdioPeerShutdownWatchdog.ShutdownHangExitCode);
    }

    /// <summary>The host must report its true outcome to the waiter; cover production Program wiring too.</summary>
    [Fact]
    public async Task ProductionWiring_ThrowingHostRun_ForceExitsNonZero()
    {
        var hostDisposalCompleted = new TaskCompletionSource();
        var hostFailure = new InvalidOperationException("host shutdown threw");
        var exitCodes = new List<int>();

        var hostRun = Program.RunHostAndSignalDisposalAsync(
            () => Task.FromException(hostFailure),
            hostDisposalCompleted);

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => { },
            StdioPeerShutdownWatchdog.CreateDisposalAwareWaiter(hostDisposalCompleted.Task, NullLogger.Instance),
            exitCodes.Add);

        var observedHostFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => hostRun);
        observedHostFailure.Message.Should().Be("host shutdown threw");
        forced.Should().BeTrue("a host run that threw must not be signaled to the watchdog as success.");
        exitCodes.Should().ContainSingle().Which.Should().Be(StdioPeerShutdownWatchdog.ShutdownHangExitCode);
    }

    [Fact]
    public async Task ProductionWiring_CleanHostRun_DoesNotForceExit()
    {
        var hostDisposalCompleted = new TaskCompletionSource();
        var exitCodes = new List<int>();

        await Program.RunHostAndSignalDisposalAsync(() => Task.CompletedTask, hostDisposalCompleted);

        var forced = await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => { },
            StdioPeerShutdownWatchdog.CreateDisposalAwareWaiter(hostDisposalCompleted.Task, NullLogger.Instance),
            exitCodes.Add);

        forced.Should().BeFalse("a clean host exit must remain a clean exit.");
        exitCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task GracePeriod_IsAwaited_BeforeForceExit()
    {
        var gracePeriodsObserved = new List<TimeSpan>();

        await CreateWatchdog().WatchAsync(
            Task.CompletedTask,
            () => false,
            () => { },
            grace =>
            {
                gracePeriodsObserved.Add(grace);
                return Task.FromResult(true);
            },
            _ => { });

        gracePeriodsObserved.Should().ContainSingle().Which
            .Should().Be(TimeSpan.FromMilliseconds(200), "the configured grace period must be passed through.");
    }
}
