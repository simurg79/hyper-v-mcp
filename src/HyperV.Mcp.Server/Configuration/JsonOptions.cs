using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Configuration;

/// <summary>
/// Shared JSON serialization settings for consistent behavior across the project.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D5: Return Task&lt;string&gt; with JSON serialization.
/// </summary>
public static class JsonOptions
{
    /// <summary>
    /// Default serialization options used for MCP response envelopes
    /// and internal JSON operations.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}
