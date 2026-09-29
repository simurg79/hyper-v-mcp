using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>Readiness must authenticate afresh, confirm guest execution and clean up its own connection. Windows scripts run in a real
/// runspace so canned transport success cannot hide broken scripts.</summary>
[Trait("Category", "Runtime")]
public class Issue381ReadinessAuthenticatorRealSeamTests
{
    private const string VmId = "12345678-1234-1234-1234-123456789abc";
    private const string VmName = "readiness-vm";
    private const string Username = "readiness-user";
    private const string Password = "readiness-pass";

    private static ReadinessAuthenticator BuildAuthenticator(
        IPowerShellHost? host, ISshExecClientFactory? sshFactory = null)
        => new(host, sshFactory ?? new ReadinessSshExecClientFactory(), NullLogger.Instance);


    [Fact]
    public async Task Windows_ConfirmsReadiness_ByFreshlyAuthenticatingAndIssuingTheSentinelProbe()
    {
        using var host = new ScriptExecutingPowerShellHost();
        var budget = new ReadinessBudget(300);

        var verdict = await BuildAuthenticator(host)
            .ConfirmWindowsAsync(VmId, VmName, Username, Password, budget, CancellationToken.None);

        verdict.Kind.Should().Be(ReadinessVerdictKind.Ready);
        host.AuthenticationCount.Should().Be(1, "readiness must authenticate during this call");
        host.Events.Should().Contain(entry => entry.Contains($"user={Username}") && entry.Contains($"pass={Password}"),
            "the authentication must carry THIS call's credentials");
        host.ConfirmationCount.Should().Be(1);
        host.Events.Should().Contain(entry => entry.StartsWith("CONFIRM ") && entry.Contains(ReadinessVerdict.Sentinel),
            "the concrete sentinel probe must actually be issued");
    }

    [Fact]
    public void Windows_ReadsAndWritesNoSharedSessionCache_Structurally()
    {
        typeof(ReadinessAuthenticator).GetConstructors().Should().ContainSingle();
        var parameterTypes = typeof(ReadinessAuthenticator).GetConstructors()[0]
            .GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        parameterTypes.Should().NotContain(typeof(ISessionStore));
        parameterTypes.Should().NotContain(typeof(ISshSessionStore));
        typeof(ReadinessAuthenticator).GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Select(field => field.FieldType)
            .Should().NotContain(new[] { typeof(ISessionStore), typeof(ISshSessionStore) });
    }

    [Fact]
    public async Task Windows_ReferencesNoSharedSessionTable_Behaviourally()
    {
        using var host = new ScriptExecutingPowerShellHost();

        await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        host.InvokedScripts.Should().NotBeEmpty();
        host.InvokedScripts.Should().OnlyContain(script => !script.Contains("__HvMcpSessions"),
            "readiness must never touch the shared session table");
    }

    [Fact]
    public async Task Windows_LeavesAnotherUsersCachedSessionIntact_AndStillAuthenticatesFreshly()
    {
        using var host = new ScriptExecutingPowerShellHost();
        var cache = new System.Collections.Hashtable { ["local::" + VmId] = "user-a-session" };
        host.SeedSharedSessionCache(cache);

        var verdict = await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, "user-b", "pass-b", new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().Be(ReadinessVerdictKind.Ready);
        host.Events.Should().Contain(entry => entry.Contains("user=user-b"),
            "a cached session for another user must not satisfy this call");
        host.ReadSharedSessionCache()!["local::" + VmId].Should().Be("user-a-session",
            "the other user's cached entry must survive usable");
    }

    [Fact]
    public async Task Windows_SameUserWithChangedPassword_StillAuthenticatesFreshly_AndRejectionStaysAnObstacle()
    {
        using var host = new ScriptExecutingPowerShellHost();
        var cache = new System.Collections.Hashtable { ["local::" + VmId] = "stale-password-session" };
        host.SeedSharedSessionCache(cache);
        host.SetAuthMode("reject");

        var verdict = await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, "new-password", new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().Be(ReadinessVerdictKind.Obstacle, "an in-boot rejection is not terminal");
        verdict.CredentialRejected.Should().BeTrue();
        host.Events.Should().Contain(entry => entry.Contains("pass=new-password"),
            "the cached session under the old password must not substitute for this call");
        host.ReadSharedSessionCache()!["local::" + VmId].Should().Be("stale-password-session");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("nonsentinel")]
    [InlineData("fault")]
    public async Task Windows_AuthenticationSucceedsButConfirmationDoesNot_IsNotReadyAndStillCloses(string confirmMode)
    {
        using var host = new ScriptExecutingPowerShellHost();
        host.SetConfirmMode(confirmMode);

        var verdict = await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().NotBe(ReadinessVerdictKind.Ready,
            "absence of a positive sentinel is never readiness");
        host.AuthenticationCount.Should().Be(1);
        host.CloseCount.Should().Be(1, "the readiness connection must be closed on this exit path too");
    }

