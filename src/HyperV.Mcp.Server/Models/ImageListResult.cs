using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Result of <c>vm_list_images</c> enumeration. Carries discovery metadata so callers
/// can distinguish "no image directory configured yet" from "directory configured and
/// empty" without parsing error strings.
/// See /myplans/vm-management/storage/storage-design.md — ST-D7.
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/54.
/// </summary>
public class ImageListResult
{
    /// <summary>
    /// Enumerated images. Empty (not null) when none are configured or none are present.
    /// </summary>
    [JsonPropertyName("images")]
    public IReadOnlyList<ImageInfo> Images { get; set; } = System.Array.Empty<ImageInfo>();

    /// <summary>
    /// Convenience count for callers (mirrors <c>Images.Count</c>).
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>
    /// True when at least one of <c>HYPERV_MCP_IMAGE_DIR</c>, host-profile
    /// <c>BaseVhdxPath</c>, or <c>HYPERV_MCP_BASE_VHDX</c> resolved to a directory.
    /// False when all three sources are absent (soft "not configured" state per ST-D7).
    /// </summary>
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    /// <summary>
    /// The resolved image directory, when <see cref="Configured"/> is true. Null otherwise.
    /// </summary>
    [JsonPropertyName("imageDir")]
    public string? ImageDir { get; set; }

    /// <summary>
    /// Operator-facing hint emitted only in the unconfigured case so the caller knows
    /// how to enable enumeration. Null when configured.
    /// </summary>
    [JsonPropertyName("hint")]
    public string? Hint { get; set; }
}
