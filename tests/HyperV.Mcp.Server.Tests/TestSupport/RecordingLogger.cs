using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>
/// Captures formatted log records. Discarding a component's logs is what let the LGR-D33/D35
/// diagnostics defects ship: where message-plus-marker is the only supported discriminator, an
/// unobserved marker is an unguarded contract.
/// See internal documentation — LGR-D33, LGR-D35.
/// </summary>
internal sealed class RecordingLogger<TCategory> : ILogger<TCategory>
{
    internal List<(LogLevel Level, string Message)> Records { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Records.Add((logLevel, formatter(state, exception)));

    internal IEnumerable<string> At(LogLevel level)
        => Records.Where(record => record.Level == level).Select(record => record.Message);
}
