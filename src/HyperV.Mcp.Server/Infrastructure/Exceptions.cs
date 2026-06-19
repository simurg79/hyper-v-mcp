namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Thrown when a VM is not found on the specified host.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_NOT_FOUND.
/// </summary>
public class VmNotFoundException : Exception
{
    public string VmId { get; }
    public string HostId { get; }

    public VmNotFoundException(string hostId, string vmId)
        : base($"No VM with ID '{vmId}' exists on host '{hostId}'")
    {
        HostId = hostId;
        VmId = vmId;
    }
}

/// <summary>
/// Thrown when a command times out during execution.
/// See /myplans/execution/commands/commands-design.md — CMD-D4: Timeout returns success:false with partial output.
/// </summary>
public class CommandTimeoutException : Exception
{
    public string? PartialStdout { get; }
    public string? PartialStderr { get; }
    public long DurationMs { get; }

    public CommandTimeoutException(string message, string? partialStdout = null,
        string? partialStderr = null, long durationMs = 0)
        : base(message)
    {
        PartialStdout = partialStdout;
        PartialStderr = partialStderr;
        DurationMs = durationMs;
    }
}

/// <summary>
/// Thrown when a VM already exists with the given name.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_ALREADY_EXISTS.
/// Issue #203 / VC-DUP-D5 / Constraint #6: the message format is contractually pinned
/// to <c>"A VM with the name '{name}' already exists on host '{hostId}'."</c> so
/// smoke tests can assert string equality and PowerShell <c>throw</c> text never
/// leaks onto the wire.
/// </summary>
public class VmAlreadyExistsException : Exception
{
    public string VmName { get; }
    public string HostId { get; }

    public VmAlreadyExistsException(string hostId, string vmName)
        : base($"A VM with the name '{vmName}' already exists on host '{hostId}'.")
    {
        HostId = hostId;
        VmName = vmName;
    }

    public VmAlreadyExistsException(string hostId, string vmName, Exception innerException)
        : base($"A VM with the name '{vmName}' already exists on host '{hostId}'.", innerException)
    {
        HostId = hostId;
        VmName = vmName;
    }
}

/// <summary>
/// Thrown when a checkpoint operation (create, restore, remove) fails.
/// Maps to CHECKPOINT_FAILED error code in the MCP error taxonomy.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: CHECKPOINT_FAILED.
/// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
///
/// This is a dedicated exception type that ensures checkpoint failures map to
/// CHECKPOINT_FAILED (not COMMAND_FAILED from InvalidOperationException).
/// </summary>
public class CheckpointFailedException : InvalidOperationException
{
    public string VmId { get; }
    public string HostId { get; }
    public string? CheckpointName { get; }

    public CheckpointFailedException(string hostId, string vmId, string message, string? checkpointName = null)
        : base(message)
    {
        HostId = hostId;
        VmId = vmId;
        CheckpointName = checkpointName;
    }

    public CheckpointFailedException(string hostId, string vmId, string message, Exception innerException, string? checkpointName = null)
        : base(message, innerException)
    {
        HostId = hostId;
        VmId = vmId;
        CheckpointName = checkpointName;
    }
}

/// <summary>
/// Thrown when an ISO file is not found during OS installation.
/// Maps to ISO_NOT_FOUND error code in the MCP error taxonomy.
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — Error Handling.
/// </summary>
public class IsoNotFoundException : InvalidOperationException
{
    public string IsoPath { get; }

    public IsoNotFoundException(string isoPath)
        : base($"ISO file not found: {isoPath}")
    {
        IsoPath = isoPath;
    }
}

/// <summary>
/// Thrown when OS installation times out waiting for the guest to become ready.
/// Maps to INSTALL_TIMEOUT error code in the MCP error taxonomy.
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — Error Handling.
/// </summary>
public class InstallTimeoutException : InvalidOperationException
{
    public int TimeoutMinutes { get; }
    public string LastPhase { get; }
    public string? VmId { get; }
    public string? VmName { get; }

    public InstallTimeoutException(string message, int timeoutMinutes, string lastPhase, string? vmId = null, string? vmName = null)
        : base(message)
    {
        TimeoutMinutes = timeoutMinutes;
        LastPhase = lastPhase;
        VmId = vmId;
        VmName = vmName;
    }
}

/// <summary>
/// Thrown when OS installation fails (general installation failure).
/// Maps to INSTALL_FAILED error code in the MCP error taxonomy.
/// Preserves the VM — caller should NOT roll back after installation has started.
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — Rollback Policy.
/// </summary>
public class InstallFailedException : InvalidOperationException
{
    public string? VmId { get; }
    public string? VmName { get; }

