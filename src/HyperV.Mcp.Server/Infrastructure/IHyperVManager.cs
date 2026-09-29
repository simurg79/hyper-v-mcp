using HyperV.Mcp.Server.Models;

namespace HyperV.Mcp.Server.Infrastructure;

public interface IHyperVManager
{
    /// <summary>Creates a VM; autoStart=true starts it, otherwise it remains Off (default).
    /// verifyBaseImageHash=true uses the warm pre-hash and unconditionally force-recomputes the post-hash; mismatches yield BASE_IMAGE_MUTATED.
    /// False skips both hashes, leaving the legacy null-cache ReadOnly-only guard: an operator-accepted correctness trade-off, not a transport-timeout knob.</summary>
    Task<VmInfo> CreateVmAsync(string hostId, string name, string? baseVhdxPath,
        int cpuCount, long memoryMB, bool autoStart,
        bool verifyBaseImageHash, CancellationToken ct);

    /// <summary>Null adminPassword uses the no-password overload; empty/whitespace values fail with INVALID_PARAMETER.
    /// A supplied password requires a generalized image, forces startup and returns success only when the guest accepts it;
    /// readiness uses createTimeBudgetSeconds with a 150s floor. Separate arity preserves Moq expression-tree setups, which cannot bind optional arguments.</summary>
    Task<VmInfo> CreateVmAsync(string hostId, string name, string? baseVhdxPath,
        int cpuCount, long memoryMB, bool autoStart,
        bool verifyBaseImageHash, string? adminPassword, int createTimeBudgetSeconds,
        CancellationToken ct);

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

    Task<IReadOnlyList<VmInfo>> ListVmsAsync(string hostId, string? nameFilter = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<VmInfo>> FindVmsByNameAsync(string hostId, string name, bool caseSensitive,
        CancellationToken ct = default);

    Task<VmInfo> GetVmStatusAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>Lists base VHDXs; no configured directory returns Configured=false and an empty list, not an error.
    /// A missing configured path throws ArgumentException (INVALID_PARAMETER); ACL/IO enumeration failures throw IoOperationFailedException (IO_ERROR).</summary>
    Task<ImageListResult> ListImagesAsync(string hostId, CancellationToken ct = default);

    /// <summary>
    /// Restart a VM: stop + start as atomic operation.
    /// </summary>
    Task<VmInfo> RestartVmAsync(string hostId, string vmId, CancellationToken ct = default);

    /// <summary>
    /// Pause a running VM into in-memory <c>Paused</c>. VM must be Running. Success is reported
    /// only when the VM settles into <c>Paused</c>; <c>Saved</c>/<c>Off</c> or a timeout is a
    /// caller-facing failure.
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

    Task<VmInfo> WaitForReadyAsync(string hostId, string vmId, int timeoutSeconds = 300, CancellationToken ct = default);

    Task<VmInfo> WaitForReadyAsync(string hostId, string vmId, ReadinessBudget budget,
        string? username = null, string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Find and optionally destroy MCP-owned ephemeral VMs whose recorded creation time is older
    /// than the 24h cutoff. Classification uses ownership, the ephemeral marker, and age only
    /// never power state (#93).
    /// </summary>
    Task<IReadOnlyList<VmInfo>> CleanupOrphansAsync(string hostId, bool dryRun = true, CancellationToken ct = default);

    /// <summary>
    /// Install OS from ISO image — creates VM, installs OS via unattended setup, bootstraps to ready.
    /// Supports Windows and Ubuntu Server 24.04. <paramref name="guestUsername"/>
    /// is the initial Ubuntu login user (default <c>ubuntu</c>) and is ignored for the Windows target.
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
        string? guestUsername = null,
        CancellationToken ct = default);

    /// <summary> Returns the host-side absolute path to the primary VHDX attached to the named VM via
    /// <c>Get-VMHardDiskDrive | Select-Object -First 1</c>. Throws <see cref="VmNotFoundException"/> when the VM does
    /// not exist; throws <see cref="InvalidOperationException"/> when the VM has no attached VHDX. Used by
    /// <c>vm_create_base_image</c> to locate the disk to copy. </summary>
    Task<string> GetPrimaryVhdxPathAsync(string hostId, string vmName, CancellationToken ct = default);
}
