using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Standard MCP response envelope for all tool responses.
/// See internal documentation — MCP-D2: Consistent response envelope.
/// </summary>
public class McpToolResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Machine-readable error code from the taxonomy.
    /// See internal documentation — Error Code Taxonomy.
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Optional VM state at the time of the error (e.g., "OsBooting").
    /// See internal documentation — ADR-8.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>
    /// Optional structured error details. Currently carries the
    /// <c>vm_create</c> rollback block per LF-D17 (Issue #164):
    /// <c>{ vmName, phase, rollback: { performed, succeeded, elapsedMs, residualArtifacts } }</c>.
    /// Null on success and on errors that do not produce a structured detail body.
    /// </summary>
    [JsonPropertyName("details")]
    public object? Details { get; set; }

    public static McpToolResponse Ok(object? data = null) =>
        new() { Success = true, Data = data };

    public static McpToolResponse Fail(string error, string errorCode, string? state = null) =>
        new() { Success = false, Error = error, ErrorCode = errorCode, State = state };
}