    public InstallFailedException(string message, string? vmId = null, string? vmName = null)
        : base(message)
    {
        VmId = vmId;
        VmName = vmName;
    }

    public InstallFailedException(string message, Exception innerException, string? vmId = null, string? vmName = null)
        : base(message, innerException)
    {
        VmId = vmId;
        VmName = vmName;
    }
}

/// <summary>
/// Thrown when the autounattend ISO creation or processing fails.
/// Maps to AUTOUNATTEND_FAILED error code in the MCP error taxonomy.
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — Error Handling.
/// </summary>
public class AutounattendFailedException : InvalidOperationException
{
    public AutounattendFailedException(string message)
        : base(message) { }

    public AutounattendFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when an ISO supplied to <c>vm_os_install</c> is not a Windows installer
/// (does not contain <c>sources\install.wim</c>). Maps to
/// <c>OS_NOT_SUPPORTED</c> in the MCP error taxonomy.
/// Issue #97; ISO-D16 (always enforced; not bypassable by <c>skipPreflight</c>).
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D16.
/// </summary>
public class OsNotSupportedException : InvalidOperationException
{
    public string IsoPath { get; }

    public OsNotSupportedException(string isoPath)
        : base("vm_os_install currently supports Windows ISOs only (sources\\install.wim required). See ISO-D16.")
    {
        IsoPath = isoPath;
    }

    public OsNotSupportedException(string isoPath, string message)
        : base(message)
    {
        IsoPath = isoPath;
    }
}

/// <summary>
/// Thrown when <c>vm_os_install</c>'s C#-side resource-floor preflight fails:
/// <c>cpuCount &lt; 2</c>, <c>memoryMB &lt; 4096</c>, or <c>diskSizeGB &lt; 64</c>.
/// Maps to <c>INSUFFICIENT_RESOURCES</c> in the MCP error taxonomy. The
/// <see cref="FailedFloor"/>, <see cref="Minimum"/>, and <see cref="Actual"/>
/// values are surfaced via the response envelope's <c>data</c> field so
/// callers can react programmatically.
/// Issue #97; ISO-D17 (bypassable via <c>skipPreflight=true</c>).
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D17.
/// </summary>
public class InsufficientResourcesException : InvalidOperationException
{
    /// <summary>Name of the violated floor: <c>cpuCount</c>, <c>memoryMB</c>, or <c>diskSizeGB</c>.</summary>
    public string FailedFloor { get; }

    /// <summary>The minimum required value for the violated floor.</summary>
    public long Minimum { get; }

    /// <summary>The caller-supplied value that was below the minimum.</summary>
    public long Actual { get; }

    public InsufficientResourcesException(string failedFloor, long minimum, long actual, string message)
        : base(message)
    {
        FailedFloor = failedFloor;
        Minimum = minimum;
        Actual = actual;
    }
}

/// <summary>
/// Thrown when a VM is not in the Running state and a command/script/file-transfer is attempted.
/// Maps to VM_NOT_RUNNING error code in the MCP error taxonomy.
/// See GitHub Issue #21.
/// </summary>
public class VmNotRunningException : Exception
{
    public string VmId { get; }
    public string HostId { get; }
    public string ActualState { get; }

    public VmNotRunningException(string hostId, string vmId, string actualState)
        : base($"VM '{vmId}' on host '{hostId}' is not running (state: {actualState}). VM must be Running to execute commands.")
    {
        HostId = hostId;
        VmId = vmId;
        ActualState = actualState;
    }
}

/// <summary>
/// Thrown when VM credentials cannot be resolved from tool parameters or environment variables.
/// Maps to MISSING_CREDENTIALS error code in the MCP error taxonomy.
/// See /myplans/security/credentials/credentials-design.md — Phase 1: Minimal Credential Resolution.
/// See GitHub Issue #20.
/// </summary>
public class MissingCredentialsException : Exception
{
    public MissingCredentialsException()
        : base("No credentials provided. Supply username/password as tool parameters or set HYPERV_MCP_VM_USERNAME and HYPERV_MCP_VM_PASSWORD environment variables.") { }
}

/// <summary>
/// Thrown when a configured filesystem path exists but cannot be read, enumerated,
/// or written due to permissions, locking, or other I/O failure. Maps to
/// <c>IO_ERROR</c> in the MCP error taxonomy. Distinct from
/// <see cref="FileNotFoundException"/> (specific source artifact missing) and
/// from validation/missing-config failures (which use <c>INVALID_PARAMETER</c>).
/// See /myplans/vm-management/storage/storage-design.md — ST-D7.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: IO_ERROR.
/// </summary>
public class IoOperationFailedException : Exception
{
    public string Path { get; }

