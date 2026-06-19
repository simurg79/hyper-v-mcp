using HyperV.Mcp.Server.Models;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// High-level VM lifecycle operations.
/// See /myplans/vm-management/vm-management-design.md — Interfaces: Provided.
/// </summary>
public interface IHyperVManager
{
    /// <summary>
    /// Create a new VM from a base VHDX. When <paramref name="autoStart"/> is true,
    /// the VM is started after creation; when false (default), it remains in Off state.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md.
    /// <para>
    /// <paramref name="verifyBaseImageHash"/> (Issue #169 / VC-D6, VC-D8, ADR-4):
    /// when <see langword="true"/> (default) the warm-on-init / ST-D6a SHA-256 cache
    /// supplies the pre-hash and the post-create SHA-256 is unconditionally
    /// force-recomputed via <see cref="IBaseImageHashCache.ForceRecomputeAsync"/>;
    /// any mismatch surfaces as <c>BASE_IMAGE_MUTATED</c>. When <see langword="false"/>
    /// both the pre-hash lookup AND the post-create recompute are skipped — the
    /// guard collapses to ReadOnly-attribute-only (the legacy
    /// <c>baseImageHashCache==null</c> code path). The opt-out is an
    /// operator-accepted ADR-4 trade-off documented on the <c>vm_create</c> tool
    /// description; it is NOT a transport-timeout knob.
    /// </para>
    /// </summary>
    Task<VmInfo> CreateVmAsync(string hostId, string name, string? baseVhdxPath,
        int cpuCount, long memoryMB, bool autoStart,
        bool verifyBaseImageHash, CancellationToken ct);

    /// <summary>
    /// Start a stopped VM.
    /// </summary>
    Task<VmInfo> StartVmAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// Stop a running VM. Force=true for hard power off.
    /// </summary>
    Task<VmInfo> StopVmAsync(string hostId, string vmId, bool force = false,
        CancellationToken ct = default);

    /// <summary>
    /// Destroy a VM: stop + remove + cleanup resources.
    /// </summary>
    Task DestroyVmAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// List VMs on a host with optional filtering.
    /// </summary>
    Task<IReadOnlyList<VmInfo>> ListVmsAsync(string hostId, string? nameFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get detailed status for a specific VM.
    /// </summary>
    Task<VmInfo> GetVmStatusAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// List available base VHDX images on a host.
    /// Returns an <see cref="ImageListResult"/> envelope per ST-D7: when no image
    /// directory source is configured, the call returns success with
    /// <c>Configured=false</c> and an empty list (not an error). When a configured
    /// path does not exist, throws <see cref="ArgumentException"/> (→ INVALID_PARAMETER).
    /// When the path exists but enumeration fails (ACL/IO), throws
    /// <see cref="IoOperationFailedException"/> (→ IO_ERROR).
    /// See /myplans/vm-management/storage/storage-design.md — ST-D7.
    /// </summary>
    Task<ImageListResult> ListImagesAsync(string hostId, CancellationToken ct = default);

    /// <summary>
    /// Restart a VM: stop + start as atomic operation.
    /// </summary>
    Task<VmInfo> RestartVmAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// Pause a running VM (Suspend-VM). VM must be in Running state.
    /// </summary>
    Task<VmInfo> PauseVmAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// Resume a paused/suspended VM (Resume-VM). VM must be in Paused state.
    /// </summary>
    Task<VmInfo> ResumeVmAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// Modify VM configuration (CPU count and/or startup memory). At least one of
    /// <paramref name="cpuCount"/> or <paramref name="memoryMB"/> must be provided.
    /// </summary>
    Task<VmInfo> ConfigureVmAsync(string hostId, string vmId, int? cpuCount, long? memoryMB, CancellationToken ct);

    /// <summary>
    /// Wait for a VM to reach a ready state (Running + heartbeat).
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Readiness Probes.
    /// </summary>
    Task<VmInfo> WaitForReadyAsync(string hostId, string vmId, int timeoutSeconds = 300, CancellationToken ct = default);

    /// <summary>
    /// Find and optionally destroy orphaned VMs (VMs tagged with hyper-v-mcp that have been
    /// running for more than 24 hours or are in a failed state).
    /// </summary>
    Task<IReadOnlyList<VmInfo>> CleanupOrphansAsync(string hostId, bool dryRun = true, CancellationToken ct = default);

    /// <summary>
    /// Install OS from ISO image — creates VM, installs OS via unattended setup, bootstraps to ready.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md.
    /// </summary>
    Task<OsInstallResult> OsInstallAsync(
        string hostId,
        string name,
        string isoPath,
        string adminPassword,
        int cpuCount = 4,
        long memoryMB = 8192,
        int diskSizeGB = 127,
        string? switchName = null,
        string locale = "en-US",
        string windowsEdition = "Windows 11 Pro",
        string? productKey = null,
        int timeoutMinutes = 60,
        bool skipPreflight = false,
        CancellationToken ct = default);

    /// <summary>
    /// Issue #51: Returns the host-side absolute path to the primary VHDX attached
    /// to the named VM via <c>Get-VMHardDiskDrive | Select-Object -First 1</c>.
    /// Throws <see cref="VmNotFoundException"/> when the VM does not exist;
    /// throws <see cref="InvalidOperationException"/> when the VM has no attached
    /// VHDX. Used by <c>vm_create_base_image</c> to locate the disk to copy.
    /// </summary>
    Task<string> GetPrimaryVhdxPathAsync(string hostId, string vmName, CancellationToken ct = default);
}
