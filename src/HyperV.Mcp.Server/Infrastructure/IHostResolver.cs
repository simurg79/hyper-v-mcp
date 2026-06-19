using HyperV.Mcp.Server.Configuration;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Resolves hostId parameters to HostProfile configurations.
/// See /myplans/remoting/remoting-design.md — Host Connection Configuration.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: All tools accept optional hostId (defaults to "local").
/// 
/// The resolver is responsible for:
/// - Defaulting null/empty hostId to the configured default (typically "local")
/// - Looking up HostProfile by hostId from ServerOptions.Hosts
/// - Throwing or returning error when hostId is not found
/// </summary>
public interface IHostResolver
{
    /// <summary>
    /// Resolve a hostId to a HostProfile. Returns null if the hostId is not configured.
    /// If hostId is null or empty, uses the default hostId from ServerOptions.
    /// </summary>
    HostProfile? Resolve(string? hostId);

    /// <summary>
    /// Resolve a hostId to a HostProfile, throwing if not found.
    /// </summary>
    /// <exception cref="HostNotFoundException">When no profile matches the hostId.</exception>
    HostProfile ResolveRequired(string? hostId);
}
