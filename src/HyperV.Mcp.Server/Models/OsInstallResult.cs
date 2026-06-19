using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Result model for OS installation from ISO via <c>vm_os_install</c>.
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — Response.
/// </summary>
public class OsInstallResult
{
    [JsonPropertyName("vmId")]
    public string VmId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("processorCount")]
    public int ProcessorCount { get; set; }

    [JsonPropertyName("memoryMB")]
    public long MemoryMB { get; set; }

    [JsonPropertyName("installationDurationSeconds")]
    public int InstallationDurationSeconds { get; set; }

    [JsonPropertyName("bootstrapDurationSeconds")]
    public int BootstrapDurationSeconds { get; set; }

    [JsonPropertyName("totalDurationSeconds")]
    public int TotalDurationSeconds { get; set; }

    [JsonPropertyName("guestIpAddress")]
    public string? GuestIpAddress { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}
