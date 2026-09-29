namespace HyperV.Mcp.Server.Infrastructure;

public sealed record ReadinessBoundary(string Step, TimeSpan Elapsed, bool Allowed);

public sealed class ReadinessBudget
{
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly List<ReadinessBoundary> _boundaries = new();

    public ReadinessBudget(int timeoutSeconds = 300, TimeProvider? clock = null)
    {
        if (timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Wait budget must be positive.");

        _clock = clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
        RequestedSeconds = timeoutSeconds;
        EffectiveSeconds = timeoutSeconds;
        Record("entry");
    }

    public int RequestedSeconds { get; }
    public int EffectiveSeconds { get; }
    public TimeSpan Elapsed => _clock.GetElapsedTime(_started);
    public TimeSpan Remaining => TimeSpan.FromSeconds(EffectiveSeconds) - Elapsed;
    public IReadOnlyList<ReadinessBoundary> Boundaries => _boundaries.AsReadOnly();
    public string LastObservation { get; set; } = "Cause unknown; no guest-login observation completed.";

    public void Check(string step, string vmId, CancellationToken ct)
    {
        var elapsed = Elapsed;
        var allowed = !ct.IsCancellationRequested && elapsed < TimeSpan.FromSeconds(EffectiveSeconds);
        _boundaries.Add(new ReadinessBoundary(step, elapsed, allowed));
        ct.ThrowIfCancellationRequested();
        if (!allowed)
            throw Failure(vmId, "wait budget exhausted");
    }

    public void Record(string step) => _boundaries.Add(new ReadinessBoundary(step, Elapsed, true));

    public async Task DelayAsync(TimeSpan delay, string step, string vmId, CancellationToken ct)
    {
        Check(step, vmId, ct);
        var remaining = Remaining;
        if (remaining <= TimeSpan.Zero)
            throw Failure(vmId, "wait budget exhausted");
        await Task.Delay(delay < remaining ? delay : remaining, _clock, ct).ConfigureAwait(false);
        Record(step + "-completed");
        ct.ThrowIfCancellationRequested();
    }

    public ReadinessNotReachedException Failure(string vmId, string reason) => new(
        vmId,
        $"Could not determine guest-login readiness for VM '{vmId}': {reason}; guest-login readiness was not confirmed. " +
        $"Requested wait budget: {RequestedSeconds}s; effective wait budget: {EffectiveSeconds}s. " +
        $"Last observation: {LastObservation} " +
        "Inspect guest access configuration and retry after further boot progress; this does not prove the guest will never be ready.",
        artifactScrubbed: true);
}
