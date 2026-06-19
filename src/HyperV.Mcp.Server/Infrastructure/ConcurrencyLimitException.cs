namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Thrown when a concurrency limit is reached and the operation cannot proceed.
/// See internal documentation — CC-D4: Non-blocking TryWait.
/// </summary>
public class ConcurrencyLimitException : Exception
{
    public ConcurrencyLimitException(string message) : base(message) { }
    public ConcurrencyLimitException(string message, Exception innerException) : base(message, innerException) { }
}
