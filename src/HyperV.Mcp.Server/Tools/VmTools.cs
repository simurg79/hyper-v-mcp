using System.ComponentModel;
using ModelContextProtocol.Server;
using HyperV.Mcp.Server.Infrastructure;

namespace HyperV.Mcp.Server.Tools;

/// <summary>
/// MCP SDK tool wrappers that bridge attribute-based tool discovery to the internal
/// <see cref="IToolDispatcher"/>. Each method is discovered by the MCP SDK via
/// <see cref="McpServerToolTypeAttribute"/> / <see cref="McpServerToolAttribute"/>
/// and delegates to <see cref="IToolDispatcher.DispatchAsync"/> for actual execution.
///
/// This class is the sole integration point between the MCP SDK's tool discovery
/// mechanism and the internal tool dispatch pipeline. All concurrency control,
/// argument validation, error mapping, and service delegation happen inside
/// ToolDispatcher — these wrappers are thin pass-through shims.
///
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D1: Attribute-based tool registration.
/// </summary>
[McpServerToolType]
public static class VmTools
{
    // ═══════════════════════════════════════════════════════════════════
    // Health
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_echo"), Description("Echo message back — health check")]
    public static async Task<string> VmEcho(
        IToolDispatcher dispatcher,
        string message,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync("vm_echo", ct, ("message", message));
    }

