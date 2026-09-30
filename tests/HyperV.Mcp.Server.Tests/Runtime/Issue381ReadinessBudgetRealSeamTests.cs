using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>Budget boundaries and cadence must follow production delays; advancing time independently would hide a missing wait. These
/// checks use virtual time, not live-duration measurements.</summary>
[Trait("Category", "Runtime")]
public class Issue381ReadinessBudgetRealSeamTests
{
    private const string VmId = "12345678-1234-1234-1234-123456789abc";

    /// <summary>Auto-advance here moves time only when awaited; deleting a production delay removes elapsed time and fails timestamp
    /// assertions.</summary>
    private static FakeTimeProvider DelayDrivenClock() =>
        new() { AutoAdvanceAmount = TimeSpan.Zero };

    [Fact]
    public void RequestedBudget_MustBePositive_AndIsNeverRaisedToAFloor()
    {
        var oneSecond = new ReadinessBudget(1, DelayDrivenClock());
        oneSecond.RequestedSeconds.Should().Be(1);
        oneSecond.EffectiveSeconds.Should().Be(1, "no minimum such as 10 seconds may be imposed");

        new ReadinessBudget(clock: DelayDrivenClock()).RequestedSeconds.Should().Be(300,
            "omission defaults to 300 seconds");

        var zero = () => new ReadinessBudget(0, DelayDrivenClock());
        zero.Should().Throw<ArgumentOutOfRangeException>();
        var negative = () => new ReadinessBudget(-5, DelayDrivenClock());
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Delay_ActuallyConsumesBudget_OnTheSharedClock()
    {
        var clock = DelayDrivenClock();
        var budget = new ReadinessBudget(300, clock);
        budget.Elapsed.Should().Be(TimeSpan.Zero);

        var waiting = budget.DelayAsync(TimeSpan.FromSeconds(3), "retry-delay", VmId, CancellationToken.None);
        waiting.IsCompleted.Should().BeFalse("the production delay must actually wait on the shared clock");

        clock.Advance(TimeSpan.FromSeconds(3));
        await waiting;

        budget.Elapsed.Should().Be(TimeSpan.FromSeconds(3),
            "a deleted delay would leave elapsed time at zero");
        budget.Boundaries.Should().Contain(boundary =>
            boundary.Step == "retry-delay-completed" && boundary.Elapsed == TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task RepeatedDelays_AccumulateCadence_ProportionalToTheNumberOfWaits()
    {
        var clock = DelayDrivenClock();
        var budget = new ReadinessBudget(300, clock);

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var waiting = budget.DelayAsync(TimeSpan.FromSeconds(3), "retry-delay", VmId, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(3));
            await waiting;
        }

        budget.Elapsed.Should().Be(TimeSpan.FromSeconds(9),
            "three three-second waits must show as nine seconds of consumed budget");
        budget.Boundaries.Count(boundary => boundary.Step == "retry-delay-completed").Should().Be(3);
    }

    [Fact]
    public void Check_RefusesToStartWork_OnceTheBudgetIsExhausted()
    {
        var clock = DelayDrivenClock();
        var budget = new ReadinessBudget(10, clock);

        budget.Check("authentication", VmId, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(10));

        var act = () => budget.Check("confirmation", VmId, CancellationToken.None);
        act.Should().Throw<ReadinessNotReachedException>()
            .Which.Message.Should().Contain("wait budget exhausted");
        budget.Boundaries.Should().Contain(boundary => boundary.Step == "confirmation" && !boundary.Allowed,
            "the refused boundary must be recorded, not silently skipped");
    }

    [Fact]
    public void Check_AtExactExpiry_RefusesRatherThanAllowingOneMoreStep()
    {
        var clock = DelayDrivenClock();
        var budget = new ReadinessBudget(10, clock);
        clock.Advance(TimeSpan.FromSeconds(10));

        var act = () => budget.Check("confirmation", VmId, CancellationToken.None);
        act.Should().Throw<ReadinessNotReachedException>("at expiry no further readiness work may start");
    }

    [Fact]
    public void Check_JustBeforeExpiry_StillAllowsTheStepToStart()
    {
        var clock = DelayDrivenClock();
        var budget = new ReadinessBudget(10, clock);
        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(1));

        var act = () => budget.Check("confirmation", VmId, CancellationToken.None);
        act.Should().NotThrow("a step begun with budget remaining is permitted to start");
    }

    [Fact]
    public void Check_HonoursCallerCancellation_DistinctlyFromBudgetExpiry()
    {
        var budget = new ReadinessBudget(300, DelayDrivenClock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => budget.Check("authentication", VmId, cancellation.Token);
        act.Should().Throw<OperationCanceledException>(
            "caller cancellation is not a budget timeout and must not be reported as one");
    }

    [Fact]
    public async Task Delay_IsClampedToRemainingBudget_RatherThanOverrunning()
    {
        var clock = DelayDrivenClock();
        var budget = new ReadinessBudget(10, clock);
        clock.Advance(TimeSpan.FromSeconds(8));

        var waiting = budget.DelayAsync(TimeSpan.FromSeconds(30), "retry-delay", VmId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        await waiting;

        budget.Elapsed.Should().Be(TimeSpan.FromSeconds(10),
            "the wait must be clamped to the remaining budget, not the full requested delay");
    }

    [Fact]
    public void Failure_DisclosesRequestedAndEffectiveBudgets_AndTheLastObservation()
    {
        var budget = new ReadinessBudget(42, DelayDrivenClock())
        {
            LastObservation = "Credential rejection observed for username 'readiness-user'.",
        };

        var failure = budget.Failure(VmId, "wait budget exhausted");

        failure.Message.Should().Contain(VmId)
            .And.Contain("Requested wait budget: 42s")
            .And.Contain("effective wait budget: 42s")
            .And.Contain("Credential rejection observed for username 'readiness-user'")
            .And.Contain("guest-login readiness was not confirmed")
            .And.Contain("this does not prove the guest will never be ready");
    }

    [Fact]
    public void Failure_WithNoObservation_SaysTheCauseIsUnknown_RatherThanAssertingNotReady()
    {
        var failure = new ReadinessBudget(30, DelayDrivenClock()).Failure(VmId, "observation failed");

        failure.Message.Should().Contain("observation failed")
            .And.Contain("Cause unknown")
            .And.Contain("this does not prove the guest will never be ready");
    }

    [Fact]
    public void Boundaries_RecordEveryCrossing_AgainstTheSharedClock()
    {
        var clock = DelayDrivenClock();
        var budget = new ReadinessBudget(300, clock);

        budget.Check("classification", VmId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        budget.Record("kvp-completed");
        clock.Advance(TimeSpan.FromSeconds(2));
        budget.Check("authentication", VmId, CancellationToken.None);

        budget.Boundaries.Select(boundary => boundary.Step).Should()
            .ContainInOrder("entry", "classification", "kvp-completed", "authentication");
        budget.Boundaries.Single(boundary => boundary.Step == "kvp-completed").Elapsed
            .Should().Be(TimeSpan.FromSeconds(1));
        budget.Boundaries.Single(boundary => boundary.Step == "authentication").Elapsed
            .Should().Be(TimeSpan.FromSeconds(3));
    }
}
