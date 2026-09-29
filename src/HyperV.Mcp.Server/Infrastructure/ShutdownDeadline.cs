using System.Diagnostics;

namespace HyperV.Mcp.Server.Infrastructure;

public sealed class ShutdownDeadline
{
    private long _started;

    public void Begin() => Interlocked.CompareExchange(ref _started, Stopwatch.GetTimestamp(), 0);

    public TimeSpan Remaining
    {
        get
        {
            Begin();
            return TimeSpan.FromTicks(Math.Max(0,
                (TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(Volatile.Read(ref _started))).Ticks));
        }
    }
}
