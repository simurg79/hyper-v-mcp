using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Text.Json;
using HyperV.Mcp.Server.Models;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Maps internal exceptions to MCP response envelope shapes.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy.
///
/// Design decisions:
/// - Pattern-matching on exception type maps to the appropriate error code constant.
/// - CommandTimeoutException includes partial output data in the response (ADR-9).
/// - FileNotFoundException maps to FILE_NOT_FOUND error code.
/// - ArgumentException/ArgumentNullException maps to INVALID_PARAMETER error code.
/// - UnauthorizedAccessException maps to AUTH_FAILED error code.
/// - TimeoutException maps to COMMAND_TIMEOUT error code (generic timeout).
/// - InvalidOperationException maps to COMMAND_FAILED for operational failures,
///   forwarding the exception message (which contains user-relevant diagnostic info
///   from HyperVManager.HandleError — PowerShell errors, exit codes, stderr output).
///   Falls back to a generic message when the exception message is empty.
/// - NotSupportedException maps to INVALID_PARAMETER for unsupported operations.
/// - All unknown exception types fall through to INTERNAL_ERROR as catch-all.
/// - vmState is forwarded to the response envelope when provided (ADR-8).
/// - Error messages are sanitized to prevent leaking secrets, credentials, or internal
///   implementation details. Domain exceptions carry safe, user-supplied identifiers.
///   Generic/system exceptions use fixed safe messages per error code.
/// </summary>
public class ErrorMapper : IErrorMapper
{
    /// <inheritdoc />
    /// <remarks>
    /// Maps each known exception type to the correct error code per the taxonomy.
    /// Outward-facing error messages are sanitized: domain exceptions include safe
    /// identifiers (hostId, vmId, toolName); generic exceptions use fixed messages
    /// to avoid leaking credentials, paths, or internal stack details.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy.
    /// </remarks>
    public McpToolResponse MapException(Exception ex, string? vmState = null)
    {
        // ── Issue #52, Gate 6 re-review: PowerShellDirectChannelException unwrap ──
        // The channel wraps non-cancellation, non-timeout failures from the underlying
        // IPowerShellHost in PowerShellDirectChannelException, with the ORIGINAL failure
        // attached as InnerException (concrete type preserved — e.g.
        // PSRemotingTransportException, RuntimeException) and the top-level Message
        // already credential-redacted by ExecuteWithRetryAsync's outer guard
        // (see PowerShellDirectChannel — PSD-D8 / Gate 6 re-verification fix).
        //
        // Without this unwrap, every channel-thrown failure (transport drop, broken
        // runspace, credential failure, etc.) would fall through to the generic
        // INTERNAL_ERROR catch-all instead of being classified by its real cause.
        //
        // PowerShellDirectChannel wraps inner exceptions while preserving their concrete
        // type (it no longer rebuilds the chain via RedactExceptionTree, which was removed).
        // The wrapper's top-level Message is already redacted; the InnerException's Message
        // is preserved as-thrown. We unwrap once to classify by inner type, then overwrite
        // the recursed Error text with the wrapper's redacted top-level so credentials
        // cannot escape via the MCP error envelope.
        //
        // Strategy:
        //   1. Recurse on the inner exception so it is classified by its actual type
        //      (PSRemotingTransportException → SESSION_FAILED, RuntimeException with
        //      "failed login" → AUTH_FAILED, etc.).
        //   2. Replace the inner-classified message with the channel's already-redacted
        //      top-level message — that is what the user is meant to see.
        //   3. Bound the recursion to a single unwrap. The channel never wraps a
        //      PowerShellDirectChannelException inside another, but the guard makes the
        //      contract explicit.
        if (ex is PowerShellDirectChannelException channelEx)
        {
            var inner = channelEx.InnerException;
            if (inner is not null and not PowerShellDirectChannelException)
            {
                var innerResponse = MapException(inner, vmState);
                return new McpToolResponse
                {
                    Success = false,
                    Error = string.IsNullOrWhiteSpace(channelEx.Message)
                        ? innerResponse.Error
                        : channelEx.Message,
                    ErrorCode = innerResponse.ErrorCode,
                    Data = innerResponse.Data,
                    State = vmState,
                };
            }
            // No inner (or pathological double-wrap): default to COMMAND_FAILED with the
            // redacted top-level message — NOT INTERNAL_ERROR.
            return new McpToolResponse
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(channelEx.Message)
                    ? "The PowerShell Direct channel operation failed."
                    : channelEx.Message,
                ErrorCode = ErrorCodes.CommandFailed,
                Data = null,
                State = vmState,
            };
        }

