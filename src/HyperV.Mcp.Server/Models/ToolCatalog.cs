namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Defines the complete MCP tool catalog with metadata.
/// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
/// 
/// This is the authoritative list of tools the server must register.
/// Tests validate that all tools in this catalog are discoverable.
/// </summary>
public static class ToolCatalog
{
    /// <summary>
    /// All tool definitions ordered by category, matching the design doc.
    /// Exposed as IReadOnlyList backed by an array to prevent consumers from
    /// casting back to a mutable List&lt;T&gt;.
    /// </summary>
    public static readonly IReadOnlyList<ToolDefinition> AllTools = Array.AsReadOnly(new ToolDefinition[]
    {
        // Health
        new("vm_echo", "Echo message back — health check", ToolCategory.Health, ToolPriority.P0),
        new("vm_diag", "Diagnostic tool — reports execution context, privileges, and environment", ToolCategory.Health, ToolPriority.P0),

        // Lifecycle
        new("vm_create", "Create VM from VHDX — autoStart (default: false) controls whether VM is started after creation. verifyBaseImageHash (default: true) enforces ST-D6 base-VHDX SHA-256 mutation guard (force-recomputed post-create); set to false to skip both pre-hash lookup and post-recompute (operator-accepted ADR-4 trade-off; preserved-stat mutations go undetected). See Issue #169 / VC-D6 / VC-D8. Performance note. vm_create verifies the base VHDX with SHA-256 before and after the differencing clone (see ADR-4 / ST-D6). On a cold OS page cache this is roughly 2 s/GB per full-file pass (~60 s for each cold 30 GB read). The persisted sidecar .sha256 only short-circuits the pre-hash lookup on stat-tuple match; vm_create still force-recomputes the post-create hash. The default request timeout is 120 seconds; override via the HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS environment variable (range 60–600) if you regularly create from bases larger than ~50 GB on slow storage. You may also pass verifyBaseImageHash: false for an individual call to skip the hash check entirely — this accepts the documented ADR-4 trade-off (preserved-stat mutations are not detected for that call).", ToolCategory.Lifecycle, ToolPriority.P0),
        new("vm_start", "Start a stopped VM", ToolCategory.Lifecycle, ToolPriority.P0),
        new("vm_stop", "Stop a VM (graceful or force)", ToolCategory.Lifecycle, ToolPriority.P0),
        new("vm_restart", "Restart a VM", ToolCategory.Lifecycle, ToolPriority.P1),
        new("vm_os_install", "Install OS from ISO image — fully automated, single call", ToolCategory.Lifecycle, ToolPriority.P1),
        new("vm_destroy", "Stop, remove VM, and cleanup resources", ToolCategory.Lifecycle, ToolPriority.P0),
        new("vm_pause", "Pause a running VM", ToolCategory.Lifecycle, ToolPriority.P2),
        new("vm_resume", "Resume a paused VM", ToolCategory.Lifecycle, ToolPriority.P2),

        // Discovery
        new("vm_list", "List VMs with filtering", ToolCategory.Discovery, ToolPriority.P0),
        new("vm_status", "Get detailed VM status", ToolCategory.Discovery, ToolPriority.P0),
        new("vm_wait_ready", "Wait for VM readiness state", ToolCategory.Discovery, ToolPriority.P1),

        // Execution
        new("vm_run_command", "Execute single command on guest VM", ToolCategory.Execution, ToolPriority.P0),
        new("vm_run_script", "Execute multi-line script on guest VM", ToolCategory.Execution, ToolPriority.P1),

        // File Transfer
        new("vm_copy_file", "Copy file or directory from host to guest", ToolCategory.FileTransfer, ToolPriority.P0),
        new("vm_get_file", "Retrieve file from guest to host", ToolCategory.FileTransfer, ToolPriority.P1),

        // Checkpoints
        new("vm_checkpoint", "Create, restore, list, or delete checkpoints", ToolCategory.Checkpoints, ToolPriority.P1),

        // Storage
        new("vm_list_images", "List available base VHDX images", ToolCategory.Storage, ToolPriority.P1),
        new("vm_create_base_image", "Sysprep a Running VM, merge checkpoints, and copy the VHDX to the image directory as a generalized base image", ToolCategory.Storage, ToolPriority.P2),

        // Cleanup
        new("vm_cleanup_orphans", "Find and destroy orphaned VMs", ToolCategory.Cleanup, ToolPriority.P1),

        // Configuration
        new("vm_configure", "Modify VM settings: CPU, memory, network", ToolCategory.Configuration, ToolPriority.P2),
    });

    /// <summary>
    /// Returns only P0 tools (Phase 1 — Core Foundation).
    /// </summary>
    public static IEnumerable<ToolDefinition> P0Tools =>
        AllTools.Where(t => t.Priority == ToolPriority.P0);

    /// <summary>
    /// Returns only tools that require hostId targeting (all except vm_echo and vm_diag, which have no hostId parameter).
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3.
    /// </summary>
    public static IEnumerable<ToolDefinition> HostScopedTools =>
        AllTools.Where(t => t.Name != "vm_echo" && t.Name != "vm_diag");
}

/// <summary>
/// Metadata for a single MCP tool in the catalog.
/// </summary>
public record ToolDefinition(
    string Name,
    string Description,
    ToolCategory Category,
    ToolPriority Priority);

public enum ToolCategory
{
    Health,
    Lifecycle,
    Discovery,
    Execution,
    FileTransfer,
    Checkpoints,
    Storage,
    Cleanup,
    Configuration
}

public enum ToolPriority
{
    P0,
    P1,
    P2
}
