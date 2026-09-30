using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #209 / PR #259 review-feedback coverage for the two cancellation fixes in
/// <see cref="SshExecClientFactory"/> (LGS-SSH-D5). These pin the connect-time cancellation
/// contract on the REAL factory (no live SSH server): a cancelled token must break the blocking
/// connect race and surface <see cref="OperationCanceledException"/> promptly instead of hanging,
/// and the faulted connect task must be observed (OnlyOnFaulted continuation) so it never resurfaces
/// as an <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>.
///
/// <para>The <c>ExecuteAsync</c> WaitAny cancellation path requires a connected SSH client and is
/// therefore covered by the live Tier-2 suite (LiveLinuxSshExecTests); it cannot be exercised
/// without a reachable guest.</para>
/// </summary>
[Trait("Category", "Runtime")]
public class Issue209SshExecClientFactoryCancellationTests
{
    // An address in TEST-NET-1 (RFC 5737) is guaranteed non-routable, so the blocking Connect()
    // stays in-flight long enough for the token race to win deterministically without a real server.
    private const string UnreachableHost = "192.0.2.1";
    private const int UnreachablePort = 22;

    [Fact]
    public async Task ConnectAsync_AlreadyCancelledToken_Throws_OperationCanceled_PromptlyNotHang()
    {
        var factory = new SshExecClientFactory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var connect = new Func<Task>(() => factory.ConnectAsync(
            UnreachableHost, UnreachablePort, "user", "pw", cts.Token));

        // Must not hang: cancellation wins the connect race and propagates within the timeout budget.
        var completed = await Task.WhenAny(
            Assert.ThrowsAnyAsync<OperationCanceledException>(connect),
            Task.Delay(TimeSpan.FromSeconds(15)));

        completed.Should().BeAssignableTo<Task<OperationCanceledException>>(
            "an already-cancelled token must break the blocking connect and surface " +
            "OperationCanceledException rather than hanging (LGS-SSH-D5).");
    }

    [Fact]
    public async Task ConnectAsync_TokenCancelledMidConnect_Throws_OperationCanceled()
    {
        var factory = new SshExecClientFactory();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Func<Task> act = () => factory.ConnectAsync(
            UnreachableHost, UnreachablePort, "user", "pw", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a token that fires while the blocking connect is in-flight must interrupt it and " +
            "propagate cancellation (LGS-SSH-D5).");
    }

    [Fact]
    public async Task ConnectAsync_Cancellation_Does_Not_Leak_UnobservedTaskException()
    {
        // The OnlyOnFaulted continuation added in the review fix must observe the faulted connect
        // task after the client is disposed. If it did not, forcing GC after the connect faults
        // would raise TaskScheduler.UnobservedTaskException. Assert none fires for our exception.
        var observed = new List<Exception>();
        void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            observed.AddRange(args.Exception.InnerExceptions);
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var factory = new SshExecClientFactory();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                await factory.ConnectAsync(UnreachableHost, UnreachablePort, "user", "pw", cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected — cancellation won the race
            }

            // Give the disposed client's blocking Connect() time to unblock/fault, then force the
            // finalizer so any unobserved faulted task would raise here if the continuation were absent.
            await Task.Delay(TimeSpan.FromSeconds(2));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            observed.Should().BeEmpty(
                "the faulted connect task must be observed by the OnlyOnFaulted continuation so it " +
                "never resurfaces as an UnobservedTaskException (PR #259 review fix).");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }
}
