using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Response shape for vm_run_command and vm_run_script.
/// See /myplans/execution/commands/commands-design.md — Response Shape.
/// </summary>
public class CommandResult
{
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("stdout")]
    public string Stdout { get; set; } = string.Empty;

    [JsonPropertyName("stderr")]
    public string Stderr { get; set; } = string.Empty;

    [JsonPropertyName("timedOut")]
    public bool TimedOut { get; set; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }
}
