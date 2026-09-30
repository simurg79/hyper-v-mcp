namespace HyperV.Mcp.Server.Infrastructure;

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

/// <summary>Command timeout returns success:false with partial output.</summary>
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

/// <summary>VM_ALREADY_EXISTS keeps the exact constructor message for smoke-test equality
/// and never exposes raw PowerShell throw text.</summary>
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

/// <summary>Maps checkpoint create/restore/remove failures to CHECKPOINT_FAILED, not generic COMMAND_FAILED.</summary>
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
/// </summary>
public class AutounattendFailedException : InvalidOperationException
{
    public AutounattendFailedException(string message)
        : base(message) { }

    public AutounattendFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>ISO lacks sources\install.wim; maps to OS_NOT_SUPPORTED.
/// The media check is mandatory even with skipPreflight.</summary>
public class OsNotSupportedException : InvalidOperationException
{
    public string IsoPath { get; }

    // Fixed text names both supported targets without exposing internal tokens, caller paths or secrets; OS_NOT_SUPPORTED stays unchanged.
    public OsNotSupportedException(string isoPath)
        : base("Unsupported installation media. vm_os_install supports Windows (ISO with " +
               "sources\\install.wim) and Ubuntu Server 24.04 (ISO with a casper/ live-installer " +
               "layout). The supplied ISO matched neither.")
    {
        IsoPath = isoPath;
    }

    public OsNotSupportedException(string isoPath, string message)
        : base(message)
    {
        IsoPath = isoPath;
    }
}

/// <summary> Thrown when an Ubuntu Server 24.04 install does not publish its completion KVP
/// (<c>hyperv-mcp/os-install</c>) within <c>timeoutMinutes</c>. Maps to <c>LINUX_INSTALL_TIMEOUT</c>; preserves
/// the VM for inspection. Linux analogue of <see cref="InstallTimeoutException"/>, kept distinct so the Windows
/// timeout code is unchanged. </summary>
public class LinuxInstallTimeoutException : InvalidOperationException
{
    public int TimeoutMinutes { get; }
    public string? VmId { get; }
    public string? VmName { get; }

    /// <summary>Observed primary-DVD media distinguishes wrong media from missing completion signal.
    /// Null if unreadable; never substitute the requested path, which would assume the fact being investigated.</summary>
    public string? AttachedIsoPath { get; }

    /// <summary>
    /// Observed guest completion channel state; Undetermined is reported, not hidden.
    /// .
    /// </summary>
    public GuestCompletionChannelState ChannelState { get; }

    public LinuxInstallTimeoutException(
        string message,
        int timeoutMinutes,
        string? vmId = null,
        string? vmName = null,
        string? attachedIsoPath = null,
        GuestCompletionChannelState channelState = GuestCompletionChannelState.Undetermined)
        : base(message)
    {
        TimeoutMinutes = timeoutMinutes;
        VmId = vmId;
        VmName = vmName;
        AttachedIsoPath = attachedIsoPath;
        ChannelState = channelState;
    }
}

/// <summary> Thrown when Ubuntu cloud-init autoinstall reports a terminal provisioning failure (a <c>failed:*</c>
/// KVP) or an observable error before the ready signal. Maps to <c>LINUX_PROVISION_FAILED</c> and preserves the
/// VM for inspection. Linux analogue of <see cref="InstallFailedException"/>. </summary>
public class LinuxProvisionFailedException : InvalidOperationException
{
    public string? VmId { get; }
    public string? VmName { get; }

    /// <summary>Required stable step identifier (e.g. seed-media-build or vm-create) prevents step-less LINUX_PROVISION_FAILED errors.</summary>
    public string FailingStep { get; }

    /// <summary>Sanitized, capped underlying cause text; null when the step reported none.</summary>
    public string? Cause { get; }

    /// <summary>True when <see cref="Cause"/> was capped, so truncation is disclosed not silent.</summary>
    public bool CauseTruncated { get; }

    /// <summary>
    /// Which tool authored the media, when the failing step is media authoring. Host-side
    /// diagnostic only — it MUST NOT be projected into the caller envelope.
    /// </summary>
    public string? AuthoringRoute { get; }