    [McpServerTool(Name = "vm_diag"), Description("Diagnostic tool — reports execution context, privileges, and environment")]
    public static async Task<string> VmDiag(
        IToolDispatcher dispatcher,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync("vm_diag", ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_create"), Description(
        "Create VM from VHDX — autoStart controls whether VM is started (default: false). " +
        "verifyBaseImageHash (default: true, Issue #169 / VC-D6 / ADR-4) enforces the base-VHDX " +
        "SHA-256 mutation guard: warm-on-init supplies the pre-hash and the post-create SHA-256 is " +
        "force-recomputed unconditionally; mismatch surfaces as BASE_IMAGE_MUTATED. Leave true unless " +
        "you have measured cache-warm SHA-256 cost dominating end-to-end latency on a known-trusted " +
        "storage root AND are willing to accept the §ADR-4 trade-off (preserved-stat mutations — " +
        "sparse in-place overwrite, touch -t mtime reset, silent storage bit-flip — go undetected). " +
        "Setting verifyBaseImageHash:false skips both the pre-hash lookup and the post-create recompute, " +
        "collapsing the guard to the ReadOnly-attribute check only. This is a per-call correctness-vs-cost " +
        "knob, NOT a transport-timeout knob. " +
        "Performance note. vm_create verifies the base VHDX with SHA-256 before and after the " +
        "differencing clone (see ADR-4 / ST-D6). On a cold OS page cache this is roughly 2 s/GB " +
        "per full-file pass (~60 s for each cold 30 GB read). The persisted sidecar .sha256 only " +
        "short-circuits the pre-hash lookup on stat-tuple match; vm_create still force-recomputes " +
        "the post-create hash. The default request timeout is 120 seconds; " +
        "override via the HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS environment variable (range 60–600) " +
        "if you regularly create from bases larger than ~50 GB on slow storage. You may also pass " +
        "verifyBaseImageHash: false for an individual call to skip the hash check entirely — this " +
        "accepts the documented ADR-4 trade-off (preserved-stat mutations are not detected for that call).")]
    public static async Task<string> VmCreate(
        IToolDispatcher dispatcher,
        string name,
        string? hostId = null,
        string? baseVhdxPath = null,
        int cpuCount = 2,
        long memoryMB = 4096,
        bool autoStart = false,
        bool verifyBaseImageHash = true,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_create",
            ct,
            ("name", name),
            ("hostId", hostId),
            ("baseVhdxPath", baseVhdxPath),
            ("cpuCount", cpuCount),
            ("memoryMB", memoryMB),
            ("autoStart", autoStart),
            ("verifyBaseImageHash", verifyBaseImageHash));
    }

    [McpServerTool(Name = "vm_start"), Description("Start a stopped VM")]
    public static async Task<string> VmStart(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_start",
            ct,
            ("vmId", vmId),
            ("hostId", hostId));
    }

    [McpServerTool(Name = "vm_stop"), Description("Stop a VM (graceful or force)")]
    public static async Task<string> VmStop(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        bool force = false,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_stop",
            ct,
            ("vmId", vmId),
            ("hostId", hostId),
            ("force", force));
    }

    [McpServerTool(Name = "vm_os_install"), Description("Install OS from ISO image — fully automated, single call. Windows-only: non-Windows ISOs (those without sources\\install.wim) are rejected with OS_NOT_SUPPORTED. Set skipPreflight=true to bypass Windows-11 CPU/RAM/disk floors (e.g., for Windows Server / Win10).")]
    public static async Task<string> VmOsInstall(
        IToolDispatcher dispatcher,
        string name,
        string isoPath,
        string adminPassword,
        string? hostId = null,
        int cpuCount = 4,
        long memoryMB = 8192,
        int diskSizeGB = 127,
        string? switchName = null,
        string locale = "en-US",
        string windowsEdition = "Windows 11 Pro",
        string? productKey = null,
        int timeoutMinutes = 60,
        bool skipPreflight = false,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_os_install",
            ct,
            ("name", name),
            ("isoPath", isoPath),
            ("adminPassword", adminPassword),
            ("hostId", hostId),
            ("cpuCount", cpuCount),
            ("memoryMB", memoryMB),
            ("diskSizeGB", diskSizeGB),
            ("switchName", switchName),
            ("locale", locale),
            ("windowsEdition", windowsEdition),
            ("productKey", productKey),
            ("timeoutMinutes", timeoutMinutes),
            ("skipPreflight", skipPreflight));
    }

    [McpServerTool(Name = "vm_restart"), Description("Restart a VM")]
    public static async Task<string> VmRestart(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_restart",
            ct,
            ("vmId", vmId),
            ("hostId", hostId));
    }

    [McpServerTool(Name = "vm_destroy"), Description("Stop, remove VM, and cleanup resources")]
    public static async Task<string> VmDestroy(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_destroy",
            ct,
            ("vmId", vmId),
            ("hostId", hostId));
    }

    [McpServerTool(Name = "vm_pause"), Description("Pause a running VM")]
    public static async Task<string> VmPause(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_pause",
            ct,
            ("vmId", vmId),
            ("hostId", hostId));
    }

    [McpServerTool(Name = "vm_resume"), Description("Resume a paused VM")]
    public static async Task<string> VmResume(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_resume",
            ct,
            ("vmId", vmId),
            ("hostId", hostId));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Discovery
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_list"), Description("List VMs with filtering")]
    public static async Task<string> VmList(
        IToolDispatcher dispatcher,
        string? hostId = null,
        string? nameFilter = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_list",
            ct,
            ("hostId", hostId),
            ("nameFilter", nameFilter));
    }

    [McpServerTool(Name = "vm_status"), Description("Get detailed VM status")]
    public static async Task<string> VmStatus(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_status",
            ct,
            ("vmId", vmId),
            ("hostId", hostId));
    }

    [McpServerTool(Name = "vm_wait_ready"), Description("Wait for VM readiness state")]
    public static async Task<string> VmWaitReady(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_wait_ready",
            ct,
            ("vmId", vmId),
            ("hostId", hostId),
            ("timeoutSeconds", timeoutSeconds));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Execution
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_run_command"), Description("Execute single command on guest VM")]
    public static async Task<string> VmRunCommand(
        IToolDispatcher dispatcher,
        string vmId,
        string command,
        string? hostId = null,
        string shell = "cmd",
        int timeoutSeconds = 30,
        string? username = null,
        string? password = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_run_command",
            ct,
            ("vmId", vmId),
            ("command", command),
            ("hostId", hostId),
            ("shell", shell),
            ("timeoutSeconds", timeoutSeconds),
            ("username", username),
            ("password", password));
    }

    [McpServerTool(Name = "vm_run_script"), Description("Execute multi-line script on guest VM")]
    public static async Task<string> VmRunScript(
        IToolDispatcher dispatcher,
        string vmId,
        string script,
        string? hostId = null,
        string shell = "powershell",
        int timeoutSeconds = 60,
        string? username = null,
        string? password = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_run_script",
            ct,
            ("vmId", vmId),
            ("script", script),
            ("hostId", hostId),
            ("shell", shell),
            ("timeoutSeconds", timeoutSeconds),
            ("username", username),
            ("password", password));
    }

    // ═══════════════════════════════════════════════════════════════════
    // File Transfer
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_copy_file"), Description("Copy file or directory from host to guest")]
    public static async Task<string> VmCopyFile(
        IToolDispatcher dispatcher,
        string vmId,
        string sourcePath,
        string destPath,
        string? hostId = null,
        bool isDirectory = false,
        string? username = null,
        string? password = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_copy_file",
            ct,
            ("vmId", vmId),
            ("sourcePath", sourcePath),
            ("destPath", destPath),
            ("hostId", hostId),
            ("isDirectory", isDirectory),
            ("username", username),
            ("password", password));
    }

    [McpServerTool(Name = "vm_get_file"), Description("Retrieve file from guest to host")]
    public static async Task<string> VmGetFile(
        IToolDispatcher dispatcher,
        string vmId,
        string sourcePath,
        string destPath,
        string? hostId = null,
        string? username = null,
        string? password = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_get_file",
            ct,
            ("vmId", vmId),
            ("sourcePath", sourcePath),
            ("destPath", destPath),
            ("hostId", hostId),
            ("username", username),
            ("password", password));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Checkpoints
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_checkpoint"), Description("Create, restore, list, or delete checkpoints")]
    public static async Task<string> VmCheckpoint(
        IToolDispatcher dispatcher,
        string vmId,
        string action,
        string? hostId = null,
        string? name = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_checkpoint",
            ct,
            ("vmId", vmId),
            ("action", action),
            ("hostId", hostId),
            ("name", name));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Storage
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_list_images"), Description("List available base VHDX images")]
    public static async Task<string> VmListImages(
        IToolDispatcher dispatcher,
        string? hostId = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_list_images",
            ct,
            ("hostId", hostId));
    }

    [McpServerTool(Name = "vm_create_base_image"), Description("Sysprep a Running VM, merge checkpoints, and copy the VHDX to the image directory as a generalized base image (Issue #51)")]
    public static async Task<string> VmCreateBaseImage(
        IToolDispatcher dispatcher,
        string vmName,
        string imageName,
        string? hostId = null,
        bool mergeCheckpoints = true,
        int shutdownTimeoutSeconds = 600,
        string? username = null,
        string? password = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_create_base_image",
            ct,
            ("vmName", vmName),
            ("imageName", imageName),
            ("hostId", hostId),
            ("mergeCheckpoints", mergeCheckpoints),
            ("shutdownTimeoutSeconds", shutdownTimeoutSeconds),
            ("username", username),
            ("password", password));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cleanup
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_cleanup_orphans"), Description("Find and destroy orphaned VMs")]
    public static async Task<string> VmCleanupOrphans(
        IToolDispatcher dispatcher,
        string? hostId = null,
        bool dryRun = true,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_cleanup_orphans",
            ct,
            ("hostId", hostId),
            ("dryRun", dryRun));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Configuration
    // ═══════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "vm_configure"), Description("Modify VM settings: CPU, memory, network")]
    public static async Task<string> VmConfigure(
        IToolDispatcher dispatcher,
        string vmId,
        string? hostId = null,
        int? cpuCount = null,
        long? memoryMB = null,
        CancellationToken ct = default)
    {
        return await dispatcher.DispatchAsync(
            "vm_configure",
            ct,
            ("vmId", vmId),
            ("hostId", hostId),
            ("cpuCount", cpuCount),
            ("memoryMB", memoryMB));
    }
}
