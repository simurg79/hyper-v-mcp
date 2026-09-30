namespace HyperV.Mcp.Server.Infrastructure;

internal sealed class VmLookupFailedException : Exception
{
    internal const string SafeMessage = "Failed to inspect the host for VM name lookup.";

    public int? ExitCode { get; }
    public string Stderr { get; }

    public VmLookupFailedException(int? exitCode = null, string stderr = "")
        : base(SafeMessage)
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }
}