    public LinuxProvisionFailedException(
        string message,
        string failingStep,
        string? vmId = null,
        string? vmName = null,
        string? cause = null,
        bool causeTruncated = false,
        string? authoringRoute = null)
        : base(message)
    {

        if (string.IsNullOrWhiteSpace(failingStep))
            throw new ArgumentException("A failing step is required.", nameof(failingStep));

        FailingStep = failingStep;
        VmId = vmId;
        VmName = vmName;
        Cause = cause;
        CauseTruncated = causeTruncated;
        AuthoringRoute = authoringRoute;
    }
}

/// <summary> Thrown when the Ubuntu Generation-2 / Secure-Boot-off precondition is not met because the caller
/// explicitly requested an incompatible firmware/generation. Maps to <c>LINUX_PRECONDITION_UNMET</c>. Synchronous
/// fail-fast before any VM or job — same family as <see cref="OsNotSupportedException"/>. Fixed message, no
/// caller paths or secrets. </summary>
public class LinuxPreconditionUnmetException : InvalidOperationException
{
    public LinuxPreconditionUnmetException(string message)
        : base(message) { }
}

/// <summary>Resource floors are cpuCount < 2, memoryMB < 4096 or diskSizeGB < 64; skipPreflight=true bypasses them.
/// Maps to INSUFFICIENT_RESOURCES, with FailedFloor/Minimum/Actual in response data so callers can adjust requests.</summary>
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
/// See GitHub.
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
/// See GitHub.
/// </summary>
public class MissingCredentialsException : Exception
{
    public MissingCredentialsException()
        : base("No credentials provided. Supply username/password as tool parameters or set HYPERV_MCP_VM_USERNAME and HYPERV_MCP_VM_PASSWORD environment variables.") { }
}

/// <summary> Thrown when a configured filesystem path exists but cannot be read, enumerated, or written due to
/// permissions, locking, or other I/O failure. Maps to <c>IO_ERROR</c> in the MCP error taxonomy. Distinct from
/// <see cref="FileNotFoundException"/> (specific source artifact missing) and from validation/missing-config
/// failures (which use <c>INVALID_PARAMETER</c>). </summary>
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

/// <summary> Thrown by <see cref="ICheckpointManager.MergeAllAsync"/> when the checkpoint tree topology is not a
/// linear chain (e.g. branched trees, multiple children). Maps to <c>MERGE_NOT_SUPPORTED</c> in the MCP error
/// taxonomy. Distinct from <see cref="CheckpointMergeFailedException"/> which represents a runtime failure of an
/// attempted merge. </summary>
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

/// <summary>MergeAllAsync attempted a linear merge but Hyper-V failed (I/O, locked VHDX or transient failure): CHECKPOINT_MERGE_FAILED.
/// Distinct from topology rejection (MergeNotSupportedException) and create/restore/list/delete failure (CheckpointFailedException).</summary>
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

/// <summary> Thrown by <c>vm_create_base_image</c> when the in-guest <c>sysprep /generalize /oobe /shutdown</c>
/// step did not succeed: the in-guest invocation failed, or the VM failed to reach <c>Off</c> within
/// <c>shutdownTimeoutSeconds</c>. Maps to <c>SYSPREP_FAILED</c>. </summary>
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

/// <summary> Thrown by <c>vm_create_base_image</c> when the host-side <see cref="System.IO.File.Copy(string,
/// string, bool)"/> of the VM's primary VHDX into the configured image directory fails. Causes:
/// <c>ServerOptions.ImageDirectory</c> not configured, destination file already exists, source missing, or
/// IO/permission error. Maps to <c>IMAGE_COPY_FAILED</c>. </summary>
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

/// <summary>Create failed or was cancelled after detached-token rollback was awaited; carries
/// { vmName, phase, rollback: { performed, succeeded, elapsedMs, residualArtifacts } } in error details.
/// ErrorCode selects OPERATION_CANCELED for caller cancellation, COMMAND_TIMEOUT for child timeout, otherwise COMMAND_FAILED.</summary>
public class VmCreateRollbackException : Exception
{
    /// <summary>VM name that the failed <c>vm_create</c> targeted.</summary>
    public string VmName { get; }

    /// <summary>Final error code the envelope should carry.</summary>
    public string ErrorCode { get; }

