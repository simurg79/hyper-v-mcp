using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

[Collection("EnvVarMutating")]
[Trait("Category", "Runtime")]
public class Issue392StartupRealSeamTests
{
    [Fact]
    public void AppendixA1_RealProcessRecoveryUsesOsValuesAndPreservesInheritedValues()
    {
        var names = new[] { "COMPUTERNAME", "windir", "SystemRoot" };
        var original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            var startup = new StartupInitialization(TimeSpan.Zero, _ => { });
            startup.NormalizeEnvironment(new SystemEnvironment());
            Environment.GetEnvironmentVariable("COMPUTERNAME").Should().Be(Environment.MachineName);
            Environment.GetEnvironmentVariable("windir").Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Environment.GetEnvironmentVariable("SystemRoot").Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            startup.Progress.Environment.Should().OnlyContain(value => value.Outcome == "missing; recovered");
            startup.NormalizeEnvironment(new SystemEnvironment(), _ => throw new Exception("must not look up inherited values"));
            startup.Progress.Environment.Should().OnlyContain(value => value.Outcome == "inherited");
        }
        finally
        {
            foreach (var value in original) Environment.SetEnvironmentVariable(value.Key, value.Value);
        }
    }

    [Fact]
    public async Task AppendixA5_OsRecoveryFailureIsNamedByProductionWorker()
    {
        var reports = new ConcurrentQueue<string>();
        var elapsed = Stopwatch.StartNew();
        var startup = new StartupInitialization(TimeSpan.Zero, reports.Enqueue);
        startup.Start(new FakeEnvironment(), () => throw new Exception("init must not run"),
            name => throw new InvalidOperationException("OS lookup unavailable"));
        await WaitForReport(reports);
        elapsed.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(125));
        startup.Terminal!.State.Should().Be("Failed");
        reports.Single().Should().Contain("COMPUTERNAME").And.Contain("could not be recovered")
            .And.Contain("OS lookup unavailable").And.Contain("restart");
        reports.Single().Should().NotContain("TimedOut").And.NotContain("cancelled");
    }

    [Fact]
    public async Task AppendixA6_OrdinaryFailureIsDistinctFromTimeout()
    {
        var reports = new ConcurrentQueue<string>();
        var startup = new StartupInitialization(TimeSpan.Zero, reports.Enqueue);
        startup.Start(new FakeEnvironment(), () => throw new InvalidOperationException("ordinary failure"));
        await WaitForReport(reports);
        startup.Terminal!.State.Should().Be("Failed");
        reports.Single().Should().Contain("ordinary failure").And.Contain("restart")
            .And.NotContain("TimedOut").And.NotContain("cancelled");
    }

    [Fact]
    public async Task ExhaustedLaunchBudgetIsNotReset()
    {
        var reports = new ConcurrentQueue<string>();
        var startup = new StartupInitialization(TimeSpan.FromSeconds(121), reports.Enqueue);
        await startup.WatchAsync().WaitAsync(TimeSpan.FromSeconds(1));
        startup.Remaining.Should().Be(TimeSpan.Zero);
        startup.Terminal!.State.Should().Be("TimedOutStillRunning");
        startup.ClockAnomaly.Should().BeNull();
        reports.Single().Should().Contain("No missing environment value is known");
    }

    [Fact]
    public void FutureTimestampGetsOnlyShortNamedRemainder()
    {
        var startup = new StartupInitialization(TimeSpan.FromSeconds(-1), _ => { });
        startup.ClockAnomaly.Should().Contain("future");
        startup.Remaining.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AppendixA4_AtomicWinnerRetainsDetailAndReportsExactlyOnce()
    {
        var reports = new ConcurrentQueue<string>();
        var startup = new StartupInitialization(TimeSpan.Zero, reports.Enqueue);
        startup.SetStage("runspace.Open", "WindowsPowerShell51");
        await Task.WhenAll(Enumerable.Range(0, 100).Select(iter => Task.Run(() =>
            startup.Publish(iter % 2 == 0 ? "Failed" : "TimedOutStillRunning", $"reason-{iter}"))));
        var winner = startup.Terminal!;
        reports.Should().ContainSingle();
        reports.Single().Should().Contain(winner.Detail).And.Contain(winner.State);
        startup.SetStage("late completion");
        startup.Publish("Ready", "late success").Should().BeFalse();
        startup.Terminal.Should().BeSameAs(winner);
        reports.Should().ContainSingle();
    }

    // SE-D4a: without the late downgrade a worker finishing after the budget would publish Ready.
    [Fact]
    public void LateReadyIsDowngradedToTimedOutStillRunning()
    {
        var reports = new ConcurrentQueue<string>();
        var startup = new StartupInitialization(TimeSpan.FromSeconds(121), reports.Enqueue);
        startup.Remaining.Should().Be(TimeSpan.Zero, "the budget must already be exhausted for this case to mean anything");
        startup.Publish("Ready", "late success").Should().BeTrue();
        var terminal = startup.Terminal!;
        terminal.State.Should().Be("TimedOutStillRunning");
        terminal.State.Should().NotBe("Ready");
        terminal.Detail.Should().Contain("still running").And.NotContain("late success");
        reports.Single().Should().Contain("TimedOutStillRunning").And.Contain("restart")
            .And.NotContain("Startup terminal: Ready");
        startup.Invoking(value => value.ThrowIfUnavailable())
            .Should().Throw<InvalidOperationException>().WithMessage("*TimedOutStillRunning*");
    }

    // An unexhausted budget must still publish Ready, so the downgrade cannot be a blanket rejection.
    [Fact]
    public void ReadyWithinBudgetIsNotDowngraded()
    {
        var reports = new ConcurrentQueue<string>();
        var startup = new StartupInitialization(TimeSpan.Zero, reports.Enqueue);
        startup.Publish("Ready", "Startup completed.").Should().BeTrue();
        startup.Terminal!.State.Should().Be("Ready");
        reports.Single().Should().Contain("Startup terminal: Ready").And.NotContain("restart");
    }

    [Fact]
    public void AppendixA3_PreStartIdentityHasExplicitMarker()
    {
        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);
        var identity = host.GetChildIdentity();
        identity.Status.Should().Be("child not yet started");
        identity.ProcessId.Should().BeNull();
        host.GetInitDiagnostics().ChildIdentity.Should().Be(identity);
    }

    private static async Task WaitForReport(ConcurrentQueue<string> reports)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (reports.IsEmpty) await Task.Delay(10, deadline.Token);
    }
}