    [Fact]
    public async Task Windows_IssuesCloseAndRemovesItsVariable_OnEveryExitPath()
    {
        foreach (var authMode in new[] { "ok", "reject", "transport" })
        {
            using var host = new ScriptExecutingPowerShellHost();
            host.SetAuthMode(authMode);

            await BuildAuthenticator(host).ConfirmWindowsAsync(
                VmId, VmName, Username, Password, new ReadinessBudget(300), CancellationToken.None);

            host.InvokedScripts.Should().Contain(script => script.Contains("Remove-PSSession"),
                $"the close must be issued when authentication mode is '{authMode}'");
            host.ReadinessVariableNames().Should().BeEmpty(
                $"no readiness session variable may outlive the call for mode '{authMode}'");
        }
    }

    [Fact]
    public async Task Windows_IssuesClose_EvenWhenCallerCancelsAfterAuthenticating()
    {
        using var host = new ScriptExecutingPowerShellHost();
        using var cancellation = new CancellationTokenSource();
        // Cancelling AFTER the authentication leg is the case with something to clean up; cancelling before it has nothing open and would
        // pass over a deleted cleanup.
        host.CancelAfterInvocation(1, cancellation);

        var act = async () => await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, new ReadinessBudget(300), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        host.AuthenticationCount.Should().Be(1);
        host.CloseCount.Should().Be(1, "cleanup must still run after cancellation");
        host.ReadinessVariableNames().Should().BeEmpty();
    }

    [Fact]
    public async Task Windows_CleanupFailure_DoesNotChangeTheOutcome()
    {
        using var host = new ScriptExecutingPowerShellHost();
        host.SetCleanupMode("fail");

        var verdict = await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().Be(ReadinessVerdictKind.Ready,
            "a failed close must never turn a confirmed guest into a failure");
    }