    /// <summary>Last successful create-pipeline phase before failure ( enum).</summary>
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

/// <summary>Cancellation-safe rollback outcome serialized into McpToolResponse.Details.rollback.
/// No orphan VHDX means ResidualArtifacts is empty.</summary>
public class VmCreateRollbackInfo
{
    public bool Performed { get; init; }
    public bool Succeeded { get; init; }
    public long ElapsedMs { get; init; }
    public IReadOnlyList<string> ResidualArtifacts { get; init; } = Array.Empty<string>();
}

/// <summary>Guest destination-parent creation failed (ACL, read-only volume, invalid drive or quota): DEST_DIR_MISSING.
/// ErrorMapper uses the exception type, not message substrings, to distinguish an uncreatable parent from a missing source (FILE_NOT_FOUND).</summary>
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

/// <summary>SessionStore could not open PowerShell Direct, including Linux PSSessionOpenFailed/vmhypervsocketclient failures.
/// InvalidOperationException inheritance preserves existing test contracts. The typed SESSION_FAILED mapper arm must precede
/// path-not-found matching to prevent Linux copy failures being mislabeled FILE_NOT_FOUND.</summary>
public class SessionOpenFailedException : InvalidOperationException
{
    public string SessionName { get; }
    public string VmId { get; }

    /// <summary>
    /// True only when the throw site recognized the guest as refusing the supplied credential.
    /// Defaults to <c>false</c> so an unrecognized signal falls through to the generic
    /// <c>SESSION_FAILED</c> arm rather than guessing an authentication verdict.
    /// </summary>
    public bool CredentialRejected { get; }

    /// <summary>
    /// Attempted username. Non-secret, surfaced in the caller-facing message; never
    /// accompanied by the password.
    /// </summary>
    public string? Username { get; }

    /// <summary>Server-side stderr spill identifier lets the bounded envelope point to omitted diagnostic detail.</summary>
    public string? SpillSummary { get; }

    /// <summary>Capture the cause structurally at the throw site; bounding preserves it verbatim regardless of position,
    /// rather than trying to recover it from head/tail-truncated prose.</summary>
    public string? DecisiveCause { get; }

    public SessionOpenFailedException(
        string sessionName,
        string vmId,
        string message,
        Exception? innerException = null,
        bool credentialRejected = false,
        string? username = null,
        string? spillSummary = null,
        string? decisiveCause = null)
        : base(message, innerException)
    {
        SessionName = sessionName;
        VmId = vmId;
        CredentialRejected = credentialRejected;
        Username = username;
        SpillSummary = spillSummary;
        DecisiveCause = decisiveCause;
    }
}

/// <summary>Linux SSH connection/session-open failure maps to SESSION_FAILED, like PowerShell Direct.
/// A non-zero remote exit instead returns CommandResult. Redact credentials before construction; never embed SSH keys or private-key material.</summary>
public class SshSessionOpenException : InvalidOperationException
{
    public string VmId { get; }

    /// <summary>
    /// Identifier of the server-side stderr spill holding the full diagnostic text, so the SSH
    /// leg has the same referent as the PSDirect leg.
    /// </summary>
    public string? SpillSummary { get; }

    /// <summary>
    /// The nested-cause text, already redacted with the actual request password while it was
    /// still in scope. Consumers MUST read this instead of walking <see cref="Exception.InnerException"/>:
    /// the mapper has no password and therefore cannot remove an encoded secret from a raw chain.
    /// </summary>
    public string? RedactedCauseChain { get; }

    /// <summary>
    /// The cause statement as known at the throw site (SSH.NET states it in the innermost
    /// exception), already redacted. Reproduced verbatim by the bounding pass irrespective of
    /// its position in the flattened text.
    /// </summary>
    public string? DecisiveCause { get; }

    public SshSessionOpenException(
        string vmId,
        string message,
        Exception? innerException = null,
        string? spillSummary = null,
        string? redactedCauseChain = null,
        string? decisiveCause = null)
        : base(message, innerException)
    {
        VmId = vmId;
        SpillSummary = spillSummary;
        RedactedCauseChain = redactedCauseChain;
        DecisiveCause = decisiveCause;
    }
}

/// <summary>Guest transfer failed without denial, missing path or connection loss. IOException preserves TRANSFER_FAILED;
/// the typed mapper forwards the composed direction/VM/path message instead of generic I/O text.</summary>
public class GuestTransferFailedException : IOException
{
    /// <summary>True only when the message already carries direction, VM and failing path and must be forwarded unchanged.
    /// Explicit state avoids prefix-matching inner errors that omit VM context and would suppress the needed outer wrapper.</summary>
    public bool CarriesCallerContext { get; init; }

