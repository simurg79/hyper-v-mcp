namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Thrown when a tool name is not found in the dispatcher registry.
/// Maps to a structured error response (not a standard MCP error code since
/// TOOL_NOT_FOUND is a dispatch-level concern, not a business error).
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
/// </summary>
public class ToolNotFoundException : Exception
{
    public string ToolName { get; }

    public ToolNotFoundException(string toolName)
        : base($"Tool '{toolName}' is not registered")
    {
        ToolName = toolName;
    }
}
