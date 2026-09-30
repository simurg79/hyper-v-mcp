using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// VM information returned by lifecycle and discovery operations.
/// See internal documentation — VM metadata.
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
    /// Classification reason for <c>vm_cleanup_orphans</c> rows (see CO-D3). One of:
    /// <c>"orphan-candidate"</c> — owned, <c>role=ephemeral</c>, parseable creation tag
    /// older than the cutoff; destroyed under <c>dryRun:false</c>. Or
    /// <c>"needs-attention"</c> — owned but missing/non-ephemeral role or
    /// missing/unparseable creation timestamp; reported, never auto-destroyed.
    /// Null/absent for other tools (e.g. <c>vm_list</c>, <c>vm_status</c>).
    /// </summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>
    /// Issue #283 / FR-7: whether <c>vm_create</c> applied a caller-supplied administrator
    /// password to this VM. Present on <c>vm_create</c> results only; absent elsewhere.
    /// Carries no representation of the password itself.
    /// See internal documentation — VCAP-D4.
    /// </summary>
    [JsonPropertyName("passwordApplied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PasswordApplied { get; set; }
}
