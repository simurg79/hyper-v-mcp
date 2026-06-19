namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Error codes for the MCP error taxonomy.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy.
/// </summary>
public static class ErrorCodes
{
    // Lifecycle
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

    /// <summary>
    /// Issue #97 / ISO-D16: ISO does not appear to be a Windows installer
    /// (no <c>sources\install.wim</c>). <c>vm_os_install</c> is Windows-only
    /// in its current form; non-Windows ISOs are rejected before any
    /// orchestration runs. Always enforced; not bypassable by
    /// <c>skipPreflight</c>.
    /// </summary>
    public const string OsNotSupported = "OS_NOT_SUPPORTED";

    /// <summary>
    /// Issue #97 / ISO-D17: Resource-floor preflight failed (cpuCount,
    /// memoryMB, or diskSizeGB below documented Windows 11 minimum).
    /// The response payload's <c>data</c> field carries
    /// <c>failedFloor</c> / <c>minimum</c> / <c>actual</c> for the violated
    /// floor. Bypassable via the <c>skipPreflight=true</c> tool argument
    /// for callers installing Windows Server / Windows 10 with smaller
    /// footprints.
    /// </summary>
    public const string InsufficientResources = "INSUFFICIENT_RESOURCES";

    // Remoting
    public const string HostNotFound = "HOST_NOT_FOUND";
    public const string HostUnreachable = "HOST_UNREACHABLE";
    public const string SessionFailed = "SESSION_FAILED";

    // Execution
    public const string CommandTimeout = "COMMAND_TIMEOUT";
    public const string CommandFailed = "COMMAND_FAILED";
    public const string ScriptFailed = "SCRIPT_FAILED";

    // File Transfer
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string TransferFailed = "TRANSFER_FAILED";

    /// <summary>
    /// Issue #204 / VC-DEST-D2: <c>vm_copy_file</c> (single-file host→guest) could
    /// not place the file because the destination's parent directory is
    /// <b>missing or not creatable</b> on the guest. This covers both:
    /// (a) the parent did not exist and the service-side auto-create
    /// (<c>New-Item -ItemType Directory -Force</c>, VC-DEST-D1/D4) failed, and
    /// (b) the parent could not be created due to an ACL denial, read-only volume,
    /// invalid/unmapped drive letter, or quota exhaustion. Distinct from
    /// <see cref="FileNotFound"/>, which remains reserved for missing <b>source</b>
    /// artifacts (Issue #38 regression contract).
    /// See /myplans/execution/file-transfer/vm-copy-file-dest-dir-design.md — VC-DEST-D2.
    /// </summary>
    public const string DestDirMissing = "DEST_DIR_MISSING";

    // Checkpoints
    public const string CheckpointFailed = "CHECKPOINT_FAILED";

    /// <summary>
    /// Issue #51 / CP-D6: Checkpoint tree topology is not supported by the linear
    /// merge implementation (branched trees / multiple children). Returned by
    /// <c>vm_create_base_image</c> when <c>mergeCheckpoints=true</c> encounters a
    /// non-linear chain. Caller should resolve branches manually before retrying.
    /// </summary>
    public const string MergeNotSupported = "MERGE_NOT_SUPPORTED";

    /// <summary>
    /// Issue #51 / CP-D6: Linear checkpoint merge attempted but the underlying
    /// Hyper-V <c>Remove-VMSnapshot</c> merge-job failed (I/O error, locked VHDX,
    /// transient Hyper-V failure, etc.). Distinct from <see cref="MergeNotSupported"/>
    /// which is a topology pre-condition rejection.
    /// </summary>
    public const string CheckpointMergeFailed = "CHECKPOINT_MERGE_FAILED";

    // Base Image Creation (Issue #51 / vm_create_base_image)

    /// <summary>
    /// Issue #51: In-guest <c>sysprep /generalize /oobe /shutdown</c> did not
    /// complete successfully (in-guest invocation failed, or VM did not reach
    /// Off state within <c>shutdownTimeoutSeconds</c>).
    /// </summary>
    public const string SysprepFailed = "SYSPREP_FAILED";

    /// <summary>
    /// Issue #51: Host-side copy of the VM's primary VHDX into the configured
    /// image directory failed. Causes include: <c>ImageDirectory</c> not configured,
    /// destination file already exists, source VHDX missing, or IO/permission error.
    /// </summary>
    public const string ImageCopyFailed = "IMAGE_COPY_FAILED";

    // Validation
    public const string InvalidParameter = "INVALID_PARAMETER";

    // Security
    public const string AuthFailed = "AUTH_FAILED";
    public const string InsufficientPrivilege = "INSUFFICIENT_PRIVILEGE";
    public const string MissingCredentials = "MISSING_CREDENTIALS";

    // Operational
    public const string ConcurrencyLimit = "CONCURRENCY_LIMIT";

    /// <summary>
    /// Issue #164 / LF-D17: the inbound MCP request <see cref="System.Threading.CancellationToken"/>
    /// was signalled before <c>vm_create</c> completed (e.g., the Roo client cancelled at
    /// its 60 s RPC budget). The detached-CTS rollback was awaited and its outcome is
    /// reported via <c>McpToolResponse.Details.rollback</c>.
    /// </summary>
    public const string OperationCanceled = "OPERATION_CANCELED";

    // Storage / I/O — see /myplans/mcp-interface/mcp-interface-design.md and ST-D7.
    /// <summary>
    /// Filesystem operation failed against a configured path that exists but cannot
    /// be read, enumerated, or written due to permissions, locking, or other I/O
    /// failure. Distinct from <see cref="InvalidParameter"/> (caller-supplied path
    /// is malformed or non-existent) and <see cref="FileNotFound"/> (specific source
    /// artifact missing during transfer). Used by <c>vm_list_images</c> per ST-D7.
    /// </summary>
    public const string IoError = "IO_ERROR";
}

/// <summary>
/// Extended error codes for runtime dispatch and internal error handling.
/// These are not part of the standard MCP error taxonomy (18 codes) defined in
/// /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy.
/// They are used by the tool dispatcher and error mapper for runtime behavior.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
/// </summary>
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
