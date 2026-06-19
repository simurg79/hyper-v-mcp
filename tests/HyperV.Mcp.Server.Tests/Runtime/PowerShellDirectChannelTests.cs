using System.Management.Automation;
using System.Management.Automation.Remoting;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="PowerShellDirectChannel"/>. These mock <see cref="IPowerShellHost"/>
/// and <see cref="ISessionStore"/> so the tests stay hermetic and do not require Hyper-V.
///
/// See PSD-D5/D6/D7/D8 in /myplans/issue-52/phase-2/powershell-direct-channel-design.md.
/// </summary>
[Trait("Category", "Runtime")]
public class PowerShellDirectChannelTests
{
    private const string HostId = "local";
    private const string VmId = "12345678-1234-1234-1234-123456789abc";
    private const string User = "admin";
    private const string Pass = "P@ssw0rd!";
    private static readonly SessionHandle Handle = new(HostId, VmId, "hyperv-mcp-local-vm");
    private static readonly SessionHandle Handle2 = new(HostId, VmId, "hyperv-mcp-local-vm-r2");

    private static PowerShellHostResult Ok(string stderr = "")
        => new(Success: true, Output: new List<object?> { "ok" }, Stderr: stderr, ExitCode: 0);

    private static PowerShellHostResult BrokenSession()
        => new(Success: false, Output: new List<object?>(), Stderr: "PSRemotingTransportException: session is broken", ExitCode: 1);

    private static PowerShellHostResult HardFail(string stderr)
        => new(Success: false, Output: new List<object?>(), Stderr: stderr, ExitCode: 1);

    private static (Mock<IPowerShellHost> host, Mock<ISessionStore> store, PowerShellDirectChannel channel)
        CreateChannel()
    {
        var host = new Mock<IPowerShellHost>(MockBehavior.Strict);
        var store = new Mock<ISessionStore>(MockBehavior.Strict);
        var channel = new PowerShellDirectChannel(
            host.Object,
            store.Object,
            NullLogger<PowerShellDirectChannel>.Instance);
        return (host, store, channel);
    }

    // ─── InvokeScriptAsync ─────────────────────────────────────────────

