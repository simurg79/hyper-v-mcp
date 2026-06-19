using HyperV.Mcp.Server.Configuration;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Resolves hostId parameters to HostProfile configurations.
/// See /myplans/remoting/remoting-design.md — Host Connection Configuration.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: All tools accept optional hostId (defaults to "local").
///
/// Resolution semantics:
/// - null/empty hostId defaults to ServerOptions.DefaultHostId (typically "local")
/// - Known hostId returns the matching HostProfile from ServerOptions.Hosts
/// - Unknown hostId returns null (Resolve) or throws HostNotFoundException (ResolveRequired)
/// </summary>
public class HostResolver : IHostResolver
{
    private readonly ServerOptions _options;

    public HostResolver(ServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Non-throwing variant: returns null for unknown hostId.
    /// Defaults null/empty hostId to ServerOptions.DefaultHostId per MCP-D3.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3.
    /// </remarks>
    public HostProfile? Resolve(string? hostId)
    {
        // Default null/empty hostId to the configured default (MCP-D3).
        var effectiveHostId = string.IsNullOrEmpty(hostId)
            ? _options.DefaultHostId
            : hostId;

        // Look up the host profile in the configured hosts dictionary.
        if (_options.Hosts.TryGetValue(effectiveHostId, out var profile))
        {
            return profile;
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Throwing variant: raises HostNotFoundException for unknown hostId.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: HOST_NOT_FOUND.
    /// </remarks>
    public HostProfile ResolveRequired(string? hostId)
    {
        var profile = Resolve(hostId);
        if (profile is null)
        {
            // Use the effective hostId for the error message so the caller
            // sees the actual ID that was looked up, not the raw input.
            var effectiveHostId = string.IsNullOrEmpty(hostId)
                ? _options.DefaultHostId
                : hostId;
            throw new HostNotFoundException(effectiveHostId);
        }

        return profile;
    }
}