    [Fact]
    public async Task Windows_UsesACallUniqueSessionVariable_NeverAFixedName()
    {
        using var first = new ScriptExecutingPowerShellHost();
        using var second = new ScriptExecutingPowerShellHost();

        await BuildAuthenticator(first).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, new ReadinessBudget(300), CancellationToken.None);
        await BuildAuthenticator(second).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        ExtractSessionVariable(first.InvokedScripts[0]).Should()
            .NotBe(ExtractSessionVariable(second.InvokedScripts[0]),
                "a fixed name would let two calls collide on one session");
    }

    /// <summary>Direct authenticator calls must reject spent budgets without relying on the manager's check.</summary>
    [Fact]
    public async Task Windows_EnteredWithAnExhaustedBudget_AuthenticatesNothingAtAll()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var budget = new ReadinessBudget(10, clock);
        clock.Advance(TimeSpan.FromSeconds(10));
        using var host = new ScriptExecutingPowerShellHost();

        var act = async () => await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, budget, CancellationToken.None);

        await act.Should().ThrowAsync<ReadinessNotReachedException>();
        host.AuthenticationCount.Should().Be(0, "no guest access may begin after expiry");
    }

    /// <summary>The Linux leg has its own entry boundary and must refuse independently.</summary>
    [Fact]
    public async Task Linux_EnteredWithAnExhaustedBudget_ConnectsNothingAtAll()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var budget = new ReadinessBudget(10, clock);
        clock.Advance(TimeSpan.FromSeconds(10));
        var factory = new ReadinessSshExecClientFactory();

        var act = async () => await BuildAuthenticator(host: null, factory).ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, budget, CancellationToken.None);

        await act.Should().ThrowAsync<ReadinessNotReachedException>();
        factory.Connections.Should().BeEmpty("no connection may be opened after expiry");
    }

    /// <summary>Authentication errors must prevent confirmation; a later sentinel cannot rescue failed authentication.</summary>
    [Fact]
    public async Task Windows_AuthenticationReportingErrors_IsNotRescued_AndNeverConfirms()
    {
        using var host = new ScriptExecutingPowerShellHost();
        host.SetAuthMode("noisy");

        var verdict = await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().NotBe(ReadinessVerdictKind.Ready,
            "an authentication that reported errors must never yield readiness");
        host.ConfirmationCount.Should().Be(0,
            "confirmation must not run after a failed authentication");
    }

    /// <summary>Authentication can outlast the budget; confirmation must re-check before starting.</summary>
    [Fact]
    public async Task Windows_BudgetExpiringDuringAuthentication_StopsBeforeConfirming()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var budget = new ReadinessBudget(10, clock);
        using var host = new ScriptExecutingPowerShellHost();
        host.ExpireAfterInvocation(1, () => clock.Advance(TimeSpan.FromSeconds(10)));

        var act = async () => await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, budget, CancellationToken.None);

        await act.Should().ThrowAsync<ReadinessNotReachedException>();
        host.AuthenticationCount.Should().Be(1);
        host.ConfirmationCount.Should().Be(0,
            "confirmation must not start once the budget is spent");
        host.CloseCount.Should().Be(1, "the opened session must still be closed");
    }

    /// <summary>Cleanup runs in a finally and MUST still close a session opened before expiry.</summary>
    [Fact]
    public async Task Linux_BudgetExpiringDuringAuthentication_StopsBeforeConfirming_AndStillDisposes()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var budget = new ReadinessBudget(10, clock);
        var factory = new ReadinessSshExecClientFactory
        {
            OnConnected = () => clock.Advance(TimeSpan.FromSeconds(10)),
        };

        var act = async () => await BuildAuthenticator(host: null, factory).ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, budget, CancellationToken.None);

        await act.Should().ThrowAsync<ReadinessNotReachedException>();
        factory.LastClient!.Commands.Should().BeEmpty("no confirmation may run after expiry");
        factory.LastClient.DisposeCount.Should().Be(1);
    }

    // Cancellation during cleanup must not replace the budget failure's last-observation diagnostic.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_CancelledCleanup_PreservesBudgetFailure(bool cleanupFails)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var budget = new ReadinessBudget(10, clock);
        using var cancellation = new CancellationTokenSource();
        using var host = new ScriptExecutingPowerShellHost();
        host.ExpireAfterInvocation(1, () => clock.Advance(TimeSpan.FromSeconds(10)));
        host.CancelAfterInvocation(2, cancellation);
        host.SetCleanupMode(cleanupFails ? "fail" : "ok");

        var act = async () => await BuildAuthenticator(host).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, budget, cancellation.Token);

        var failure = await act.Should().ThrowAsync<ReadinessNotReachedException>();
        failure.Which.Message.Should().Contain("wait budget exhausted")
            .And.Contain("Last observation: Fresh authentication completed; the guest operation is not yet confirmed.");
        cancellation.IsCancellationRequested.Should().BeTrue();
        host.AuthenticationCount.Should().Be(1);
        host.ConfirmationCount.Should().Be(0);
        host.CloseCount.Should().Be(1);
        host.ReadinessVariableNames().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linux_CancelledCleanup_PreservesBudgetFailure(bool cleanupFails)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var budget = new ReadinessBudget(10, clock);
        using var cancellation = new CancellationTokenSource();
        var client = new Mock<ISshExecClient>(MockBehavior.Strict);
        client.Setup(connection => connection.Dispose()).Callback(() =>
        {
            cancellation.Cancel();
            if (cleanupFails)
                throw new OperationCanceledException(cancellation.Token);
        });
        var factory = new Mock<ISshExecClientFactory>(MockBehavior.Strict);
        factory.Setup(connection => connection.ConnectAsync("10.0.0.5", 22, Username, Password, cancellation.Token))
            .Callback(() => clock.Advance(TimeSpan.FromSeconds(10)))
            .ReturnsAsync(client.Object);

        var act = async () => await BuildAuthenticator(host: null, factory.Object).ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, budget, cancellation.Token);

        var failure = await act.Should().ThrowAsync<ReadinessNotReachedException>();
        failure.Which.Message.Should().Contain("wait budget exhausted")
            .And.Contain("Last observation: Fresh authentication completed; the guest operation is not yet confirmed.");
        cancellation.IsCancellationRequested.Should().BeTrue();
        client.Verify(connection => connection.Dispose(), Times.Once);
        client.Verify(connection => connection.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        factory.VerifyAll();
    }

    [Fact]
    public async Task Windows_WithoutAGuestAccessHost_IsTerminalRatherThanReady()
    {
        var verdict = await BuildAuthenticator(host: null).ConfirmWindowsAsync(
            VmId, VmName, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().Be(ReadinessVerdictKind.Terminal);
    }


    [Fact]
    public async Task Linux_ConfirmsReadiness_ByFreshlyConnectingAndIssuingTheSentinelCommand()
    {
        var factory = new ReadinessSshExecClientFactory();
        var verdict = await BuildAuthenticator(host: null, factory).ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().Be(ReadinessVerdictKind.Ready);
        factory.Connections.Should().ContainSingle()
            .Which.Should().Be($"10.0.0.5:22 user={Username} pass={Password}");
        factory.LastClient!.Commands.Should().ContainSingle()
            .Which.Should().Be("echo " + ReadinessVerdict.Sentinel,
                "the Linux command argument is mandatory — an empty command exits 0 and would fake readiness");
        factory.LastClient.DisposeCount.Should().Be(1, "the readiness connection must be closed");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("nonsentinel")]
    [InlineData("nonzero")]
    public async Task Linux_ConfirmationWithoutPositiveSentinel_IsNotReadyAndStillDisposes(string confirmMode)
    {
        var factory = new ReadinessSshExecClientFactory { ConfirmMode = confirmMode };

        var verdict = await BuildAuthenticator(host: null, factory).ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().NotBe(ReadinessVerdictKind.Ready);
        factory.LastClient!.DisposeCount.Should().Be(1);
    }

    /// <summary>Each readiness call must open and close its own connection; caching could report ready after the guest stops accepting
    /// logins.</summary>
    [Fact]
    public async Task Linux_SecondCall_ConnectsAgain_RatherThanReusingTheFirstConnection()
    {
        var factory = new ReadinessSshExecClientFactory();
        var authenticator = BuildAuthenticator(host: null, factory);

        await authenticator.ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, new ReadinessBudget(300), CancellationToken.None);
        var firstClient = factory.LastClient!;

        await authenticator.ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        factory.Connections.Should().HaveCount(2, "each readiness call must authenticate on its own");
        factory.LastClient.Should().NotBeSameAs(firstClient, "a reused client is not fresh evidence");
        firstClient.DisposeCount.Should().Be(1);
        factory.LastClient!.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Linux_CredentialRejection_RemainsAnObstacleRatherThanTerminal()
    {
        var factory = new ReadinessSshExecClientFactory { ConnectMode = "reject" };

        var verdict = await BuildAuthenticator(host: null, factory).ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().Be(ReadinessVerdictKind.Obstacle);
        verdict.CredentialRejected.Should().BeTrue();
    }

    [Fact]
    public async Task Linux_TransportFailure_RemainsAnObstacleWithoutClaimingRejection()
    {
        var factory = new ReadinessSshExecClientFactory { ConnectMode = "transport" };

        var verdict = await BuildAuthenticator(host: null, factory).ConfirmLinuxAsync(
            VmId, "10.0.0.5", 22, Username, Password, new ReadinessBudget(300), CancellationToken.None);

        verdict.Kind.Should().Be(ReadinessVerdictKind.Obstacle);
        verdict.CredentialRejected.Should().BeFalse("transport failure is not a credential verdict");
    }


    [Fact]
    public void Verdict_RequiresAffirmativeSentinel_AndRejectsSubstringContainment()
    {
        ReadinessVerdict.Evaluate(true, false, ReadinessVerdict.Sentinel, null).Kind
            .Should().Be(ReadinessVerdictKind.Ready);
        ReadinessVerdict.Evaluate(true, false, "HVMCP_READYISH", null).Kind
            .Should().Be(ReadinessVerdictKind.Obstacle, "containment is not delimiter-bounded matching");
        ReadinessVerdict.Evaluate(true, false, null, null).Kind
            .Should().Be(ReadinessVerdictKind.Obstacle, "absence of an error is not evidence of success");
        ReadinessVerdict.Evaluate(true, true, ReadinessVerdict.Sentinel, null).Kind
            .Should().Be(ReadinessVerdictKind.Obstacle, "an error-bearing result is never readiness");
    }

    private static string ExtractSessionVariable(string script)
    {
        var marker = script.IndexOf("__HvMcpReadiness_", StringComparison.Ordinal);
        marker.Should().BeGreaterThan(-1);
        var name = new string(script[marker..].TakeWhile(
            character => char.IsLetterOrDigit(character) || character == '_').ToArray());
        name.Should().NotBe("__HvMcpReadiness_");
        return name;
    }
}