    public IoOperationFailedException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    public IoOperationFailedException(string path, string message, Exception innerException)
        : base(message, innerException)
    {
        Path = path;
    }
}

/// <summary>
/// Issue #51 / CP-D6: Thrown by <see cref="ICheckpointManager.MergeAllAsync"/> when the
/// checkpoint tree topology is not a linear chain (e.g. branched trees, multiple
/// children). Maps to <c>MERGE_NOT_SUPPORTED</c> in the MCP error taxonomy.
/// Distinct from <see cref="CheckpointMergeFailedException"/> which represents a
/// runtime failure of an attempted merge.
/// </summary>
public class MergeNotSupportedException : InvalidOperationException
{
    public string VmId { get; }
    public string HostId { get; }

    public MergeNotSupportedException(string hostId, string vmId, string message)
        : base(message)
    {
        HostId = hostId;
        VmId = vmId;
    }
}

/// <summary>
/// Issue #51 / CP-D6: Thrown by <see cref="ICheckpointManager.MergeAllAsync"/> when a
/// linear-chain merge was attempted but the underlying Hyper-V merge job failed
/// (I/O error, locked VHDX, transient Hyper-V failure, etc.). Maps to
/// <c>CHECKPOINT_MERGE_FAILED</c> in the MCP error taxonomy. Distinct from
/// <see cref="CheckpointFailedException"/> which covers create/restore/list/delete
/// failures, and from <see cref="MergeNotSupportedException"/> which is a topology
/// pre-condition rejection.
/// </summary>
public class CheckpointMergeFailedException : InvalidOperationException
{
    public string VmId { get; }
    public string HostId { get; }

    public CheckpointMergeFailedException(string hostId, string vmId, string message)
        : base(message)
    {
        HostId = hostId;
        VmId = vmId;
    }

    public CheckpointMergeFailedException(string hostId, string vmId, string message, Exception innerException)
        : base(message, innerException)
    {
        HostId = hostId;
        VmId = vmId;
    }
}

/// <summary>
/// Issue #51: Thrown by <c>vm_create_base_image</c> when the in-guest
/// <c>sysprep /generalize /oobe /shutdown</c> step did not succeed: the in-guest
/// invocation failed, or the VM failed to reach <c>Off</c> within
/// <c>shutdownTimeoutSeconds</c>. Maps to <c>SYSPREP_FAILED</c>.
/// </summary>
public class SysprepFailedException : InvalidOperationException
{
    public string VmId { get; }
    public string HostId { get; }

    public SysprepFailedException(string hostId, string vmId, string message)
        : base(message)
    {
        HostId = hostId;
        VmId = vmId;
    }

    public SysprepFailedException(string hostId, string vmId, string message, Exception innerException)
        : base(message, innerException)
    {
        HostId = hostId;
        VmId = vmId;
    }
}

/// <summary>
/// Issue #51: Thrown by <c>vm_create_base_image</c> when the host-side
/// <see cref="System.IO.File.Copy(string, string, bool)"/> of the VM's primary VHDX
/// into the configured image directory fails. Causes: <c>ServerOptions.ImageDirectory</c>
/// not configured, destination file already exists, source missing, or IO/permission error.
/// Maps to <c>IMAGE_COPY_FAILED</c>.
/// </summary>
public class ImageCopyFailedException : InvalidOperationException
{
    /// <summary>Source path on the host (may be <c>null</c> when source could not be resolved).</summary>
    public string? SourcePath { get; }
    /// <summary>Destination path on the host (may be <c>null</c> when destination could not be resolved).</summary>
    public string? DestinationPath { get; }

    public ImageCopyFailedException(string message, string? sourcePath = null, string? destinationPath = null)
        : base(message)
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
    }

    public ImageCopyFailedException(string message, Exception innerException,
        string? sourcePath = null, string? destinationPath = null)
        : base(message, innerException)
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
    }
}

