using Microsoft.Extensions.Time.Testing;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>Drive FakeTimeProvider in the background so Task.Delay timers fire; read-only auto-advance leaves delays stuck. The pump
/// advances even without production waits, so it cannot prove cadence: delay-deletion assertions belong in unpumped budget tests. Here it
/// only keeps bounded loops live.</summary>
public sealed class VirtualClockPump : IDisposable
{
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _loop;

    public VirtualClockPump(FakeTimeProvider clock, TimeSpan step)
    {
        // Use a background thread: an outliving pooled task can keep the test host alive after success, mimicking a product hang.
        _loop = new Thread(() =>
        {
            while (!_stop.IsSet)
            {
                clock.Advance(step);
                _stop.Wait(TimeSpan.FromMilliseconds(2));
            }
        })
        {
            IsBackground = true,
            Name = nameof(VirtualClockPump),
        };
        _loop.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _loop.Join(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }
}