        var (errorCode, message, data) = ex switch
        {
            // Issue #204 / VC-DEST-D3 / VC-DEST-D4: typed classification for guest-side
            // ensure-parent failure during single-file vm_copy_file. MUST be type-based
            // (not substring) and MUST be matched BEFORE the FileNotFoundException /
            // substring "cannot find path" branches so that a missing/uncreatable
            // destination parent surfaces as DEST_DIR_MISSING rather than FILE_NOT_FOUND
            // (Issue #38 regression contract preserved for actual source-missing cases).
            // ErrorMapper deliberately does NOT reference the diagnostic marker constant
            // — classification is type-based only (anti-spoof, VC-DEST-D8 rule 3).
            DestinationDirectoryUnavailableException destDirEx =>
                (ErrorCodes.DestDirMissing,
                 $"Destination parent directory for '{destDirEx.DestinationPath}' is missing or not creatable on the guest.",
                 (object?)null),

            // Domain exceptions — well-known error codes with safe, user-supplied identifiers.
            HostNotFoundException hostEx =>
                (ErrorCodes.HostNotFound, $"Host '{hostEx.HostId}' was not found.", (object?)null),

            VmNotFoundException vmEx =>
                (ErrorCodes.VmNotFound, $"VM '{vmEx.VmId}' was not found on host '{vmEx.HostId}'.", (object?)null),

            // Issue #203 / VC-DUP-D5: name-collision envelope. Use the exception's
            // own (contractually-pinned) Message verbatim — see VmAlreadyExistsException
            // for the canonical format. Forwarding the message rather than rebuilding
            // it here keeps Constraint #6 in one place.
            VmAlreadyExistsException vmExistsEx =>
                (ErrorCodes.VmAlreadyExists, vmExistsEx.Message, (object?)null),

            // Issue #203 / VC-DUP-D4: residual-race name-collision classifier
            // (defense-in-depth). If a generic InvalidOperationException carrying
            // PowerShell's "already exists" stderr ever reaches the mapper
            // (e.g., a code path that didn't unwrap via HyperVManager), classify
            // it as VM_ALREADY_EXISTS with a sanitized message instead of leaking
            // raw PS-throw text via the generic COMMAND_FAILED branch. Must come
            // BEFORE the generic InvalidOperationException catch-all.
            InvalidOperationException ioNameEx when IsNameCollisionMessage(ioNameEx.Message) =>
                (ErrorCodes.VmAlreadyExists,
                 "A VM with the requested name already exists on the target host.",
                 (object?)null),

            ConcurrencyLimitException =>
                (ErrorCodes.ConcurrencyLimit, "The operation was rejected because the concurrency limit has been reached. Retry later.", (object?)null),

            ToolNotFoundException toolEx =>
                (RuntimeErrorCodes.ToolNotFound, $"Tool '{toolEx.ToolName}' is not registered.", (object?)null),

            // CommandTimeoutException includes partial output in the data field (ADR-9).
            // See /myplans/execution/commands/commands-design.md — CMD-D4: Timeout returns success:false with partial output.
            CommandTimeoutException timeoutEx => (
                ErrorCodes.CommandTimeout,
                $"Command timed out after {timeoutEx.DurationMs}ms.",
                (object?)new CommandResult
                {
                    ExitCode = -1,
                    Stdout = timeoutEx.PartialStdout ?? string.Empty,
                    Stderr = timeoutEx.PartialStderr ?? string.Empty,
                    TimedOut = true,
                    DurationMs = timeoutEx.DurationMs
                }),

            // FileNotFoundException maps to FILE_NOT_FOUND per the error code taxonomy.
            FileNotFoundException =>
                (ErrorCodes.FileNotFound, "The specified file was not found.", (object?)null),

            // Operational/domain failure mappings — prevents over-broad INTERNAL_ERROR usage.

            // ArgumentException (and ArgumentNullException, ArgumentOutOfRangeException) → INVALID_PARAMETER.
            // These represent validation failures from tool argument parsing.
            // Safe to include parameter name (user-supplied), but not the full message which
            // may contain internal validation details.
            // See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
            ArgumentException argEx =>
                (ErrorCodes.InvalidParameter, SafeArgumentMessage(argEx), (object?)null),

            // UnauthorizedAccessException → AUTH_FAILED.
            // Covers PowerShell Direct (PSSession) credential failures and permission issues.
            // Raw message may contain server names, paths, or credential hints — use fixed message.
            // See /myplans/security/security-design.md — Error Code Taxonomy: AUTH_FAILED.
            UnauthorizedAccessException =>
                (ErrorCodes.AuthFailed, "Authentication failed or access was denied.", (object?)null),

            // TimeoutException → COMMAND_TIMEOUT.
            // Generic timeout from System.TimeoutException (e.g., session-acquisition timeout
            // when establishing a PSSession to the guest via PowerShell Direct).
            // See /myplans/execution/commands/commands-design.md — CMD-D4.
            TimeoutException =>
                (ErrorCodes.CommandTimeout, "The operation timed out.", (object?)null),

            // CheckpointFailedException → CHECKPOINT_FAILED.
            // Dedicated exception for checkpoint operation failures (create, restore, remove).
            // Must be matched BEFORE InvalidOperationException since CheckpointFailedException
            // extends InvalidOperationException.
            // Safe to include VM/checkpoint identifiers (user-supplied).
            // See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: CHECKPOINT_FAILED.
            CheckpointFailedException cpEx =>
                (ErrorCodes.CheckpointFailed,
                 $"Checkpoint operation failed for VM '{cpEx.VmId}' on host '{cpEx.HostId}'." +
                 (cpEx.CheckpointName != null ? $" Checkpoint: '{cpEx.CheckpointName}'." : ""),
                 (object?)null),

            // Issue #51 / CP-D6: branched/non-linear checkpoint tree rejected by
            // MergeAllAsync. Must be matched BEFORE InvalidOperationException since
            // it extends it. Message is operator-supplied diagnostic and safe to forward.
            MergeNotSupportedException mnsEx =>
                (ErrorCodes.MergeNotSupported, mnsEx.Message, (object?)null),

            // Issue #51 / CP-D6: linear-chain merge attempted but underlying
            // Hyper-V merge job failed. Must be matched BEFORE InvalidOperationException.
            CheckpointMergeFailedException cmfEx =>
                (ErrorCodes.CheckpointMergeFailed, cmfEx.Message, (object?)null),

            // Issue #51: in-guest sysprep step failed or VM did not shut down in time.
            // Must be matched BEFORE InvalidOperationException since it extends it.
            SysprepFailedException sfEx =>
                (ErrorCodes.SysprepFailed, sfEx.Message, (object?)null),

            // Issue #51: host-side File.Copy of the primary VHDX into the image
            // directory failed. Message is operator-supplied (no credentials) and
            // safe to forward. Must be matched BEFORE InvalidOperationException.
            ImageCopyFailedException icfEx =>
                (ErrorCodes.ImageCopyFailed, icfEx.Message, (object?)null),

            // Issue #164 / LF-D17: vm_create rollback envelope. Carries its own
            // ErrorCode (OPERATION_CANCELED | COMMAND_TIMEOUT | COMMAND_FAILED)
            // and structured details that LF-D17 requires under `details.rollback`.
            // The Details body is set separately on the response (see below) since
            // the tuple shape here only supports `data`.
            VmCreateRollbackException vmRollbackEx =>
                (vmRollbackEx.ErrorCode, vmRollbackEx.Message, (object?)null),

            // ISO installation typed exceptions → ISO-specific error codes.
            // Must be matched BEFORE InvalidOperationException since they extend it.
            // See /myplans/vm-management/iso-installation/iso-installation-design.md — Error Handling.
            IsoNotFoundException isoEx =>
                (ErrorCodes.IsoNotFound, isoEx.Message, (object?)null),

            InstallTimeoutException timeoutInstEx =>
                (ErrorCodes.InstallTimeout, timeoutInstEx.Message, (object?)null),

            InstallFailedException installEx =>
                (ErrorCodes.InstallFailed, installEx.Message, (object?)null),

            AutounattendFailedException auEx =>
                (ErrorCodes.AutounattendFailed, auEx.Message, (object?)null),

            // OsNotSupportedException → OS_NOT_SUPPORTED.
            // Issue #97 / ISO-D16: ISO does not contain sources\install.wim.
            // Must be matched BEFORE InvalidOperationException since it extends it.
            // Message is fixed/safe; IsoPath is user-supplied and not surfaced here
            // to keep the envelope minimal.
            OsNotSupportedException osEx =>
                (ErrorCodes.OsNotSupported, osEx.Message, (object?)null),

            // InsufficientResourcesException → INSUFFICIENT_RESOURCES.
            // Issue #97 / ISO-D17: cpuCount/memoryMB/diskSizeGB below minimum.
            // The data field carries failedFloor/minimum/actual so callers can
            // react programmatically (e.g., retry with skipPreflight=true).
            // Must be matched BEFORE InvalidOperationException since it extends it.
            InsufficientResourcesException resEx =>
                (ErrorCodes.InsufficientResources,
                 resEx.Message,
                 (object?)new
                 {
                     failedFloor = resEx.FailedFloor,
                     minimum = resEx.Minimum,
                     actual = resEx.Actual,
                 }),

            // MissingCredentialsException → MISSING_CREDENTIALS.
            // Thrown when VM credentials cannot be resolved from tool parameters or env vars.
            // Safe to include the exception message — it contains no secrets, only guidance.
            // Must be matched BEFORE InvalidOperationException catch-all.
            // See /myplans/security/credentials/credentials-design.md — Phase 1.
            // See GitHub Issue #20.
            MissingCredentialsException credEx =>
                (ErrorCodes.MissingCredentials, credEx.Message, (object?)null),

            // IoOperationFailedException → IO_ERROR.
            // Used when a configured filesystem path exists but cannot be read, enumerated,
            // or written due to permissions, locking, or other I/O failure (ST-D7).
            // The exception message is operator-facing diagnostic text built at the throw
            // site (e.g., HyperVManager.ListImagesAsync) and is safe to forward — it carries
            // user-configured paths, not credentials.
            // See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: IO_ERROR.
            IoOperationFailedException ioFailEx =>
                (ErrorCodes.IoError, ioFailEx.Message, (object?)null),

            // VmNotRunningException → VM_NOT_RUNNING.
            // Thrown when a command/script/file-transfer is attempted on a non-running VM.
            // Handled explicitly to preserve the specific VM_NOT_RUNNING error code.
            // See GitHub Issue #21.
            VmNotRunningException vmNotRunEx =>
                (ErrorCodes.VmNotRunning, $"VM '{vmNotRunEx.VmId}' on host '{vmNotRunEx.HostId}' is not running (state: {vmNotRunEx.ActualState}).", (object?)null),

            // ── Issue #209 (sub-finding) / VC-SO-D3, VC-SO-D4, VC-SO-D5 ──
            // PSDirect session-open failures (notably Linux guests where the
            // hypervisor socket negotiation fails) MUST classify as
            // SESSION_FAILED, not FILE_NOT_FOUND. These arms MUST sit ABOVE
            // the ST-6 substring block AND ABOVE the path-not-found arm
            // (~line 278) because the underlying stderr text frequently
            // contains "cannot find path"-shaped phrases (e.g. "cannot find
            // the path") that would otherwise be caught by the FILE_NOT_FOUND
            // arm first.
            //
            // VC-SO-D3: typed arm. The wrap-at-source in SessionStore throws
            // SessionOpenFailedException. We compose a non-empty error
            // string from ex.Message (with a synthetic fallback for empty
            // payloads, VC-SO-D5) and append the inner-exception message
            // when present and not already embedded.
            SessionOpenFailedException soEx =>
                (ErrorCodes.SessionFailed,
                 ComposeSessionOpenFailedError(soEx),
                 (object?)null),

            // VC-SO-D4: substring defense-in-depth. Catches any code path
            // that surfaces a New-PSSession failure as a plain
            // InvalidOperationException (i.e. bypassed the typed wrap).
            // Mirrors the existing ChannelMessageContains pattern from ST-6.
            // MUST also sit above the path-not-found arm.
            InvalidOperationException soSubEx when ChannelMessageContains(soSubEx,
                "PSSessionOpenFailed",
                "vmhypervsocketclient",
                "new-pssession") =>
                (ErrorCodes.SessionFailed,
                 string.IsNullOrWhiteSpace(soSubEx.Message)
                    ? "PSDirect session open failed: see logs"
                    : soSubEx.Message,
                 (object?)null),

            // ── Issue #52, ST-6: PowerShell Direct channel failure-mode mappings ──
            // FileTransferService / CommandExecutor surface guest-side failures as
            // InvalidOperationException carrying the channel's already-redacted stderr.
            // We pattern-match on substrings (case-insensitive) of the message to map
            // them to the most specific existing error code BEFORE falling through to
            // the generic InvalidOperationException → COMMAND_FAILED branch.
            // See /myplans/remoting/powershell-direct/powershell-direct-design.md.

            // Copy-Item -ToSession / -FromSession: source or destination not found on
            // either side of the transfer.
            InvalidOperationException pathEx when ChannelMessageContains(pathEx, "cannot find path", "does not exist") =>
                (ErrorCodes.FileNotFound,
                 "Source or destination path was not found on the guest or host.",
                 (object?)null),

            // Copy-Item -ToSession / -FromSession or Invoke-Command: access denied
            // inside the guest (NTFS ACL, UAC, or wrapped UnauthorizedAccessException
            // surfaced as text in PSSession stderr).
            InvalidOperationException denyEx when ChannelMessageContains(denyEx, "access is denied", "unauthorizedaccessexception") =>
                (ErrorCodes.AuthFailed,
                 "Access denied during file transfer.",
                 (object?)null),

            // Copy-Item -ToSession / -FromSession: target volume out of space.
            // 0x70 == ERROR_DISK_FULL. No dedicated DiskFull code exists today.
            // TODO(issue-52): consider dedicated DISK_FULL error code.
            InvalidOperationException spaceEx when ChannelMessageContains(spaceEx, "there is not enough space", "disk full", "0x70") =>
                (ErrorCodes.TransferFailed,
                 "Insufficient disk space on guest or host.",
                 (object?)null),

            // PSSession became unusable mid-operation (host transport drop, guest
            // reboot, VM stopped, runspace closed). Channel will evict on broken-
            // session retry; surface as SESSION_FAILED so caller knows to retry.
            InvalidOperationException sessEx when ChannelMessageContains(sessEx, "psremotingtransportexception", "session is broken", "session has been disconnected") =>
                (ErrorCodes.SessionFailed,
                 "PowerShell remoting session to the guest VM is no longer usable.",
                 (object?)null),

            // ── Issue #52, Gate 6 re-review: typed transport / runspace failures ──
            // These are normally wrapped by PowerShellDirectChannelException (handled
            // above), but the explicit branches let the type itself classify when it
            // surfaces directly (e.g. via the InnerException recursion path).

            // PSRemotingTransportException / PSRemotingDataStructureException →
            // SESSION_FAILED. The PSSession transport dropped or the runspace is no
            // longer usable; caller should retry to get a fresh session.
            PSRemotingTransportException =>
                (ErrorCodes.SessionFailed,
                 "PowerShell remoting session to the guest VM is no longer usable.",
                 (object?)null),

            PSRemotingDataStructureException =>
                (ErrorCodes.SessionFailed,
                 "PowerShell remoting session to the guest VM is no longer usable.",
                 (object?)null),

            // RuntimeException with broken-session signature → SESSION_FAILED.
            // Same broken-session matcher used by PowerShellDirectChannel.IsBrokenSessionFailure.
            RuntimeException reSess when ChannelMessageContains(reSess,
                "session is broken",
                "psremotingtransportexception",
                "the session state is broken",
                "runspace session is not in the opened state",
                "runspace is not available to run commands") =>
                (ErrorCodes.SessionFailed,
                 "PowerShell remoting session to the guest VM is no longer usable.",
                 (object?)null),

            // RuntimeException with credential / auth signature → AUTH_FAILED.
            // Covers "Failed login for user ...", "Access is denied", "logon failure", etc.
            RuntimeException reAuth when ChannelMessageContains(reAuth,
                "failed login",
                "logon failure",
                "access is denied",
                "unauthorizedaccessexception",
                "authentication failed",
                "the user name or password is incorrect") =>
                (ErrorCodes.AuthFailed,
                 "Authentication failed or access was denied.",
                 (object?)null),

            // Generic RuntimeException → COMMAND_FAILED with redacted message.
            // The PowerShellDirectChannelException unwrap path replaces this message with
            // the channel's already-redacted top-level message, so secrets cannot leak
            // even if a RuntimeException carrying credential text reaches this branch via
            // the inner-classification recursion.
            RuntimeException reGeneric =>
                (ErrorCodes.CommandFailed,
                 string.IsNullOrWhiteSpace(reGeneric.Message)
                    ? "A PowerShell runtime error occurred."
                    : RedactCredentials(reGeneric.Message, password: null),
                 (object?)null),

            // InvalidOperationException → COMMAND_FAILED (generic catch-all for this type).
            // Covers operational failures like VM state conflicts and other domain-level
            // errors that are not internal bugs.
            //
            // The message is forwarded because all current callers produce safe diagnostic
            // messages containing operational data, NOT secrets:
            //
            //   - HyperVManager.HandleError(): PowerShell cmdlet errors, exit codes, stderr
            //     output (e.g., "Get-VM : VM is in an invalid state").
            //   - FileTransferService.CopyToGuestAsync/CopyFromGuestAsync(): VM GUID + PowerShell
            //     stderr from Copy-Item -ToSession/-FromSession failures. May include host
            //     filesystem paths to VHDX files — these are user-configured and not secrets.
            //   - SessionStore.GetOrCreateSessionAsync(): Session name + PowerShell stderr
            //     from New-PSSession failures (e.g., "Failed to create PSSession
            //     'hyperv-mcp-local-{vmId}': The credential is invalid").
            //
            // Accepted risk: Messages may contain host filesystem paths (e.g., C:\HyperVMCP\VMs\...).
            // This is intentional — the MCP server runs on the user's own machine and these paths
            // are user-configured. Forwarding them is essential for debugging VM operation failures
            // (see GitHub Issue #10). If a future caller needs to throw InvalidOperationException
            // with a message containing actual secrets (credentials, tokens), it should use a
            // different exception type or sanitize the message before throwing.
            //
            // Falls back to a generic message when the exception message is empty.
            // See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: COMMAND_FAILED.
            // Issue #203 / VC-DUP-D5: PS-text sanitization is applied to the
            // wire message so positional tokens (At <path>:<line> char:<col>,
            // + ~~~, CategoryInfo, FullyQualifiedErrorId, RuntimeException stack
            // tail) never leak into the envelope's `error` field. Raw PS text
            // remains visible only via LogDebug at the throw site.
            InvalidOperationException ioEx =>
                (ErrorCodes.CommandFailed,
                 string.IsNullOrWhiteSpace(ioEx.Message)
                    ? "The operation failed due to an invalid state or precondition."
                    : SanitizePowerShellErrorText(RedactCredentials(ioEx.Message, password: null)),
                 (object?)null),

            // NotSupportedException → INVALID_PARAMETER.
            // Covers unsupported shell types, unsupported operations, etc.
            NotSupportedException =>
                (ErrorCodes.InvalidParameter, "The requested operation is not supported.", (object?)null),

            // DirectoryNotFoundException maps to FILE_NOT_FOUND per error code taxonomy.
            // Must be matched BEFORE IOException since DirectoryNotFoundException extends IOException.
            DirectoryNotFoundException =>
                (ErrorCodes.FileNotFound, "The specified directory was not found.", (object?)null),

            // IOException → TRANSFER_FAILED.
            // Covers disk I/O failures during file transfer, checkpoint operations, etc.
            // Raw message may contain filesystem paths — use fixed message.
            // See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: TRANSFER_FAILED.
            IOException =>
                (ErrorCodes.TransferFailed, "A file transfer or I/O operation failed.", (object?)null),

            // JsonException → COMMAND_FAILED.
            // Covers JSON parsing failures when PowerShell output contains unexpected
            // content (e.g., ANSI escape codes from pwsh.exe, progress bar artifacts,
            // or non-JSON text mixed with expected JSON output).
            // Uses a fixed message to avoid leaking raw output content.
            JsonException =>
                (ErrorCodes.CommandFailed, "Failed to parse command output. PowerShell may have produced non-JSON output.", (object?)null),

            // NotImplementedException → INVALID_PARAMETER (defense-in-depth).
            // A tool catalog entry without a registered handler falls through to a stub
            // that throws NotImplementedException. Surface this as INVALID_PARAMETER so
            // callers see a stable, actionable error rather than INTERNAL_ERROR.
            // See GitHub Issue #56.
            NotImplementedException =>
                (ErrorCodes.InvalidParameter, "The requested tool is not implemented in this build.", (object?)null),

            // Catch-all for unmapped exceptions → INTERNAL_ERROR.
            // Never expose raw exception messages for unknown types — they may contain
            // secrets, credentials, connection strings, or internal implementation details.
            // See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6.
            _ => (RuntimeErrorCodes.InternalError, "An internal error occurred.", (object?)null),
        };

