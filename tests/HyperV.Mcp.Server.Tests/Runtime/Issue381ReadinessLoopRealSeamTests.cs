using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>Readiness must require fresh login and confirmation within the shared budget. Only transport and OS-observation seams are faked
/// so production loop failures remain visible.</summary>
[Trait("Category", "Runtime")]
public class Issue381ReadinessLoopRealSeamTests
{
    private const string HostId = "local";
    private const string VmId = "12345678-1234-1234-1234-123456789abc";
    private const string Username = "readiness-user";
    private const string Password = "readiness-pass";

    /// <summary>Counts probe calls so suppression after expiry is observable.</summary>
    private sealed class CountingGuestOsProbe : IGuestOsProbe
    {
        private readonly GuestRoutingHint? _hint;

        internal CountingGuestOsProbe(GuestRoutingHint? hint) => _hint = hint;

        internal int ProbeCount { get; private set; }
        internal int EndpointCount { get; private set; }

        public Task<GuestRoutingHint?> ProbeAsync(string hostId, string vmId, CancellationToken ct)
        {
            ProbeCount++;
            return Task.FromResult(_hint);
        }

        public Task<(string SshHost, int SshPort)?> ResolveVmScopedSshEndpointAsync(
            string hostId, string vmId, CancellationToken ct)
        {
            EndpointCount++;
            return Task.FromResult<(string, int)?>(null);
        }
    }

    private static ServerOptions BuildOptions() => new()
    {
        DefaultHostId = HostId,
        Hosts = new Dictionary<string, HostProfile>
        {
            [HostId] = new HostProfile
            {
                HostId = HostId,
                ComputerName = "localhost",
                TrustPolicy = "local",
                BaseVhdxPath = @"C:\Base\base.vhdx",
                StorageRoot = @"C:\HyperVMCP\VMs",
            },
        },
    };

    private static Mock<IPowerShellExecutor> RunningVmWithHeartbeat()
    {
        var executor = new Mock<IPowerShellExecutor>();
        executor
            .Setup(x => x.ExecuteAsync(It.Is<string>(script => script.Contains("Heartbeat")),
                It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "HVMCP_HEARTBEAT_OK",
                Stderr = string.Empty,
            });
        executor
            .Setup(x => x.ExecuteAsync(It.Is<string>(script => !script.Contains("Heartbeat")),
                It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = $$"""
                {"Id":"{{VmId}}","Name":"readiness-vm","State":"Running",
                 "ProcessorCount":2,"MemoryMB":4096,"UptimeSeconds":9000}
                """,
                Stderr = string.Empty,
            });
        return executor;
    }

    private static HyperVManager BuildManager(
        Mock<IPowerShellExecutor> executor,
        IGuestOsProbe probe,
        IPowerShellHost? psHost,
        ISshExecClientFactory sshFactory,
        TimeProvider clock,
        IGuestRoutingHintStore? hintStore = null)
    {
        var options = BuildOptions();
        return new HyperVManager(
            executor.Object,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(),
            guestRoutingHintStore: hintStore ?? new GuestRoutingHintStore(),
            guestOsProbe: probe,
            psHost: psHost,
            sshExecClientFactory: sshFactory,
            readinessClock: clock);
    }