    [Fact]
    public async Task InvokeScriptAsync_HappyPath_GetsSessionAndInvokesWrapperOnce()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("__HvMcpSessions") && s.Contains("Invoke-Command")),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok());

        var result = await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        result.Success.Should().BeTrue();
        store.Verify(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()), Times.Once);
        host.Verify(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeScriptAsync_BrokenSession_EvictsAndRetriesOnce()
    {
        var (host, store, channel) = CreateChannel();

        store.SetupSequence(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle)
            .ReturnsAsync(Handle2);
        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        host.SetupSequence(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BrokenSession())
            .ReturnsAsync(Ok());

        var result = await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        result.Success.Should().BeTrue("retry must succeed");
        store.Verify(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()), Times.Exactly(2));
        store.Verify(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once);
        host.Verify(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task InvokeScriptAsync_BrokenSessionTwice_DoesNotRetryAgain()
    {
        var (host, store, channel) = CreateChannel();

        store.SetupSequence(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle)
            .ReturnsAsync(Handle2);
        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BrokenSession());

        var result = await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        result.Success.Should().BeFalse();
        host.Verify(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "broken-session retry must happen exactly once (no retry loop)");
        store.Verify(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeScriptAsync_NonBrokenFailure_DoesNotRetry()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HardFail("some unrelated failure"));

        var result = await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        result.Success.Should().BeFalse();
        host.Verify(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.EvictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeScriptAsync_RedactsPasswordInStderr()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HardFail($"login failed for {Pass}"));

        var result = await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        result.Stderr.Should().NotContain(Pass, "password must be redacted (PSD-D8)");
        result.Stderr.Should().Contain("***REDACTED***");
    }

    // ─── CopyToSessionAsync / CopyFromSessionAsync wrappers ────────────

    [Fact]
    public async Task CopyToSessionAsync_DelegatesToHostWithCopyItemWrapper()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        string? capturedScript = null;
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(Ok());

        await channel.CopyToSessionAsync(HostId, VmId, User, Pass, @"C:\src\f.txt", @"C:\dst\f.txt");

        capturedScript.Should().NotBeNullOrEmpty();
        capturedScript!.Should().Contain("Copy-Item", "host script must invoke Copy-Item -ToSession");
        capturedScript.Should().Contain("ToSession");
    }

    [Fact]
    public async Task CopyFromSessionAsync_DelegatesToHostWithCopyItemWrapper()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        string? capturedScript = null;
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(Ok());

        await channel.CopyFromSessionAsync(HostId, VmId, User, Pass, @"C:\guest\f.txt", @"C:\host\f.txt");

        capturedScript.Should().NotBeNullOrEmpty();
        capturedScript!.Should().Contain("Copy-Item");
        capturedScript.Should().Contain("FromSession");
    }

    // ─── EvictSessionAsync ─────────────────────────────────────────────

    [Fact]
    public async Task EvictSessionAsync_DelegatesToSessionStore()
    {
        var (_, store, channel) = CreateChannel();

        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await channel.EvictSessionAsync(HostId, VmId);

        store.Verify(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Per-VM lock serializes concurrent invocations ─────────────────

    [Fact]
    public async Task InvokeScriptAsync_PerVmLock_SerializesConcurrentCallsForSameVm()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);

        var firstCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxObservedConcurrency = 0;
        var lockObj = new object();

        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                int now;
                lock (lockObj)
                {
                    now = ++inFlight;
                    if (now > maxObservedConcurrency) maxObservedConcurrency = now;
                }

                if (now == 1)
                {
                    firstCallStarted.TrySetResult(true);
                    await releaseFirstCall.Task;
                }

                lock (lockObj) { inFlight--; }
                return Ok();
            });

        var t1 = channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'a'");
        await firstCallStarted.Task;

        var t2 = channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'b'");
        // Give t2 a chance to (incorrectly) enter — it must NOT.
        await Task.Delay(50);

        t2.IsCompleted.Should().BeFalse("the per-VM lock must serialize calls to the same VM");
        maxObservedConcurrency.Should().Be(1);

        releaseFirstCall.SetResult(true);
        await Task.WhenAll(t1, t2);
        maxObservedConcurrency.Should().Be(1, "no two invocations on the same VM may overlap");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52, Gate 6 Fix #2 — timeout plumbing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies <see cref="PowerShellDirectChannel.InvokeScriptWithTimeoutAsync"/> forwards
    /// the timeout to <see cref="IPowerShellHost.InvokeWithTimeoutAsync"/>. (Gate 6 Fix #2.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptWithTimeoutAsync_ForwardsTimeoutToHost()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);

        int observedTimeout = -1;
        host.Setup(h => h.InvokeWithTimeoutAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, int?, CancellationToken>(
                (_, _, t, _) => observedTimeout = t ?? -1)
            .ReturnsAsync(Ok());

        await channel.InvokeScriptWithTimeoutAsync(
            HostId, VmId, User, Pass, "param() 'hi'",
            args: null, timeoutSeconds: 7);

        observedTimeout.Should().Be(7,
            "channel must forward the per-invocation timeout to the host (Gate 6 Fix #2)");
    }

    /// <summary>
    /// When the host throws <see cref="TimeoutException"/>, the channel must let it
    /// propagate (NOT swallow into the broken-session retry path, NOT redact-wrap).
    /// (Gate 6 Fix #2 + Fix #4.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptWithTimeoutAsync_HostThrowsTimeout_PropagatesUnredacted()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeWithTimeoutAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("PowerShell invocation exceeded the 1s timeout."));

        Func<Task> act = () => channel.InvokeScriptWithTimeoutAsync(
            HostId, VmId, User, Pass, "param() 'hi'",
            args: null, timeoutSeconds: 1);

        var ex = await act.Should().ThrowAsync<TimeoutException>();
        ex.Which.Message.Should().Contain("1s timeout");
        store.Verify(s => s.EvictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "timeout must NOT trigger broken-session eviction");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52, Gate 6 Fix #3 — exception-path broken-session retry
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mock host throws <see cref="PSRemotingTransportException"/> on first call, succeeds
    /// on second → assert evict + retry + result returned. (Gate 6 Fix #3.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptAsync_TransportExceptionThenSuccess_EvictsAndRetries()
    {
        var (host, store, channel) = CreateChannel();

        store.SetupSequence(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle)
            .ReturnsAsync(Handle2);
        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        host.SetupSequence(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PSRemotingTransportException("session is broken"))
            .ReturnsAsync(Ok());

        var result = await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        result.Success.Should().BeTrue("retry after transport exception must succeed");
        store.Verify(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once,
            "evict must be called exactly once when first call throws transport exception");
        host.Verify(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "host must be called twice (initial + retry)");
    }

    /// <summary>
    /// Mock host throws <see cref="PSRemotingTransportException"/> on BOTH calls →
    /// the wrapped exception must surface, evict was called only once (no retry-of-retry).
    /// (Gate 6 Fix #3.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptAsync_TransportExceptionTwice_SurfacesAndDoesNotRetryAgain()
    {
        var (host, store, channel) = CreateChannel();

        store.SetupSequence(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle)
            .ReturnsAsync(Handle2);
        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PSRemotingTransportException("session is broken"));

        Func<Task> act = () => channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        // The retry's exception is wrapped by the outer redaction guard (Fix #4) into
        // a PowerShellDirectChannelException — verify the type AND that it carries the
        // original transport text in the redacted message.
        var thrown = await act.Should().ThrowAsync<PowerShellDirectChannelException>();
        thrown.Which.Message.Should().Contain("session is broken");

        store.Verify(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once,
            "evict only the first failure — the retry's failure must propagate, not loop");
        host.Verify(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "host must be called exactly twice (initial + single retry)");
    }

    /// <summary>
    /// An UNRELATED exception type (e.g. <see cref="ArgumentException"/>) must NOT trigger
    /// the broken-session retry path. Verifies the catch is narrow. (Gate 6 Fix #3.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptAsync_UnrelatedException_DoesNotRetryOrEvict()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("totally unrelated"));

        Func<Task> act = () => channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        // Wrapped by the outer redaction guard.
        await act.Should().ThrowAsync<PowerShellDirectChannelException>();

        store.Verify(s => s.EvictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "narrow catch must NOT evict on unrelated exceptions");
        host.Verify(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once, "must not retry on unrelated exceptions");
    }

    /// <summary>
    /// <see cref="RuntimeException"/> whose message matches the broken-session signature
    /// also triggers evict-and-retry. (Gate 6 Fix #3, string-match fallback.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptAsync_RuntimeExceptionWithBrokenSessionMessage_EvictsAndRetries()
    {
        var (host, store, channel) = CreateChannel();

        store.SetupSequence(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle)
            .ReturnsAsync(Handle2);
        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        host.SetupSequence(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RuntimeException("The runspace session is not in the Opened state."))
            .ReturnsAsync(Ok());

        var result = await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        result.Success.Should().BeTrue();
        store.Verify(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52, Gate 6 Fix #4 — exception-path credential redaction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Host throws <see cref="RuntimeException"/> whose message contains the password.
    /// The wrapped exception's message MUST NOT contain the literal password.
    /// (Gate 6 Fix #4.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptAsync_HostThrowsWithPasswordInMessage_RedactsExceptionMessage()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RuntimeException($"Failed login for user admin:{Pass}"));

        Func<Task> act = () => channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        var ex = await act.Should().ThrowAsync<PowerShellDirectChannelException>();
        ex.Which.Message.Should().NotContain(Pass,
            "exception-path message must be credential-redacted (PSD-D8 / Gate 6 Fix #4)");
        ex.Which.Message.Should().Contain("***REDACTED***");
    }

    /// <summary>
    /// Session-store throws with a credential-bearing message → outer wrapper must
    /// redact before propagation. (Gate 6 Fix #4.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptAsync_SessionStoreThrowsWithPassword_RedactsExceptionMessage()
    {
        var (_, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                $"Failed to create PSSession 'hyperv-mcp-local-vm': bad password '{Pass}'"));

        Func<Task> act = () => channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        var ex = await act.Should().ThrowAsync<PowerShellDirectChannelException>();
        ex.Which.Message.Should().NotContain(Pass,
            "session-store exceptions must also flow through credential redaction");
        ex.Which.Message.Should().Contain("***REDACTED***");
    }

    /// <summary>
    /// <see cref="OperationCanceledException"/> must pass through the redaction wrapper
    /// UNTOUCHED (it carries no credentials by definition; mapping cares about the type).
    /// (Gate 6 Fix #4.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptAsync_OperationCanceledException_PassesThroughUnwrapped()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("caller cancelled"));

        Func<Task> act = () => channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        var ex = await act.Should().ThrowAsync<OperationCanceledException>();
        ex.Which.Message.Should().Be("caller cancelled",
            "OperationCanceledException must pass through unredacted-but-unmodified");
    }

    /// <summary>
    /// <see cref="TimeoutException"/> must pass through the redaction wrapper UNTOUCHED.
    /// (Gate 6 Fix #4.)
    /// </summary>
    [Fact]
    public async Task InvokeScriptWithTimeoutAsync_TimeoutException_PassesThroughUnwrapped()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeWithTimeoutAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("PowerShell invocation exceeded the 1s timeout."));

        Func<Task> act = () => channel.InvokeScriptWithTimeoutAsync(
            HostId, VmId, User, Pass, "param() 'hi'", args: null, timeoutSeconds: 1);

        var ex = await act.Should().ThrowAsync<TimeoutException>();
        ex.Which.Message.Should().Contain("1s timeout",
            "TimeoutException must pass through unredacted-but-unmodified");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52, Gate 6 re-verification — production wrapper-shape contract
    //
    // The contradiction these tests resolve: previously the channel ran the
    // inner-exception chain through RedactExceptionTree, which wrapped EVERY
    // link in a fresh PowerShellDirectChannelException — destroying the
    // concrete type. As a result the typed mapper branches in ErrorMapper
    // (PSRemotingTransportException → SESSION_FAILED, RuntimeException-auth
    // → AUTH_FAILED, etc.) never fired in production, even though isolated
    // ErrorMapper unit tests passed by manually constructing the "ideal"
    // wrapper shape.
    //
    // The fix: the wrapper now retains the ORIGINAL thrown exception as its
    // InnerException. These tests pin that contract end-to-end through the
    // real PowerShellDirectChannel.InvokeScriptAsync path, then re-classify
    // the caught wrapper through the real ErrorMapper.MapException to prove
    // the production shape DOES drive the typed branches.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drives the real channel with a host that throws
    /// <see cref="PSRemotingTransportException"/> with a credential-bearing message
    /// and asserts the production wrapper shape: top-level Message redacted, but
    /// InnerException is the ORIGINAL <see cref="PSRemotingTransportException"/>
    /// (not a re-wrapped <see cref="PowerShellDirectChannelException"/>). This
    /// pins the contract that the typed mapper branches rely on.
    /// </summary>
    [Fact]
    public async Task InvokeScriptAsync_HostThrowsTransportExceptionWithPassword_PreservesInnerTypeAndRedactsBothMessages()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Both attempts (initial + retry) throw — so the retry's exception escapes
        // the outer redaction guard and we can observe the production wrapper shape.
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PSRemotingTransportException($"session is broken: {Pass}"));

        Func<Task> act = () => channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");

        var thrown = await act.Should().ThrowAsync<PowerShellDirectChannelException>();
        var caught = thrown.Which;

        // (1) Outer message must be redacted.
        caught.Message.Should().NotContain(Pass, "outer wrapper Message must be credential-redacted");
        caught.Message.Should().Contain("***REDACTED***");

        // (2) InnerException must be the ORIGINAL concrete type — NOT re-wrapped.
        // This is the contract the typed ErrorMapper branches depend on.
        caught.InnerException.Should().BeOfType<PSRemotingTransportException>(
            "production wrapping path MUST preserve the original exception type so " +
            "ErrorMapper can classify by type — NOT re-wrap inner in another " +
            "PowerShellDirectChannelException (the previous bug)");
    }

    /// <summary>
    /// End-to-end: production-shape wrapper from the real channel, run through the
    /// real <see cref="ErrorMapper.MapException"/>, must classify as SESSION_FAILED.
    /// Proves the typed inner branches actually fire on the production wrapper shape
    /// (the reviewer's specific concern).
    /// </summary>
    [Fact]
    public async Task ProductionWrapperShape_ClassifiesAsSessionFailed_ViaErrorMapper()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PSRemotingTransportException($"session is broken: {Pass}"));

        PowerShellDirectChannelException? caught = null;
        try
        {
            await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");
        }
        catch (PowerShellDirectChannelException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull();

        var mapper = new ErrorMapper();
        var response = mapper.MapException(caught!);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(Models.ErrorCodes.SessionFailed,
            "the production wrapper's preserved PSRemotingTransportException InnerException " +
            "MUST drive ErrorMapper to SESSION_FAILED — NOT fall through to COMMAND_FAILED");
        response.Error.Should().NotContain(Pass,
            "ErrorMapper must surface the channel's redacted top-level message, never the inner");
    }

    /// <summary>
    /// Same end-to-end shape contract for the broken-session
    /// <see cref="RuntimeException"/> path → SESSION_FAILED via the typed
    /// RuntimeException-with-broken-session-signature branch.
    /// </summary>
    [Fact]
    public async Task ProductionWrapperShape_RuntimeExceptionBrokenSession_ClassifiesAsSessionFailed()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        store.Setup(s => s.EvictAsync(HostId, VmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RuntimeException(
                $"The runspace session is not in the Opened state. password={Pass}"));

        PowerShellDirectChannelException? caught = null;
        try
        {
            await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");
        }
        catch (PowerShellDirectChannelException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull();
        caught!.InnerException.Should().BeOfType<RuntimeException>(
            "production wrapper must preserve RuntimeException as InnerException");

        var response = new ErrorMapper().MapException(caught);

        response.ErrorCode.Should().Be(Models.ErrorCodes.SessionFailed);
        response.Error.Should().NotContain(Pass);
    }

    /// <summary>
    /// Same end-to-end shape contract for the auth-flavored
    /// <see cref="RuntimeException"/> path → AUTH_FAILED via the typed
    /// RuntimeException-with-auth-signature branch. Note: an auth-flavored
    /// RuntimeException (no broken-session keywords) does NOT trigger
    /// IsBrokenSessionException, so only one host invocation is required.
    /// </summary>
    [Fact]
    public async Task ProductionWrapperShape_RuntimeExceptionAuth_ClassifiesAsAuthFailed()
    {
        var (host, store, channel) = CreateChannel();

        store.Setup(s => s.GetOrCreateAsync(HostId, VmId, User, Pass, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Handle);
        host.Setup(h => h.InvokeAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RuntimeException($"Failed login for user admin:{Pass}"));

        PowerShellDirectChannelException? caught = null;
        try
        {
            await channel.InvokeScriptAsync(HostId, VmId, User, Pass, "param() 'hi'");
        }
        catch (PowerShellDirectChannelException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull();
        caught!.InnerException.Should().BeOfType<RuntimeException>();

        var response = new ErrorMapper().MapException(caught);

        response.ErrorCode.Should().Be(Models.ErrorCodes.AuthFailed,
            "production wrapper preserving RuntimeException with 'failed login' text " +
            "MUST drive ErrorMapper to AUTH_FAILED via the typed inner branch");
        response.Error.Should().NotContain(Pass,
            "outer wrapper's redacted message is what surfaces — never the inner credential text");
    }
}