/// <summary>
/// Issue #164 / LF-D17: thrown by <c>HyperVManager.CreateVmAsync</c> when the primary
/// create pipeline fails (or the inbound CT is cancelled) AND the detached-CTS
/// rollback has been awaited. Carries the structured details block that LF-D17
/// requires in the MCP error envelope:
/// <c>{ vmName, phase, rollback: { performed, succeeded, elapsedMs, residualArtifacts } }</c>.
///
/// <para>The mapped error code is decided by <see cref="ErrorCode"/> rather than by
/// exception type:</para>
/// <list type="bullet">
/// <item><c>OPERATION_CANCELED</c> — inbound CT was signalled.</item>
/// <item><c>COMMAND_TIMEOUT</c> — PowerShell child timed out (TimedOut=true).</item>
/// <item><c>COMMAND_FAILED</c> — any other create-pipeline failure.</item>
/// </list>
///
/// See /myplans/vm-management/lifecycle/lifecycle-design.md — LF-D17.
/// </summary>
public class VmCreateRollbackException : Exception
{
    /// <summary>VM name that the failed <c>vm_create</c> targeted.</summary>
    public string VmName { get; }

    /// <summary>Final error code the envelope should carry.</summary>
    public string ErrorCode { get; }

    /// <summary>Last successful create-pipeline phase before failure (LF-D17 enum).</summary>
    public string Phase { get; }

    /// <summary>Structured rollback result. Always non-null — rollback is always attempted.</summary>
    public VmCreateRollbackInfo Rollback { get; }

    public VmCreateRollbackException(
        string vmName,
        string errorCode,
        string phase,
        VmCreateRollbackInfo rollback,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        VmName = vmName;
        ErrorCode = errorCode;
        Phase = phase;
        Rollback = rollback;
    }
}

/// <summary>
/// Outcome of the LF-D17 cancellation-safe rollback. Serialized into
/// <c>McpToolResponse.Details.rollback</c>.
/// AC#2 ("no orphan VHDX") is satisfied iff
/// <see cref="ResidualArtifacts"/> is empty.
/// </summary>
public class VmCreateRollbackInfo
{
    public bool Performed { get; init; }
    public bool Succeeded { get; init; }
    public long ElapsedMs { get; init; }
    public IReadOnlyList<string> ResidualArtifacts { get; init; } = Array.Empty<string>();
}

/// Issue #204 / VC-DEST-D2 / VC-DEST-D3: Thrown by
/// <see cref="FileTransferService.CopyFileToGuestAsync"/> when the guest-side
/// ensure-parent step (auto-create of the destination's parent directory) fails
/// — e.g. ACL denial, read-only volume, invalid drive, or quota exhaustion.
/// Maps to <c>DEST_DIR_MISSING</c> ("missing or not creatable") in the MCP error
/// taxonomy via <see cref="ErrorMapper.MapException"/> type-match (not substring),
/// so callers can distinguish a missing/uncreatable destination parent from a
/// missing source file (which remains <c>FILE_NOT_FOUND</c>, Issue #38 contract).
/// See /myplans/execution/file-transfer/vm-copy-file-dest-dir-design.md — VC-DEST-D1..D8.
/// </summary>
internal sealed class DestinationDirectoryUnavailableException : Exception
{
    /// <summary>The caller-supplied destination path (verbatim, not the parent).</summary>
    public string DestinationPath { get; }

    public DestinationDirectoryUnavailableException(string destPath, string message, Exception? inner = null)
        : base(message, inner)
    {
        DestinationPath = destPath;
    }
}

/// <summary>
/// Issue #209 (sub-finding) / VC-SO-D2: Thrown by <see cref="SessionStore"/> when
/// <c>New-PSSession</c> fails to open a PowerShell Direct session against a guest
/// VM (notably Linux guests where the hypervisor socket negotiation fails with
/// <c>PSSessionOpenFailed</c> / <c>vmhypervsocketclient</c> errors).
///
/// <para>Derives from <see cref="InvalidOperationException"/> (not
/// <see cref="Exception"/>) to preserve backward compatibility with existing
/// <c>SessionStoreTests.GetOrCreateAsync_NewPSSessionThrows_*</c> and
/// <c>GetOrCreateAsync_EmptyExceptionMessage_*</c> tests which assert
/// <c>InvalidOperationException</c> via <c>Assert.ThrowsAsync</c> /
/// <c>Should().ThrowAsync</c>. C5 backward-compat lock.</para>
///
/// <para>Maps to <c>SESSION_FAILED</c> via the typed arm in
/// <see cref="ErrorMapper"/> (VC-SO-D3) placed ABOVE the path-not-found
/// substring arm to prevent the prior misclassification as
/// <c>FILE_NOT_FOUND</c> on <c>vm_copy_file</c> Linux failures.</para>
///
/// See /myplans/remoting/session-management/psdirect-linux-session-open-classification-design.md.
/// </summary>
public class SessionOpenFailedException : InvalidOperationException
{
    public string SessionName { get; }
    public string VmId { get; }

    public SessionOpenFailedException(string sessionName, string vmId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        SessionName = sessionName;
        VmId = vmId;
    }
}