    public GuestTransferFailedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Guest denial retains AUTH_FAILED through UnauthorizedAccessException inheritance but forwards composed direction/VM/path text.
/// A separate type avoids exposing unproven-safe host image/VHDX messages from the shared UnauthorizedAccessException arm.</summary>
public class GuestTransferAccessDeniedException : UnauthorizedAccessException
{
    public GuestTransferAccessDeniedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A guest file transfer targeted a path that does not exist. Extends
/// <see cref="FileNotFoundException"/> so it still classifies as <c>FILE_NOT_FOUND</c>, typed for
/// the same reason as <see cref="GuestTransferAccessDeniedException"/>.
/// </summary>
public class GuestTransferPathNotFoundException : FileNotFoundException
{
    public GuestTransferPathNotFoundException(
        string message, string? fileName = null, Exception? innerException = null)
        : base(message, fileName, innerException)
    {
    }
}

/// <summary>SSH/SFTP connection establishment or loss is a session fault (SESSION_FAILED), not a transfer fault.
/// Discard the cached client so the next call reconnects; both cases share handling.</summary>
public class GuestConnectionLostException : InvalidOperationException
{
    /// <summary>True when this message already names direction, VM, and failing path.</summary>
    public bool CarriesCallerContext { get; init; }

    public GuestConnectionLostException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Known-Linux without usable SSH, or undetermined guest OS, prevents transport selection: SESSION_FAILED.
/// Thrown synchronously to reach ErrorMapper. Distinguish the conditions by message and router log marker, never by error code.</summary>
public class GuestRoutingUnavailableException : InvalidOperationException
{
    public string VmId { get; }

    public GuestRoutingUnavailableException(string vmId, string message)
        : base(message)
    {
        VmId = vmId;
    }
}

/// <summary>Password-bearing creation rejects non-generalized or indeterminate base images before artifacts exist; no rollback is needed.
/// The typed mapper arm must precede generic InvalidOperationException or this becomes COMMAND_FAILED.</summary>
public class BaseImageNotGeneralizedException : InvalidOperationException
{
    /// <summary>True when the verdict was "could not be confirmed" rather than a clean negative.</summary>
    public bool Indeterminate { get; }

    public BaseImageNotGeneralizedException(string message, bool indeterminate)
        : base(message)
    {
        Indeterminate = indeterminate;
    }
}

/// <summary>
/// One shared vocabulary for the <c>vm_pause</c> settle script, the sentinel crossing the
/// PowerShell seam, and the caller-visible <c>details.pauseOutcome</c>, so no alternative spelling
/// can appear at any of those three points.
/// </summary>
public static class PauseOutcomes
{
    public const string SettledPaused = "settled_paused";
    public const string ConflictingTerminalState = "conflicting_terminal_state";
    public const string WaitExhausted = "wait_exhausted";

    /// <summary>Prefix the manager searches for anywhere in a failure message.</summary>
    public const string SentinelPrefix = "vm_pause:";
}

/// <summary>Typed pause failure carries outcome/state details that a generic PowerShell throw would lose as COMMAND_FAILED.
/// Only PauseOutcome classifies; exhausted waits and genuine conflicts can share ObservedState.
/// The typed mapper arm must precede generic InvalidOperationException.</summary>
public class VmStateConflictException : InvalidOperationException
{
    /// <summary>Exactly <c>conflicting_terminal_state</c> or <c>wait_exhausted</c>.</summary>
    public string PauseOutcome { get; }

    /// <summary>The <c>Get-VM</c> state token read at abandon.</summary>
    public string ObservedState { get; }

    public VmStateConflictException(string pauseOutcome, string observedState, string message)
        : base(message)
    {
        PauseOutcome = pauseOutcome;
        ObservedState = observedState;
    }
}

/// <summary>Guest-login readiness was not confirmed; the VM remains available for inspection.</summary>
public class ReadinessNotReachedException : InvalidOperationException
{
    /// <summary>Name of the preserved VM.</summary>
    public string VmName { get; }

    /// <summary>
    /// False when the guest-delivery answer file could not be confirmed deleted, which downgrades
    /// the caller-facing outcome.
    /// </summary>
    public bool ArtifactScrubbed { get; }

    public ReadinessNotReachedException(string vmName, string message, bool artifactScrubbed)
        : base(message)
    {
        VmName = vmName;
        ArtifactScrubbed = artifactScrubbed;
    }
}
