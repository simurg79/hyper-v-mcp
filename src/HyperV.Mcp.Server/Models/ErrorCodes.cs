namespace HyperV.Mcp.Server.Models;

public static class ErrorCodes
{
    public const string VmNotFound = "VM_NOT_FOUND";
    public const string VmNotRunning = "VM_NOT_RUNNING";
    public const string VmAlreadyExists = "VM_ALREADY_EXISTS";
    public const string BootstrapFailed = "BOOTSTRAP_FAILED";
    public const string DestroyFailed = "DESTROY_FAILED";

    // ISO Installation
    public const string IsoNotFound = "ISO_NOT_FOUND";
    public const string InstallTimeout = "INSTALL_TIMEOUT";
    public const string InstallFailed = "INSTALL_FAILED";
    public const string AutounattendFailed = "AUTOUNATTEND_FAILED";

    /// <summary>Non-Windows media (no sources\install.wim) is rejected before orchestration;
    /// skipPreflight cannot bypass this check.</summary>
    public const string OsNotSupported = "OS_NOT_SUPPORTED";

    /// <summary>CPU/memory/disk falls below Windows 11 floors; data carries failedFloor/minimum/actual.
    /// skipPreflight=true permits smaller Windows Server/Windows 10 installations.</summary>
    public const string InsufficientResources = "INSUFFICIENT_RESOURCES";

    // Separate Ubuntu timeout/failure codes let callers distinguish Linux outcomes without changing the Windows taxonomy.

    /// <summary>
    /// the Ubuntu guest did not publish its completion KVP
    /// (<c>hyperv-mcp/os-install</c>) within <c>timeoutMinutes</c>. Terminal job phase; VM
    /// preserved. Linux-only; the Windows path keeps <see cref="InstallTimeout"/>.
    /// </summary>
    public const string LinuxInstallTimeout = "LINUX_INSTALL_TIMEOUT";

    /// <summary>
    /// cloud-init reported a terminal provisioning failure
    /// (a <c>failed:*</c> KVP) or an observable error before the ready signal. Terminal job phase;
    /// VM preserved. Linux-only; the Windows path keeps <see cref="InstallFailed"/>.
    /// </summary>
    public const string LinuxProvisionFailed = "LINUX_PROVISION_FAILED";

    /// <summary>Explicitly incompatible Ubuntu generation/firmware fails before any VM or job.
    /// Separate from resource floors so callers can distinguish firmware ineligibility.</summary>
    public const string LinuxPreconditionUnmet = "LINUX_PRECONDITION_UNMET";

    // Remoting
    public const string HostNotFound = "HOST_NOT_FOUND";
    public const string HostUnreachable = "HOST_UNREACHABLE";
    public const string SessionFailed = "SESSION_FAILED";

    public const string CommandTimeout = "COMMAND_TIMEOUT";
    public const string CommandFailed = "COMMAND_FAILED";
    public const string ScriptFailed = "SCRIPT_FAILED";

    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string TransferFailed = "TRANSFER_FAILED";

    /// <summary>Single-file host-to-guest copy could not create the destination parent (ACL, read-only volume, invalid/unmapped drive or quota).
    /// Includes failed New-Item -ItemType Directory -Force; FILE_NOT_FOUND remains reserved for missing source artifacts.</summary>
    public const string DestDirMissing = "DEST_DIR_MISSING";

    public const string CheckpointFailed = "CHECKPOINT_FAILED";

    /// <summary> Checkpoint tree topology is not supported by the linear merge implementation (branched trees /
    /// multiple children). Returned by <c>vm_create_base_image</c> when <c>mergeCheckpoints=true</c> encounters a
    /// non-linear chain. Caller should resolve branches manually before retrying. </summary>
    public const string MergeNotSupported = "MERGE_NOT_SUPPORTED";

    /// <summary> Linear checkpoint merge attempted but the underlying Hyper-V <c>Remove-VMSnapshot</c> merge-job
    /// failed (I/O error, locked VHDX, transient Hyper-V failure, etc.). Distinct from <see
    /// cref="MergeNotSupported"/> which is a topology pre-condition rejection. </summary>
    public const string CheckpointMergeFailed = "CHECKPOINT_MERGE_FAILED";

    // Base Image Creation (vm_create_base_image)

    /// <summary>
    /// In-guest <c>sysprep /generalize /oobe /shutdown</c> did not
    /// complete successfully (in-guest invocation failed, or VM did not reach
    /// Off state within <c>shutdownTimeoutSeconds</c>).
    /// </summary>
    public const string SysprepFailed = "SYSPREP_FAILED";

    /// <summary>
    /// Host-side copy of the VM's primary VHDX into the configured
    /// image directory failed. Causes include: <c>ImageDirectory</c> not configured,
    /// destination file already exists, source VHDX missing, or IO/permission error.
    /// </summary>
    public const string ImageCopyFailed = "IMAGE_COPY_FAILED";

    /// <summary>
    /// a <c>vm_create</c> call supplied an administrator password but the base image is
    /// not sysprepped/generalized — or generalization could not be determined, which fails closed
    /// into the same family. Rejected before any artifact is created, so no VM exists.
    /// </summary>
    public const string BaseNotGeneralized = "BASE_NOT_GENERALIZED";

    /// <summary>Guest-login readiness was not confirmed: budget exhausted or observation failed.</summary>
    public const string ReadinessNotReached = "READINESS_NOT_REACHED";

    /// <summary>
    /// The VM settled into a stable state other than <c>Paused</c>, so the pause can never succeed
    /// as issued. A wait that merely elapsed stays <see cref="CommandFailed"/> — that outcome is
    /// unknown, not known-and-conflicting.
    /// </summary>
    public const string VmStateConflict = "VM_STATE_CONFLICT";

    // Validation
    public const string InvalidParameter = "INVALID_PARAMETER";

    // Security
    public const string AuthFailed = "AUTH_FAILED";
    public const string InsufficientPrivilege = "INSUFFICIENT_PRIVILEGE";
    public const string MissingCredentials = "MISSING_CREDENTIALS";

    // Operational
    public const string ConcurrencyLimit = "CONCURRENCY_LIMIT";

    /// <summary> the inbound MCP request <see cref="System.Threading.CancellationToken"/> was signalled before
    /// <c>vm_create</c> completed (e.g., the Roo client cancelled at its 60 s RPC budget). The detached-CTS rollback
    /// was awaited and its outcome is reported via <c>McpToolResponse.Details.rollback</c>. </summary>
    public const string OperationCanceled = "OPERATION_CANCELED";

    /// <summary> Filesystem operation failed against a configured path that exists but cannot be read, enumerated, or
    /// written due to permissions, locking, or other I/O failure. Distinct from <see cref="InvalidParameter"/>
    /// (caller-supplied path is malformed or non-existent) and <see cref="FileNotFound"/> (specific source artifact
    /// missing during transfer). Used by <c>vm_list_images</c>. </summary>
    public const string IoError = "IO_ERROR";
}

/// <summary>Dispatcher/mapper runtime codes outside the standard 18-code MCP taxonomy; exceptions are caught and wrapped.</summary>
public static class RuntimeErrorCodes
{
    /// <summary>
    /// Tool name not found in the dispatcher registry.
    /// </summary>
    public const string ToolNotFound = "TOOL_NOT_FOUND";

    /// <summary>
    /// Catch-all for unmapped exceptions.
    /// </summary>
    public const string InternalError = "INTERNAL_ERROR";
}