        var response = new McpToolResponse
        {
            Success = false,
            Error = message,
            ErrorCode = errorCode,
            Data = data,
            State = vmState
        };

        // Issue #164 / LF-D17: attach the structured rollback details block.
        // Kept out of the switch-tuple because the tuple shape only supports `data`,
        // and `details` is a separate envelope field (additive to `data` on success
        // responses; here it carries the rollback post-condition for AC#2).
        if (ex is VmCreateRollbackException vmrEx)
        {
            response.Details = new
            {
                vmName = vmrEx.VmName,
                phase = vmrEx.Phase,
                rollback = new
                {
                    performed = vmrEx.Rollback.Performed,
                    succeeded = vmrEx.Rollback.Succeeded,
                    elapsedMs = vmrEx.Rollback.ElapsedMs,
                    residualArtifacts = vmrEx.Rollback.ResidualArtifacts,
                },
            };
        }

        return response;
    }

    /// <summary>
    /// Case-insensitive <c>Contains</c> check across <paramref name="needles"/>
    /// against the exception's message. Used to classify
    /// <see cref="InvalidOperationException"/> instances surfaced by
    /// <see cref="IPowerShellDirectChannel"/> consumers (FileTransferService,
    /// CommandExecutor) into the most specific existing error code.
    /// Issue #52, ST-6.
    /// </summary>
    private static bool ChannelMessageContains(Exception ex, params string[] needles)
    {
        var message = ex.Message;
        if (string.IsNullOrEmpty(message))
            return false;
        foreach (var needle in needles)
        {
            if (message.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Issue #209 (sub-finding) / VC-SO-D5: composes a non-empty, descriptive
    /// error string for <see cref="SessionOpenFailedException"/>. Falls back
    /// to a synthetic message when <c>ex.Message</c> is empty/whitespace, and
    /// appends the inner-exception message when non-empty and not already a
    /// substring of <c>ex.Message</c>.
    /// </summary>
    private static string ComposeSessionOpenFailedError(SessionOpenFailedException ex)
    {
        var primary = string.IsNullOrWhiteSpace(ex.Message)
            ? $"PSDirect session open failed for VM {ex.VmId}: see logs"
            : ex.Message;

        var inner = ex.InnerException?.Message;
        if (!string.IsNullOrWhiteSpace(inner) &&
            primary.IndexOf(inner, StringComparison.Ordinal) < 0)
        {
            return $"{primary} -- {inner}";
        }
        return primary;
    }

    /// <summary>
    /// Builds a safe error message for ArgumentException variants.
    /// Includes the parameter name (user-supplied identifier) but not the full
    /// raw message which may contain internal validation logic details.
    /// </summary>
    private static string SafeArgumentMessage(ArgumentException ex)
    {
        // Strip the trailing "(Parameter 'foo')" suffix that ArgumentException appends
        // to ex.Message when a ParamName is present, so we don't duplicate the parameter
        // name in the surfaced error.
        var rawMessage = ex.Message ?? string.Empty;
        if (!string.IsNullOrEmpty(ex.ParamName))
        {
            var suffix = $" (Parameter '{ex.ParamName}')";
            if (rawMessage.EndsWith(suffix, StringComparison.Ordinal))
                rawMessage = rawMessage.Substring(0, rawMessage.Length - suffix.Length);
        }

        if (!string.IsNullOrWhiteSpace(ex.ParamName))
        {
            if (!string.IsNullOrWhiteSpace(rawMessage))
                return $"Invalid parameter '{ex.ParamName}': {rawMessage}";
            return $"Invalid parameter: '{ex.ParamName}'.";
        }

        // No ParamName: do NOT forward ex.Message — it may contain secrets
        // (connection strings, passwords) from internal validation helpers or
        // third-party code. Return a fixed sanitized string. See issue #56.
        return "A required parameter is missing or invalid.";
    }

    /// <summary>
    /// Redacts credential-related content from stderr or script text to prevent
    /// accidental leakage of secrets in error responses.
    /// Replaces:
    /// - The literal password string with ***REDACTED***
    /// - ConvertTo-SecureString argument values with ConvertTo-SecureString ***REDACTED***
    /// - -Credential parameter inline values with [PSCredential]
    ///
    /// See /myplans/security/credentials/credentials-design.md — Phase 1: Credential Redaction.
    /// See GitHub Issue #20.
    /// </summary>
    internal static string RedactCredentials(string text, string? password)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = text;

        // Redact the literal password if provided.
        if (!string.IsNullOrEmpty(password))
        {
            result = result.Replace(password, "***REDACTED***");
        }

        // Redact ConvertTo-SecureString argument values.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"ConvertTo-SecureString\s+'[^']*'",
            "ConvertTo-SecureString ***REDACTED***");

        // Redact -Credential parameter inline values.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"-Credential\s+\S+",
            "-Credential [PSCredential]");

        return result;
    }

    /// <summary>
    /// Issue #203 / VC-DUP-D4: returns true when <paramref name="message"/> carries
    /// the canonical Hyper-V <c>New-VM</c> / <c>Get-VM</c> name-collision substring
    /// (case-insensitive "already exists"). Guards against the
    /// <c>BASE_IMAGE_MUTATED</c> false-positive (which contains other substrings
    /// but not "already exists").
    /// </summary>
    internal static bool IsNameCollisionMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        if (message.Contains("BASE_IMAGE_MUTATED", StringComparison.Ordinal))
            return false;
        if (!message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return false;

        // Issue #203 / IA-Gate 5 fix: require co-occurrence with a VM-specific
        // token so non-VM "already exists" messages (e.g., the
        // ImageCopyFailedException "Destination image file already exists at
        // '<path>'." surfaced by vm_create_base_image) are NOT misclassified
        // as VM_ALREADY_EXISTS. Canonical Hyper-V collision text from the
        // primary script is "VM with name '<name>' already exists" (see
        // HyperVManager script literal). The Get-VM / New-VM cmdlet names
        // also disambiguate genuine VM-name collisions surfaced from PS.
        return message.Contains("VM with name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Get-VM", StringComparison.OrdinalIgnoreCase)
            || message.Contains("New-VM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Issue #203 / VC-DUP-D5: strip PowerShell positional / category / stack-tail
    /// tokens from a wire-bound error message so the envelope's <c>error</c>
    /// field never carries script paths, line/char positions, or
    /// <c>RuntimeException</c> stack text. Preserves the human-readable failure
    /// summary at the front of the string. Raw text remains visible to operators
    /// via <c>LogDebug</c> at the throw site (see HyperVManager LF-D19 / VC-DUP-D4
    /// branches).
    ///
    /// Patterns stripped (case-insensitive, line-anchored where appropriate):
    /// <list type="bullet">
    /// <item><description><c>At &lt;path&gt;:&lt;line&gt; char:&lt;col&gt;</c></description></item>
    /// <item><description><c>+ ~~~</c> caret-pointer lines (PS error indicator)</description></item>
    /// <item><description><c>CategoryInfo : ...</c></description></item>
    /// <item><description><c>FullyQualifiedErrorId : ...</c></description></item>
    /// <item><description><c>RuntimeException</c> stack-tail noise</description></item>
    /// </list>
    /// </summary>
    internal static string SanitizePowerShellErrorText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = text;

        // "At C:\path\to\script.ps1:42 char:7" (and any other path form).
        // IA-Gate 10 / Copilot review fix: previous \S+ pattern stopped at the
        // first whitespace and would leak Windows paths containing spaces
        // (e.g. "At C:\Program Files\foo\bar.ps1:14 char:5"). Use a
        // line-anchored greedy match so the entire "At ... char:N" run is
        // stripped regardless of embedded spaces in the path.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"^\s*At\s+.+?:\d+\s+char:\d+\s*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Defence-in-depth: also strip the same token when it appears
        // mid-line (legacy compact PS error formatting), still matching the
        // full path-with-spaces case.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"At\s+.+?:\d+\s+char:\d+",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Caret-pointer lines: "+ ~~~~~~~~~~~~~~~~~~~~~~~" and the "+ <code>" prefix line.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"^\s*\+\s*~+\s*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"^\s*\+\s*CategoryInfo\s*:[^\r\n]*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"^\s*\+\s*FullyQualifiedErrorId\s*:[^\r\n]*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Bare CategoryInfo / FullyQualifiedErrorId (no "+ " prefix variant).
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"CategoryInfo\s*:[^\r\n]*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"FullyQualifiedErrorId\s*:[^\r\n]*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // RuntimeException stack-tail noise (and subsequent stack frames).
        // IA-Gate 10 / Copilot review fix: the previous regex stripped only
        // the single RuntimeException header line and left subsequent
        // "   at System.Management.Automation.Internal..." stack frames
        // intact on the wire. Strip the header AND every following stack
        // frame line (the "at <Namespace>..." pattern emitted by .NET).
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"System\.Management\.Automation\.RuntimeException[^\r\n]*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Strip ".NET stack-frame" lines: "   at Namespace.Type.Method(...)"
        // (case-sensitive on "at " keyword, line-anchored).
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"^\s*at\s+[A-Za-z_][\w\.]*\.[A-Za-z_][\w]*[^\r\n]*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // Collapse the runs of whitespace / blank lines we just punched out.
        result = System.Text.RegularExpressions.Regex.Replace(result, @"[ \t]+", " ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"(\r?\n)\s*(\r?\n)+", "\n");
        result = result.Trim();

        return result;
    }
}
