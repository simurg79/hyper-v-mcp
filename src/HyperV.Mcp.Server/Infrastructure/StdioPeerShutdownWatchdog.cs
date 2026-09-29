using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>EOF shutdown prevents surviving runspaces/SessionStore from leaving rotated stdio pipes "Not connected" until kill+reload.
/// Force-exit is only a disposal-hang backstop. See myplans/mcp-interface/stdio-peer-lifecycle/stdio-peer-lifecycle-spec.md.</summary>
public sealed class StdioPeerShutdownWatchdog
{
    /// <summary>Use unclean exit 3 for expired grace or thrown shutdown: both leave disposal unverified; exit 0 would hide failure.</summary>
    public const int ShutdownHangExitCode = 3;

    /// <summary>Wait for host AND DI disposal. ApplicationStopped fires earlier and would let SessionStore/PowerShellHost disposal hangs escape the grace period.</summary>
    public static Func<TimeSpan, Task<bool>> CreateDisposalAwareWaiter(Task hostDisposalCompleted, ILogger logger) =>
        async gracePeriod =>
        {
            var finished = hostDisposalCompleted.IsCompleted
                ? hostDisposalCompleted
                : await Task.WhenAny(hostDisposalCompleted, Task.Delay(gracePeriod)).ConfigureAwait(false);

            if (finished != hostDisposalCompleted)
            {
                return false;
            }

            try
            {
                // Awaited, not merely tested for completion: a faulted or canceled shutdown
                // is still "completed", so reporting it clean would exit 0 on a failure and
                // leave the exception unobserved.
                await hostDisposalCompleted.ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Host shutdown/disposal failed; treating it as an unclean shutdown.");
                return false;
            }
        };

    private readonly ILogger _logger;
    private readonly TimeSpan _gracePeriod;

    public StdioPeerShutdownWatchdog(ILogger logger, TimeSpan? gracePeriod = null)
    {
        _logger = logger;
        _gracePeriod = gracePeriod ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Await the SDK transport read loop, then drive shutdown; return true if force-exit fires.
    /// isShutdownAlreadyRequested reads ApplicationStopping; stopApplication requests graceful shutdown.
    /// waitForShutdownAsync completes after graceful shutdown, returning false on timeout; exit is Environment.Exit in production.</summary>
    public async Task<bool> WatchAsync(
        Task transportTask,
        Func<bool> isShutdownAlreadyRequested,
        Action stopApplication,
        Func<TimeSpan, Task<bool>> waitForShutdownAsync,
        Action<int> exit)
    {
        var faulted = false;
        try
        {
            await transportTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            faulted = true;
            _logger.LogWarning(ex, "MCP transport completed with exception; initiating shutdown.");
        }

        if (isShutdownAlreadyRequested())
        {
            _logger.LogInformation(
                "MCP stdio transport completed ({Reason}) while shutdown was already requested; arming shutdown backstop without another stop request.",
                faulted ? "transport fault" : "peer disconnect / EOF");
        }
        else
        {
            _logger.LogInformation(
                "MCP stdio transport completed ({Reason}). Stopping application and arming shutdown backstop.",
                faulted ? "transport fault" : "peer disconnect / EOF");
            stopApplication();
        }

        var shutdownCompleted = await waitForShutdownAsync(_gracePeriod).ConfigureAwait(false);
        if (shutdownCompleted)
        {
            _logger.LogInformation("Graceful shutdown completed within the grace period.");
            return false;
        }

        _logger.LogError(
            "Graceful shutdown did not complete cleanly within the {GraceSeconds}s grace period " +
            "after stdio EOF (hung or failed); force-exiting with code {ExitCode}.",
            _gracePeriod.TotalSeconds,
            ShutdownHangExitCode);
        exit(ShutdownHangExitCode);
        return true;
    }
}
