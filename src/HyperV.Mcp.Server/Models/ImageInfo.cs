using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Information about an available base VHDX image.
/// See /myplans/vm-management/storage/storage-design.md — Base Image Enumeration.
/// </summary>
public class ImageInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sizeGB")]
    public double SizeGB { get; set; }

    [JsonPropertyName("maxSizeGB")]
    public double MaxSizeGB { get; set; }

    [JsonPropertyName("vhdType")]
    public string VhdType { get; set; } = string.Empty;

    [JsonPropertyName("parentPath")]
    public string? ParentPath { get; set; }

    /// <summary>
    /// Indicates that this image is a sysprep-generalized base image suitable for
    /// reuse as a parent for differencing disks. Defaults to <c>false</c>.
    /// Base images produced by <c>vm_create_base_image</c> (Issue #51) set this to
    /// <c>true</c> after a successful <c>sysprep /generalize /shutdown</c> + host-side
    /// VHDX copy. Enumeration via <c>vm_list_images</c> currently leaves this at the
    /// default because generalization is not introspectable from VHDX metadata.
    /// </summary>
    [JsonPropertyName("generalized")]
    public bool Generalized { get; set; }
}