    [Fact]
    public async Task HeartbeatOk_WithGuestLoginNeverConfirmed_NeverSucceeds_RegardlessOfUptime()
    {
        var clock = new FakeTimeProvider();
        using var pump = new VirtualClockPump(clock, TimeSpan.FromSeconds(5));
        using var host = new ScriptExecutingPowerShellHost();
        host.SetAuthMode("reject");
        var manager = BuildManager(RunningVmWithHeartbeat(),
            new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0)),
            host, new ReadinessSshExecClientFactory(), clock);

        var act = async () => await manager.WaitForReadyAsync(
            HostId, VmId, new ReadinessBudget(30, clock), Username, Password);

        var thrown = await act.Should().ThrowAsync<ReadinessNotReachedException>();
        thrown.Which.Message.Should().Contain("guest-login readiness was not confirmed");
        host.AuthenticationCount.Should().BeGreaterThan(0,
            "heartbeat alone must never short-circuit the guest-login attempt");
    }

    [Fact]
    public async Task CredentialRejectionUntilExpiry_ReportsBothRecoveryActions_WithoutPickingACause()
    {
        var clock = new FakeTimeProvider();
        using var pump = new VirtualClockPump(clock, TimeSpan.FromSeconds(5));
        using var host = new ScriptExecutingPowerShellHost();
        host.SetAuthMode("reject");
        var manager = BuildManager(RunningVmWithHeartbeat(),
            new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0)),
            host, new ReadinessSshExecClientFactory(), clock);

        var act = async () => await manager.WaitForReadyAsync(
            HostId, VmId, new ReadinessBudget(30, clock), Username, Password);

        var thrown = await act.Should().ThrowAsync<ReadinessNotReachedException>();
        thrown.Which.Message.Should().Contain(Username)
            .And.Contain("Verify credentials for the image")
            .And.Contain("wait and retry after a recent start")
            .And.Contain("neither cause has been established");
    }

    [Fact]
    public async Task ConfirmedGuestLogin_ReturnsTheVmInfoSuccess()
    {
        var clock = new FakeTimeProvider();
        using var host = new ScriptExecutingPowerShellHost();
        var manager = BuildManager(RunningVmWithHeartbeat(),
            new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0)),
            host, new ReadinessSshExecClientFactory(), clock);

        var vmInfo = await manager.WaitForReadyAsync(
            HostId, VmId, new ReadinessBudget(300, clock), Username, Password);

        vmInfo.State.Should().Be("Running");
        host.AuthenticationCount.Should().Be(1, "success requires exactly this call's authentication");
        host.ConfirmationCount.Should().Be(1);
        host.CloseCount.Should().Be(1, "the readiness connection must not outlive the call");
    }

    [Fact]
    public async Task LinuxGuest_ConfirmsThroughSsh_WithoutEnteringTheSshSessionStore()
    {
        var clock = new FakeTimeProvider();
        var sshFactory = new ReadinessSshExecClientFactory();
        var manager = BuildManager(RunningVmWithHeartbeat(),
            new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Linux, "10.0.0.5", 22)),
            psHost: null, sshFactory, clock);

        var vmInfo = await manager.WaitForReadyAsync(
            HostId, VmId, new ReadinessBudget(300, clock), Username, Password);

        vmInfo.State.Should().Be("Running");
        sshFactory.Connections.Should().ContainSingle()
            .Which.Should().Contain($"user={Username}");
        sshFactory.LastClient!.Commands.Should().ContainSingle()
            .Which.Should().Be("echo " + ReadinessVerdict.Sentinel);
        sshFactory.LastClient.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task ExhaustedBudgetAtEntry_StartsNoDiscovery_NoAuthentication_AndNoConfirmation()
    {
        var clock = new FakeTimeProvider();
        var budget = new ReadinessBudget(10, clock);
        clock.Advance(TimeSpan.FromSeconds(10));

        using var host = new ScriptExecutingPowerShellHost();
        var probe = new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0));
        var manager = BuildManager(RunningVmWithHeartbeat(), probe, host,
            new ReadinessSshExecClientFactory(), clock);

        var act = async () => await manager.WaitForReadyAsync(HostId, VmId, budget, Username, Password);

        await act.Should().ThrowAsync<ReadinessNotReachedException>();
        probe.ProbeCount.Should().Be(0, "no discovery may start after expiry");
        probe.EndpointCount.Should().Be(0);
        host.AuthenticationCount.Should().Be(0, "no authentication may start after expiry");
        host.ConfirmationCount.Should().Be(0);
    }

    [Fact]
    public async Task PopulatedRoutingHint_SkipsTheGuestOsProbeEntirely()
    {
        var clock = new FakeTimeProvider();
        var hintStore = new GuestRoutingHintStore();
        hintStore.Record(HostId, VmId, new GuestRoutingHint(GuestOsKind.Windows, null, 0));

        using var host = new ScriptExecutingPowerShellHost();
        var probe = new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0));
        var manager = BuildManager(RunningVmWithHeartbeat(), probe, host,
            new ReadinessSshExecClientFactory(), clock, hintStore);

        await manager.WaitForReadyAsync(HostId, VmId, new ReadinessBudget(300, clock), Username, Password);

        probe.ProbeCount.Should().Be(0, "a populated hint must short-circuit discovery");
        probe.EndpointCount.Should().Be(0);
    }

    [Fact]
    public async Task DeterminedRoute_IsRecordedIntoTheSharedHintStore_AndTheProbeIsBudgetGuarded()
    {
        var clock = new FakeTimeProvider();
        var hintStore = new GuestRoutingHintStore();
        using var host = new ScriptExecutingPowerShellHost();
        var budget = new ReadinessBudget(300, clock);
        var manager = BuildManager(RunningVmWithHeartbeat(),
            new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0)),
            host, new ReadinessSshExecClientFactory(), clock, hintStore);

        await manager.WaitForReadyAsync(HostId, VmId, budget, Username, Password);

        hintStore.TryGet(HostId, VmId, out var recorded).Should().BeTrue(
            "the route readiness determined must reach the shared store, not an isolated one");
        recorded!.GuestOs.Should().Be(GuestOsKind.Windows);
        budget.Boundaries.Select(boundary => boundary.Step).Should().ContainInOrder("classification", "kvp");
    }

    [Fact]
    public async Task UndeterminableGuest_KeepsTheRoutingRefusal_RatherThanDowngradingToHeartbeat()
    {
        var clock = new FakeTimeProvider();
        // The undetermined path takes the production classification-settle delay, which never completes unless the virtual clock is driven.
        using var pump = new VirtualClockPump(clock, TimeSpan.FromMilliseconds(100));
        using var host = new ScriptExecutingPowerShellHost();
        var manager = BuildManager(RunningVmWithHeartbeat(), new CountingGuestOsProbe(hint: null),
            host, new ReadinessSshExecClientFactory(), clock);

        var act = async () => await manager.WaitForReadyAsync(
            HostId, VmId, new ReadinessBudget(300, clock), Username, Password);

        await act.Should().ThrowAsync<GuestRoutingUnavailableException>();
        host.AuthenticationCount.Should().Be(0, "an undetermined guest must not be guessed at");
    }

    [Fact]
    public async Task CallerCancellation_ProducesCancellation_NotReadinessSuccess()
    {
        var clock = new FakeTimeProvider();
        using var host = new ScriptExecutingPowerShellHost();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var manager = BuildManager(RunningVmWithHeartbeat(),
            new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0)),
            host, new ReadinessSshExecClientFactory(), clock);

        var act = async () => await manager.WaitForReadyAsync(
            HostId, VmId, new ReadinessBudget(300, clock), Username, Password, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MissingCredentials_RefusesBeforeAnyGuestAccessIsAttempted()
    {
        var clock = new FakeTimeProvider();
        using var host = new ScriptExecutingPowerShellHost();
        var probe = new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0));
        var manager = BuildManager(RunningVmWithHeartbeat(), probe, host,
            new ReadinessSshExecClientFactory(), clock);

        using var _ = new ClearedGuestCredentialEnvironment();

        var act = async () => await manager.WaitForReadyAsync(
            HostId, VmId, new ReadinessBudget(300, clock), username: null, password: null);

        await act.Should().ThrowAsync<MissingCredentialsException>();
        host.AuthenticationCount.Should().Be(0);
        probe.ProbeCount.Should().Be(0);
    }

    /// <summary>Budget-only delay tests miss a manager loop that omits the wait. Check elapsed virtual time and the real loop's boundary
    /// timeline.</summary>
    [Fact]
    public async Task RetryLoop_WaitsBetweenAttempts_RatherThanSpinningWithoutElapsingBudget()
    {
        var clock = new FakeTimeProvider();
        using var pump = new VirtualClockPump(clock, TimeSpan.FromSeconds(1));
        using var host = new ScriptExecutingPowerShellHost();
        host.SetAuthMode("reject");
        var budget = new ReadinessBudget(30, clock);
        var manager = BuildManager(RunningVmWithHeartbeat(),
            new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0)),
            host, new ReadinessSshExecClientFactory(), clock);

        var act = async () => await manager.WaitForReadyAsync(HostId, VmId, budget, Username, Password);
        await act.Should().ThrowAsync<ReadinessNotReachedException>();

        budget.Boundaries.Should().Contain(boundary => boundary.Step == "retry-delay-completed",
            "the loop must actually await the inter-attempt delay, not merely check the budget");
        budget.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3),
            "a loop that never waits would exhaust its attempts with no virtual time consumed");
    }

    [Fact]
    public async Task ReadinessFailureMessage_CarriesNoSecret()
    {
        var clock = new FakeTimeProvider();
        using var pump = new VirtualClockPump(clock, TimeSpan.FromSeconds(5));
        using var host = new ScriptExecutingPowerShellHost();
        host.SetAuthMode("reject");
        var manager = BuildManager(RunningVmWithHeartbeat(),
            new CountingGuestOsProbe(new GuestRoutingHint(GuestOsKind.Windows, null, 0)),
            host, new ReadinessSshExecClientFactory(), clock);

        var act = async () => await manager.WaitForReadyAsync(
            HostId, VmId, new ReadinessBudget(30, clock), Username, "sup3r-s3cret-pw");

        var thrown = await act.Should().ThrowAsync<ReadinessNotReachedException>();
        thrown.Which.Message.Should().NotContain("sup3r-s3cret-pw");
    }
}
