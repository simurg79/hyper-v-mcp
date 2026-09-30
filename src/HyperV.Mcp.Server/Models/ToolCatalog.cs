namespace HyperV.Mcp.Server.Models;

/// <summary>Authoritative tool-registration catalog; tests require every entry to be discoverable.</summary>
public static class ToolCatalog
{
    public const string WaitReadyDescription =
        "Wait for guest-login readiness on supported Windows or Linux guests: freshly authenticate this call's " +
        "resolved identity and positively complete a read-only guest operation. Running state and heartbeat alone " +
        "never establish readiness. Supply username/password or configure HYPERV_MCP_VM_USERNAME and " +
        "HYPERV_MCP_VM_PASSWORD; request values take precedence per field. No usable pair returns MISSING_CREDENTIALS. " +
        "timeoutSeconds must be positive (default 300; one second is accepted); zero is INVALID_PARAMETER. " +
        "One elapsed wait budget starts before concurrency waits and gates each new functional step and retry. " +
        "It is not a hard whole-call deadline: host preparation, queued access, an in-flight operation and cleanup " +
        "can overrun it. Expiry does not cancel an in-flight step; a final confirmation begun in budget may succeed " +
        "after expiry. Required cleanup always runs; explicit caller cancellation prevents success. " +
        "READINESS_NOT_REACHED means guest-login readiness could not be determined: budget exhaustion, " +
        "credential rejection observed without later confirmation, or observation failure, distinguished in the message " +
        "with the VM, last safe observation and requested/effective budgets. None proves the guest will never be ready. " +
        "On rejection, verify credentials for the image or wait and retry after further boot progress; the cause is ambiguous. " +
        "On observation failure, correct an identified access problem before retrying. Specific host, VM, guest-routing " +
        "and concurrency refusals retain their meaning; an undetermined guest is never assumed to support a transport. " +
        "Success retains the VM-information shape and confirms access only at that point in this call: no guarantee " +
        "for another account, desktop login, application or installation readiness, network availability, privileged " +
        "commands or the next guest operation. No session is returned or reserved. The wait changes no VM configuration, " +
        "power state, accounts or guest files. Ordinary command/transfer retry behavior is unchanged. " +
        "Even valid credentials may require materially longer than the previous heartbeat-only wait and may consume " +
        "the whole budget; no minimum boot delay is implied. Use vm_status for power state only.";

    /// <summary>Category-ordered definitions use read-only array backing to prevent casts to a mutable List<T>.</summary>
    public static readonly IReadOnlyList<ToolDefinition> AllTools = Array.AsReadOnly(new ToolDefinition[]
    {
        new("vm_echo", "Echo message back — health check", ToolCategory.Health, ToolPriority.P0),
        new("vm_diag", "Diagnostic tool — reports execution context, privileges, and environment", ToolCategory.Health, ToolPriority.P0),

        new("vm_create",
            "Create VM from VHDX — autoStart (default: false) controls whether VM is started after creation. " +
            "verifyBaseImageHash (default: true) enforces ST-D6 base-VHDX SHA-256 mutation guard " +
            "(force-recomputed post-create); set to false to skip both pre-hash lookup and post-recompute " +
            "(operator-accepted ADR-4 trade-off; preserved-stat mutations go undetected). See Issue #169 / VC-D6 / VC-D8. " +
            "Performance note. vm_create verifies the base VHDX with SHA-256 before and after the differencing clone " +
            "(see ADR-4 / ST-D6). On a cold OS page cache this is roughly 2 s/GB per full-file pass " +
            "(~60 s for each cold 30 GB read). The persisted sidecar .sha256 only short-circuits the pre-hash lookup " +
            "on stat-tuple match; vm_create still force-recomputes the post-create hash. The default request timeout " +
            "is 120 seconds; override via the HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS environment variable (range 60–600) " +
            "if you regularly create from bases larger than ~50 GB on slow storage. You may also pass " +
            "verifyBaseImageHash: false for an individual call to skip the hash check entirely — this accepts the " +
            "documented ADR-4 trade-off (preserved-stat mutations are not detected for that call). " +
            "adminPassword (optional, Issue #283) configures the new VM's built-in local Administrator account to " +
            "accept that password and does not return success until the VM is login-ready, so the next " +
            "vm_run_command succeeds first try; it requires a generalized base image (otherwise BASE_NOT_GENERALIZED, " +
            "no VM created), always starts the VM regardless of autoStart, and yields READINESS_NOT_REACHED with the " +
            "VM preserved and named if readiness is never confirmed.",
            ToolCategory.Lifecycle, ToolPriority.P0),
        new("vm_start", "Start a stopped VM", ToolCategory.Lifecycle, ToolPriority.P0),
        new("vm_stop", "Stop a VM (graceful or force)", ToolCategory.Lifecycle, ToolPriority.P0),
        new("vm_restart", "Restart a VM", ToolCategory.Lifecycle, ToolPriority.P1),
        new("vm_os_install", "Install OS from ISO image — fully automated, single call", ToolCategory.Lifecycle, ToolPriority.P1),
        new("vm_destroy", "Stop, remove VM, and cleanup resources", ToolCategory.Lifecycle, ToolPriority.P0),
        new("vm_pause", "Pause a running VM", ToolCategory.Lifecycle, ToolPriority.P2),
        new("vm_resume", "Resume a paused VM", ToolCategory.Lifecycle, ToolPriority.P2),

        new("vm_list", "List VMs with filtering", ToolCategory.Discovery, ToolPriority.P0),
        new("vm_find_by_name", "Find all VMs by exact name, including untagged VMs", ToolCategory.Discovery, ToolPriority.P1),
        new("vm_status", "Get detailed VM status", ToolCategory.Discovery, ToolPriority.P0),
        new("vm_wait_ready", WaitReadyDescription, ToolCategory.Discovery, ToolPriority.P1),

        new("vm_run_command", "Execute single command on guest VM", ToolCategory.Execution, ToolPriority.P0),
        new("vm_run_script", "Execute multi-line script on guest VM", ToolCategory.Execution, ToolPriority.P1),

        new("vm_copy_file", "Copy file or directory from host to guest", ToolCategory.FileTransfer, ToolPriority.P0),
        new("vm_get_file", "Retrieve file from guest to host", ToolCategory.FileTransfer, ToolPriority.P1),

        new("vm_checkpoint", "Create, restore, list, or delete checkpoints", ToolCategory.Checkpoints, ToolPriority.P1),

        new("vm_list_images", "List available base VHDX images", ToolCategory.Storage, ToolPriority.P1),
        new("vm_create_base_image", "Sysprep a Running VM, merge checkpoints, and copy the VHDX to the image directory as a generalized base image", ToolCategory.Storage, ToolPriority.P2),

        new("vm_cleanup_orphans", "Find and destroy orphaned VMs", ToolCategory.Cleanup, ToolPriority.P1),

        new("vm_configure", "Modify VM settings: CPU, memory, network", ToolCategory.Configuration, ToolPriority.P2),
    });

    public static IEnumerable<ToolDefinition> P0Tools =>
        AllTools.Where(t => t.Priority == ToolPriority.P0);

    public static IEnumerable<ToolDefinition> HostScopedTools =>
        AllTools.Where(t => t.Name != "vm_echo" && t.Name != "vm_diag");
}

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
