using HyperV.Mcp.Server.Models;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Maps internal exceptions to MCP response envelope shapes.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped — never propagated as MCP protocol errors.
/// 
/// This class centralizes the exception-to-error-code mapping so that tool handlers
/// don't need to duplicate error mapping logic.
/// </summary>
public interface IErrorMapper
{
    /// <summary>
    /// Map an exception to an McpToolResponse.Fail with the appropriate error code.
    /// </summary>
    McpToolResponse MapException(Exception ex, string? vmState = null);
}
