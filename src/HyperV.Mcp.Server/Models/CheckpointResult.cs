using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Result of a checkpoint operation.
/// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
/// </summary>
public class CheckpointResult
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("vmId")]
    public string VmId { get; set; } = string.Empty;

    [JsonPropertyName("checkpointName")]
    public string CheckpointName { get; set; } = string.Empty;

    [JsonPropertyName("checkpoints")]
    public List<CheckpointInfo>? Checkpoints { get; set; }
}

/// <summary>
/// Metadata for a single checkpoint.
/// </summary>
public class CheckpointInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}
