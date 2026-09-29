using System.Collections.Immutable;
using System.Diagnostics;

namespace HyperV.Mcp.Server.Infrastructure;

public sealed record StartupEnvironmentValue(string Name, string Outcome, string Source);

public sealed record StartupProgress(
    string Stage,
    string? Edition,
    ImmutableArray<StartupEnvironmentValue> Environment);

public sealed record StartupTerminal(string State, string Detail, StartupProgress Progress, double ElapsedSeconds);

public sealed class StartupInitialization
{
    private readonly long _entryTimestamp = Stopwatch.GetTimestamp();
    private readonly TimeSpan _processAge;
    private readonly TimeSpan _initialRemaining;
    private readonly Action<string> _report;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StartupProgress _progress = new("Main entry", null, ImmutableArray<StartupEnvironmentValue>.Empty);
    private StartupTerminal? _terminal;
    private int _workerThreadId;
    private int _started;

    public StartupInitialization() : this(GetProcessAge(), Console.Error.WriteLine) { }

    internal StartupInitialization(TimeSpan processAge, Action<string> report)
    {
        _report = report;
        if (processAge < TimeSpan.Zero)
        {
            ClockAnomaly = "process start timestamp is in the future";
            _processAge = TimeSpan.Zero;
            _initialRemaining = TimeSpan.FromSeconds(1);
        }
        else
        {
            _processAge = processAge;
            _initialRemaining = TimeSpan.FromSeconds(120) - processAge;
        }
    }

    private static TimeSpan GetProcessAge()
    {
        using var process = Process.GetCurrentProcess();
        return DateTime.UtcNow - process.StartTime.ToUniversalTime();
    }

    public string? ClockAnomaly { get; }
    public double ElapsedSeconds => (_processAge + Stopwatch.GetElapsedTime(_entryTimestamp)).TotalSeconds;
    internal TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0,
        (_initialRemaining - Stopwatch.GetElapsedTime(_entryTimestamp)).Ticks));
    public StartupProgress Progress => Volatile.Read(ref _progress);
    public StartupTerminal? Terminal => Volatile.Read(ref _terminal);
    internal bool IsWorkerThread => Environment.CurrentManagedThreadId == Volatile.Read(ref _workerThreadId);
    internal Task Completion => _completed.Task;
    internal Func<PowerShellChildIdentity>? ReadChildIdentity { private get; set; }

    internal void SetStage(string stage, string? edition = null)
    {
        var previous = Progress;
        Volatile.Write(ref _progress, previous with { Stage = stage, Edition = edition });
    }

    internal void RecordEnvironment(StartupEnvironmentValue value)
    {
        var previous = Progress;
        var values = previous.Environment.Where(existing => existing.Name != value.Name).Append(value).ToImmutableArray();
        Volatile.Write(ref _progress, previous with { Environment = values });
    }

    internal async Task WatchAsync()
    {
        var remaining = Remaining;
        if (remaining > TimeSpan.Zero)
        {
            if (await Task.WhenAny(_completed.Task, Task.Delay(remaining)).ConfigureAwait(false) == _completed.Task)
                return;
        }
        Publish("TimedOutStillRunning", "Startup deadline exhausted; unfinished work is still running; not cancelled.");
    }

    internal void Start(IEnvironment environment, Action initialize, Func<string, string>? lookup = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Startup work was already started.");
        var thread = new Thread(() =>
        {
            Volatile.Write(ref _workerThreadId, Environment.CurrentManagedThreadId);
            try
            {
                NormalizeEnvironment(environment, lookup);
                ThrowIfUnavailable();
                initialize();
                Publish("Ready", "Startup completed.");
            }
            catch (Exception exception)
            {
                Publish("Failed", $"{exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                _completed.TrySetResult();
            }
        }) { IsBackground = true, Name = "PowerShell startup" };
        thread.Start();
    }

    internal void NormalizeEnvironment(IEnvironment environment, Func<string, string>? lookup = null)
    {
        foreach (var name in new[] { "COMPUTERNAME", "windir", "SystemRoot" })
        {
            SetStage($"environment recovery: {name}");
            if (!string.IsNullOrEmpty(environment.GetEnvironmentVariable(name)))
            {
                RecordEnvironment(new(name, "inherited", "process environment"));
                continue;
            }
            var source = name == "COMPUTERNAME" ? "Environment.MachineName" : "Environment.GetFolderPath(Windows)";
            RecordEnvironment(new(name, "missing; could not be recovered", source));
            try
            {
                var value = lookup is not null ? lookup(name) : name == "COMPUTERNAME"
                    ? Environment.MachineName : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrEmpty(value))
                    throw new InvalidOperationException($"{source} returned an empty value");
                environment.SetEnvironmentVariable(name, value);
                RecordEnvironment(new(name, "missing; recovered", source));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"{name} could not be recovered from {source}: {exception.Message}", exception);
            }
        }
    }

    internal bool Publish(string state, string detail)
    {
        if (state == "Ready" && Remaining == TimeSpan.Zero)
        {
            state = "TimedOutStillRunning";
            detail = "Startup deadline exhausted; unfinished work is still running; not cancelled.";
        }
        var progress = Progress;
        var terminal = new StartupTerminal(state, detail, progress, ElapsedSeconds);
        if (Interlocked.CompareExchange(ref _terminal, terminal, null) is not null)
            return false;
        _completed.TrySetResult();
        var missing = progress.Environment.Where(value => value.Outcome.StartsWith("missing", StringComparison.Ordinal)).ToArray();
        var recovery = missing.Length == 0 ? "No missing environment value is known."
            : string.Join("; ", missing.Select(value => $"{value.Name}: {value.Outcome} from {value.Source}"));
        // Only an unrecovered value justifies sending the operator after OS access; a recovered one is resolved.
        var unresolved = missing.Any(value => value.Outcome.EndsWith("could not be recovered", StringComparison.Ordinal));
        var action = state == "Ready" ? ""
            : unresolved ? " Restore access to unresolved OS values, collect this diagnostic, and restart the server."
            : " Collect this diagnostic and restart the server.";
        var child = ReadChildIdentity?.Invoke();
        var childDetail = child?.ProcessId is int processId
            ? $"pid={processId}; startUtc={child.StartTimeUtc:O}" : "child not yet started";
        _report($"Startup terminal: {terminal.State}; {terminal.Detail} Stage={progress.Stage}; edition={progress.Edition ?? "not selected"}; " +
            $"elapsed={terminal.ElapsedSeconds:F3}s; child={childDetail}. {recovery}{action}" +
            (ClockAnomaly is null ? "" : $" Clock anomaly: {ClockAnomaly}; bounded 1s remainder."));
        return true;
    }

    internal void ThrowIfUnavailable()
    {
        var terminal = Terminal;
        if (terminal is not null && terminal.State != "Ready")
            throw new InvalidOperationException($"{terminal.State}: {terminal.Detail} Restart the server to retry.");
    }
}
