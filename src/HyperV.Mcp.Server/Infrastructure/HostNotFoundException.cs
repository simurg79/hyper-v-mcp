namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Thrown when a hostId cannot be resolved to a configured HostProfile.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: HOST_NOT_FOUND.
/// </summary>
public class HostNotFoundException : Exception
{
    public string HostId { get; }

    public HostNotFoundException(string hostId)
        : base($"No host with the specified hostId '{hostId}' is configured")
    {
        HostId = hostId;
    }

    public HostNotFoundException(string hostId, Exception innerException)
        : base($"No host with the specified hostId '{hostId}' is configured", innerException)
    {
        HostId = hostId;
    }
}
