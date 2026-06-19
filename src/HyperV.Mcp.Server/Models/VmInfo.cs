using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// VM information returned by lifecycle and discovery operations.
/// See /myplans/vm-management/vm-management-design.md — VM metadata.
/// </summary>
public class VmInfo
{
    [JsonPropertyName("vmId")]
    public string VmId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("hostId")]
    public string HostId { get; set; } = string.Empty;

    [JsonPropertyName("cpuCount")]
    public int CpuCount { get; set; }

    [JsonPropertyName("memoryMB")]
    public long MemoryMB { get; set; }

    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; set; }

    /// <summary>
    /// Classification reason for rows returned by <c>vm_cleanup_orphans</c>.
    /// One of: <c>"orphan"</c> (parseable creation tag older than the cutoff,
    /// eligible for destroy under <c>dryRun:false</c>) or <c>"unknown-age"</c>
    /// (tagged VM whose creation timestamp could not be parsed; reported but
    /// never auto-destroyed — see LF-D10).
    /// Null/absent for all other tools (e.g. <c>vm_list</c>, <c>vm_status</c>).
    /// </summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}
