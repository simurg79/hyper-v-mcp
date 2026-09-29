using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Text;
using System.Text.Json;
using HyperV.Mcp.Server.Models;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Maps internal exceptions to MCP response envelope shapes.
/// See internal documentation — MCP-D6: Exceptions caught and wrapped.
/// See internal documentation — Error Code Taxonomy.
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
    /// See internal documentation — Error Code Taxonomy.
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

                // The wrapper carries only raw platform text, so here the composed
                // credential-rejection message MUST win — it is the point of the AUTH_FAILED
                // envelope, and it is built from already-redacted text at the throw site.
                // See internal documentation - GCR-D4.
                // Every typed session-open inner exception MUST keep the recursively composed
                // inner error: that result is the only one that has been through the compose,
                // sanitize and bound pipeline. The wrapper message is password-redacted but
                // neither bounded nor session-open sanitized, so letting it win would bypass the
                // 4,000-character limit, decisive-cause retention, script exclusion and the
                // omission marker on the real PSDirect path.
                // See internal documentation — SOE-D3.
                var preferInnerMessage =
                    inner is SessionOpenFailedException or SshSessionOpenException;

                return new McpToolResponse
                {
                    Success = false,
                    Error = preferInnerMessage || string.IsNullOrWhiteSpace(channelEx.Message)
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
            VmLookupFailedException lookupFailure =>
                (ErrorCodes.CommandFailed, VmLookupFailedException.SafeMessage,
                 (object?)new
                 {
                     exitCode = lookupFailure.ExitCode,
                     stderr = RedactCredentials(lookupFailure.Stderr, password: null),
                 }),

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
            InvalidOperationException ioNameEx when ioNameEx is not LinuxProvisionFailedException
                && IsNameCollisionMessage(ioNameEx.Message) =>
                (ErrorCodes.VmAlreadyExists,
                 "A VM with the requested name already exists on the target host.",
                 (object?)null),

            ConcurrencyLimitException =>
                (ErrorCodes.ConcurrencyLimit, "The operation was rejected because the concurrency limit has been reached. Retry later.", (object?)null),

            ToolNotFoundException toolEx =>
                (RuntimeErrorCodes.ToolNotFound, $"Tool '{toolEx.ToolName}' is not registered.", (object?)null),

            // CommandTimeoutException includes partial output in the data field (ADR-9).
            // See internal documentation — CMD-D4: Timeout returns success:false with partial output.
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

            // Composed at the throw site with direction, VM and failing path, so the message is
            // forwarded rather than replaced by the fixed text below — that replacement is what
            // discarded the context. MUST precede the plain FileNotFoundException arm (it extends
            // that type), and MUST stay narrow: the plain arm's fixed text still protects every
            // non-transfer caller, whose messages were never proven wire-safe.
            // See internal documentation — FR-ERR-5.
            GuestTransferPathNotFoundException transferMissingEx =>
                (ErrorCodes.FileNotFound,
                 SanitizePowerShellErrorText(
                     RedactCredentials(transferMissingEx.Message, password: null)),
                 (object?)null),

            // FileNotFoundException maps to FILE_NOT_FOUND per the error code taxonomy.
            FileNotFoundException =>
                (ErrorCodes.FileNotFound, "The specified file was not found.", (object?)null),

            // Operational/domain failure mappings — prevents over-broad INTERNAL_ERROR usage.

            // ArgumentException (and ArgumentNullException, ArgumentOutOfRangeException) → INVALID_PARAMETER.
            // These represent validation failures from tool argument parsing.
            // Safe to include parameter name (user-supplied), but not the full message which
            // may contain internal validation details.
            // See internal documentation — Error Code Taxonomy: INVALID_PARAMETER.
            ArgumentException argEx =>
                (ErrorCodes.InvalidParameter, SafeArgumentMessage(argEx), (object?)null),

            // Same posture as the transfer path-not-found arm above: the composed message is the
            // only carrier of direction, VM and failing path, and it MUST precede the plain
            // UnauthorizedAccessException arm, which keeps its fixed text for host-side callers.
            // See internal documentation — FR-ERR-5.
            GuestTransferAccessDeniedException transferDeniedEx =>
                (ErrorCodes.AuthFailed,
                 SanitizePowerShellErrorText(
                     RedactCredentials(transferDeniedEx.Message, password: null)),
                 (object?)null),

            // UnauthorizedAccessException → AUTH_FAILED.
            // Covers PowerShell Direct (PSSession) credential failures and permission issues.
            // Raw message may contain server names, paths, or credential hints — use fixed message.
            // See internal documentation — Error Code Taxonomy: AUTH_FAILED.
            UnauthorizedAccessException =>
                (ErrorCodes.AuthFailed, "Authentication failed or access was denied.", (object?)null),

            // TimeoutException → COMMAND_TIMEOUT.
            // Generic timeout from System.TimeoutException (e.g., session-acquisition timeout
            // when establishing a PSSession to the guest via PowerShell Direct).
            // See internal documentation — CMD-D4.
            TimeoutException =>
                (ErrorCodes.CommandTimeout, "The operation timed out.", (object?)null),

            // CheckpointFailedException → CHECKPOINT_FAILED. Must precede the generic
            // InvalidOperationException arm (it extends that type).
            //
            // Issue #125 / CPF-D1 + CPF-D2: forward the sanitized diagnostic (exit code +
            // stderr) AND keep the vmId/hostId/checkpointName identifiers so the failure is
            // no longer masked. errorCode stays CHECKPOINT_FAILED.
            // See internal documentation
            CheckpointFailedException cpEx =>
                (ErrorCodes.CheckpointFailed,
                 ComposeCheckpointFailedError(cpEx),
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

            // Password-bearing vm_create outcomes. BOTH extend InvalidOperationException, so they
            // MUST be matched above the generic catch-all or they surface as COMMAND_FAILED.
            // Messages are fixed text plus the VM name — never the password or a derived form.
            // See internal documentation — VCAP-D19,
            // and internal documentation — FR-16.
            BaseImageNotGeneralizedException baseGenEx =>
                (ErrorCodes.BaseNotGeneralized, baseGenEx.Message, (object?)null),

            ReadinessNotReachedException readinessEx =>
                (ErrorCodes.ReadinessNotReached, readinessEx.Message, (object?)null),

            // The recorded loop-exit reason is the only classifier: divergence is permanently
            // unsatisfiable, an elapsed wait leaves the outcome unknown, and live runs produced the
            // same observed state for both — so the state token cannot decide.
            // Extends InvalidOperationException, so this arm MUST stay above the generic catch-all.
            // See internal documentation — LF-D34, LF-D36.
            VmStateConflictException pauseConflictEx =>
                (pauseConflictEx.PauseOutcome == PauseOutcomes.ConflictingTerminalState
                    ? ErrorCodes.VmStateConflict
                    : ErrorCodes.CommandFailed,
                 pauseConflictEx.Message,
                 (object?)null),

            // ISO installation typed exceptions → ISO-specific error codes.
            // Must be matched BEFORE InvalidOperationException since they extend it.
            // See internal documentation — Error Handling.
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

            // ── Issue #208 / ISO-D27: Ubuntu (Linux) terminal + precondition codes ──
            // MUST sit ABOVE the generic InvalidOperationException catch-all (they extend it).
            // Messages are fixed/safe — the KVP grammar carries no secrets, so forwarding cannot
            // leak credentials.
            // See internal documentation — ISO-D27.
            LinuxInstallTimeoutException linuxTimeoutEx =>
                (ErrorCodes.LinuxInstallTimeout, linuxTimeoutEx.Message, (object?)null),

            LinuxProvisionFailedException linuxProvisionEx =>
                (ErrorCodes.LinuxProvisionFailed, linuxProvisionEx.Message, (object?)null),

            LinuxPreconditionUnmetException linuxPreconditionEx =>
                (ErrorCodes.LinuxPreconditionUnmet, linuxPreconditionEx.Message, (object?)null),

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
            // See internal documentation — Phase 1.
            // See GitHub Issue #20.
            MissingCredentialsException credEx =>
                (ErrorCodes.MissingCredentials, credEx.Message, (object?)null),

            // IoOperationFailedException → IO_ERROR.
            // Used when a configured filesystem path exists but cannot be read, enumerated,
            // or written due to permissions, locking, or other I/O failure (ST-D7).
            // The exception message is operator-facing diagnostic text built at the throw
            // site (e.g., HyperVManager.ListImagesAsync) and is safe to forward — it carries
            // user-configured paths, not credentials.
            // See internal documentation — Error Code Taxonomy: IO_ERROR.
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
            // Issue #209 / LGS-SSH-D5: SSH connect/session-open failure against a Linux
            // guest classifies as SESSION_FAILED, mirroring the PSDirect posture above.
            // The message is already credential-redacted by SshSessionStore; no new code.
            // LGS-SSH-D6: apply a FINAL defensive redaction pass on the open-failure message
            // (password + SSH private-key/PEM/secure-string material) before it reaches the
            // SESSION_FAILED envelope, so no key material can escape via the error string even
            // if the throw site missed something.
            // A connection lost mid-transfer is a session fault, so it must classify as
            // SESSION_FAILED rather than TRANSFER_FAILED. It extends InvalidOperationException, so
            // this arm MUST stay above the generic catch-all. The composed message carries the
            // direction, VM and failing path the caller needs.
            // See internal documentation — FR-ERR-5.
            GuestConnectionLostException connectionLostEx =>
                (ErrorCodes.SessionFailed,
                 SanitizePowerShellErrorText(
                     RedactCredentials(connectionLostEx.Message, password: null)),
                 (object?)null),

            SshSessionOpenException sshOpenEx =>
                (ErrorCodes.SessionFailed,
                 BoundSessionOpenError(
                     "Open SSH guest session",
                     sshOpenEx.VmId,
                     ErrorCodes.SessionFailed,
                     SanitizeSessionOpenErrorText(
                         StderrSpillHelper.RedactDefensively(
                             RedactCredentials(ComposeSshSessionOpenError(sshOpenEx), password: null))),
                     sshOpenEx.SpillSummary,
                     sshOpenEx.DecisiveCause),
                 (object?)null),

            // Reuses the SshSessionOpenException → SESSION_FAILED path; no new error code. Both
            // refusal conditions share this arm, so the MESSAGE is what tells them apart.
            // See internal documentation — LGR-D30, LGR-D35.
            GuestRoutingUnavailableException guestRoutingEx =>
                (ErrorCodes.SessionFailed,
                 string.IsNullOrWhiteSpace(guestRoutingEx.Message)
                    ? "No transport could be selected for the guest."
                    : guestRoutingEx.Message,
                 (object?)null),

            // Guarded arm MUST stay immediately above the unconditional one below, and both
            // MUST stay above the "cannot find path" FILE_NOT_FOUND arm — the #204 ordering
            // hazard. Pinned by Issue266GuestCredentialRejectionTests.
            // See internal documentation — GCR-D3.
            SessionOpenFailedException soCredEx when soCredEx.CredentialRejected =>
                (ErrorCodes.AuthFailed,
                 ComposeGuestCredentialRejectedError(soCredEx),
                 (object?)null),

            SessionOpenFailedException soEx =>
                (ErrorCodes.SessionFailed,
                 BoundSessionOpenError(
                     "Open PowerShell Direct guest session",
                     soEx.VmId,
                     ErrorCodes.SessionFailed,
                     SanitizeSessionOpenErrorText(ComposeSessionOpenFailedError(soEx)),
                     soEx.SpillSummary,
                     soEx.DecisiveCause),
                 (object?)null),

            // VC-SO-D4: substring defense-in-depth. Catches any code path
            // that surfaces a New-PSSession failure as a plain
            // InvalidOperationException (i.e. bypassed the typed wrap).
            // Mirrors the existing ChannelMessageContains pattern from ST-6.
            // MUST also sit above the path-not-found arm.
            InvalidOperationException when ChannelMessageContains(ex,
                "PSSessionOpenFailed",
                "vmhypervsocketclient",
                "new-pssession") =>
                (ErrorCodes.SessionFailed,
                 ComposeUntypedSessionOpenFallback(ex),
                 (object?)null),

            // ── Issue #52, ST-6: PowerShell Direct channel failure-mode mappings ──
            // FileTransferService / CommandExecutor surface guest-side failures as
            // InvalidOperationException carrying the channel's already-redacted stderr.
            // We pattern-match on substrings (case-insensitive) of the message to map
            // them to the most specific existing error code BEFORE falling through to
            // the generic InvalidOperationException → COMMAND_FAILED branch.
            // See internal documentation

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
            // See internal documentation — Error Code Taxonomy: COMMAND_FAILED.
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
            // See internal documentation — Error Code Taxonomy: TRANSFER_FAILED.
            // Composed at the throw site with direction, VM and failing path already redacted, so
            // the message is forwarded rather than replaced by the fixed IOException text below —
            // that replacement is what dropped the caller-facing context.
            // See internal documentation — FR-ERR-5.
            GuestTransferFailedException transferEx =>
                (ErrorCodes.TransferFailed,
                 SanitizePowerShellErrorText(RedactCredentials(transferEx.Message, password: null)),
                 (object?)null),

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
            // See internal documentation — MCP-D6.
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

        // Issue #125 / CPF-D1 + CPF-D2: also expose the diagnostic and identifiers as a
        // structured `details` block so machine consumers need not parse `error`.
        // Kept out of the switch-tuple because the tuple only supports `data`.
        // A Ubuntu provisioning failure names its failing step and carries the sanitized, capped
        // cause so callers need not parse the message. The authoring route is deliberately NOT
        // projected here — it stays host-side only.
        // See internal documentation — ISO-D35 / ISO-D37.
        if (ex is LinuxProvisionFailedException linuxDetailEx)
        {
            // `cause` is absent rather than null when there is nothing to surface, so consumers can
            // test for presence instead of distinguishing null from missing.
            response.Details = linuxDetailEx.Cause is null
                ? new
                {
                    vmId = linuxDetailEx.VmId,
                    vmName = linuxDetailEx.VmName,
                    failingStep = linuxDetailEx.FailingStep,
                    causeTruncated = linuxDetailEx.CauseTruncated,
                }
                : (object)new
                {
                    vmId = linuxDetailEx.VmId,
                    vmName = linuxDetailEx.VmName,
                    failingStep = linuxDetailEx.FailingStep,
                    cause = linuxDetailEx.Cause,
                    causeTruncated = linuxDetailEx.CauseTruncated,
                };
        }

        // The attached media and channel state also reach the caller structurally, so an agent need
        // not parse prose. `attachedIsoPath` is absent rather than null when it could not be read,
        // matching the Linux provisioning block's presence convention above.
        // See internal documentation
        // — UMD-D8.
        if (ex is LinuxInstallTimeoutException linuxTimeoutDetailEx)
        {
            var channelState = linuxTimeoutDetailEx.ChannelState.ToString();
            response.Details = linuxTimeoutDetailEx.AttachedIsoPath is null
                ? new
                {
                    vmId = linuxTimeoutDetailEx.VmId,
                    vmName = linuxTimeoutDetailEx.VmName,
                    timeoutMinutes = linuxTimeoutDetailEx.TimeoutMinutes,
                    attachedIsoPathDetermined = false,
                    guestCompletionChannelState = channelState,
                }
                : (object)new
                {
                    vmId = linuxTimeoutDetailEx.VmId,
                    vmName = linuxTimeoutDetailEx.VmName,
                    timeoutMinutes = linuxTimeoutDetailEx.TimeoutMinutes,
                    attachedIsoPathDetermined = true,
                    attachedIsoPath = linuxTimeoutDetailEx.AttachedIsoPath,
                    guestCompletionChannelState = channelState,
                };
        }

        // Lets the caller branch on `pauseOutcome` without parsing prose, independently of the
        // error code. See internal documentation — LF-D36.
        if (ex is VmStateConflictException pauseDetailEx)
        {
            response.Details = new
            {
                pauseOutcome = pauseDetailEx.PauseOutcome,
                observedState = pauseDetailEx.ObservedState,
            };
        }

        if (ex is CheckpointFailedException cpDetailEx)
        {
            response.Details = new
            {
                vmId = cpDetailEx.VmId,
                hostId = cpDetailEx.HostId,
                checkpointName = cpDetailEx.CheckpointName,
                diagnostic = SanitizeCheckpointDiagnostic(cpDetailEx.Message),
            };
        }

        return response;
    }

    /// <summary>
    /// Issue #125 / CPF-D1 + CPF-D2: builds the wire <c>error</c> string for a
    /// <see cref="CheckpointFailedException"/> — an identifier summary (vmId/hostId,
    /// plus checkpointName when present, CPF-D2) followed by the sanitized diagnostic
    /// (exit code + stderr, CPF-D1). Returns the summary alone when the diagnostic is
    /// empty after sanitization.
    /// See internal documentation
    /// </summary>
    private static string ComposeCheckpointFailedError(CheckpointFailedException ex)
    {
        var summary =
            $"Checkpoint operation failed for VM '{ex.VmId}' on host '{ex.HostId}'." +
            (ex.CheckpointName != null ? $" Checkpoint: '{ex.CheckpointName}'." : "");

        var diagnostic = SanitizeCheckpointDiagnostic(ex.Message);
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return summary;
        }

        return $"{summary} {diagnostic}";
    }

    /// <summary>
    /// Issue #125 / CPF-D1 / Constraint #4: sanitizes the checkpoint message with the
    /// same credential-redaction + PS-noise chain the generic
    /// <see cref="InvalidOperationException"/> arm uses, so secrets and positional
    /// tokens never reach the wire. <c>internal</c> so the CPF-D6 host-side log path in
    /// <see cref="CheckpointManager.HandleError"/> reuses it and stays no weaker than
    /// the client envelope.
    /// </summary>
    internal static string SanitizeCheckpointDiagnostic(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }
        return SanitizePowerShellErrorText(RedactCredentials(message, password: null));
    }

    /// <summary>
    /// Whole-token check across <paramref name="needles"/> against the exception's message.
    /// Used to classify <see cref="InvalidOperationException"/> instances surfaced by
    /// <see cref="IPowerShellDirectChannel"/> consumers (FileTransferService,
    /// CommandExecutor) into the most specific existing error code.
    /// Issue #52, ST-6.
    /// </summary>
    /// <remarks>
    /// MUST match whole tokens, not raw substrings: needles such as <c>cannot find path</c> or
    /// <c>access is denied</c> otherwise match prose embedded in a diagnostic blob and
    /// misclassify the failure (issue #289). See
    /// internal documentation — SOE-D7.
    /// </remarks>
    private static bool ChannelMessageContains(Exception ex, params string[] needles)
    {
        var message = ex.Message;
        if (string.IsNullOrEmpty(message))
            return false;
        foreach (var needle in needles)
        {
            if (TokenMatcher.ContainsPhrase(message, needle))
                return true;
        }
        return false;
    }

    /// <summary>Consumer-visible upper bound on a session-open <c>error</c> string, in characters.</summary>
    /// <remarks>
    /// Public behaviour: this value and <see cref="DecisiveCauseMaxChars"/> are fixed by
    /// internal documentation and MUST NOT be
    /// relaxed without amending that spec.
    /// </remarks>
    internal const int SessionOpenErrorMaxChars = 4000;

    /// <summary>Characters of the decisive cause statement reproduced verbatim.</summary>
    internal const int DecisiveCauseMaxChars = 1000;

    /// <summary>
    /// Cause nouns and states — the vocabulary that actually names why a session-open failed.
    /// See internal documentation — SOE-D11.
    /// </summary>
    private static readonly string[] CauseSignalTierA =
    {
        "denied", "unauthorized", "unauthorised", "forbidden", "credential", "credentials",
        "password", "logon", "authentication", "unreachable", "refused", "timeout", "timed",
        "expired", "unavailable", "offline", "disconnected", "terminated", "reset", "handshake",
        "certificate", "permission", "privilege", "disabled", "locked", "corrupt", "unsupported",
        "incompatible", "exceeded", "quota", "rejected", "nonexistent",
    };

    /// <summary>
    /// Modality and negation — meaningful only in company, so weighted below Tier A to keep a
    /// purely modal segment from outranking one that names a cause.
    /// </summary>
    private static readonly string[] CauseSignalTierB =
    {
        "cannot", "unable", "not", "no", "failed", "failure", "invalid", "missing", "required",
        "blocked", "busy",
    };

    private static readonly HashSet<string> CauseSignalTierASet =
        new(CauseSignalTierA, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CauseSignalTierBSet =
        new(CauseSignalTierB, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bounds a session-open failure message to <see cref="SessionOpenErrorMaxChars"/> while
    /// reproducing the decisive cause statement verbatim wherever it occurs in the reported cause.
    /// </summary>
    /// <remarks>
    /// Selection is salience-ranked, never positional: position-based truncation that drops a
    /// late-stated cause is non-conforming, and head-only, tail-only and head+tail strategies all
    /// fail the acceptance criterion by construction. Applied at this seam rather than in
    /// <c>SessionStore</c> because this is the one place both the PSDirect and the SSH channel
    /// become envelope text. See
    /// internal documentation — SOE-D3, SOE-D4.
    /// </remarks>
    internal static string BoundSessionOpenError(
        string operation,
        string? vmId,
        string errorCode,
        string reportedCause,
        string? spillSummary,
        string? structuralDecisiveCause = null)
    {
        var cause = reportedCause ?? string.Empty;

        // The untyped defense-in-depth arm reaches this with no VM id; the identifier is framing,
        // not evidence, so its absence must not divert composition.
        var namedVm = string.IsNullOrWhiteSpace(vmId) ? "(unknown)" : vmId!;
        var core =
            $"{operation} failed for VM '{namedVm}' ({errorCode}). Decisive cause: ";

        // FR-5a is absolute regardless of aggregate text length: a fitting authoritative cause must
        // reach the caller verbatim. Returning short aggregate text unconditionally used to route
        // around one, so this shortcut applies only when that text already reproduces it verbatim.
        var capturedCause = HasCapturedDecisiveCause(structuralDecisiveCause)
            ? structuralDecisiveCause!
            : null;

        // FR-4 framing is a contract, not a length-dependent embellishment: without it a short
        // cause that never names the VM (common for PSRemoting/WinRM/SSH transport text) reopens
        // the #294 cross-VM ambiguity. Only the omission marker and salience narrowing are
        // conditional on the text not fitting.
        if (core.Length + cause.Length <= SessionOpenErrorMaxChars
            && (capturedCause is null || cause.IndexOf(capturedCause, StringComparison.Ordinal) >= 0))
        {
            return core + cause;
        }

        var framingVocabulary = BuildFramingVocabulary(operation, namedVm, errorCode);
        return BoundWithActionableCore(core, cause, spillSummary, framingVocabulary, structuralDecisiveCause);
    }

    /// <summary>
    /// Whether a supplied authoritative cause qualifies for the captured path.
    /// </summary>
    /// <remarks>
    /// FR-5a's absolute guarantee applies only within the statement allowance. An over-long
    /// authoritative value is NOT narrowed in place — that would be a third behaviour the spec does
    /// not define — but falls through to the FR-5b recovered path over the whole reported text. See
    /// internal documentation — FR-5a, FR-5b.
    /// </remarks>
    private static bool HasCapturedDecisiveCause(string? structuralDecisiveCause) =>
        !string.IsNullOrWhiteSpace(structuralDecisiveCause)
        && structuralDecisiveCause!.Length <= DecisiveCauseMaxChars;

    /// <summary>
    /// Bounds <paramref name="reportedCause"/> beneath an already-composed actionable core
    /// (the credential-rejection arm's guidance), which is never itself elided.
    /// See internal documentation — SOE-D3.
    /// </summary>
    private static string BoundWithActionableCore(
        string actionableCore,
        string reportedCause,
        string? spillSummary,
        HashSet<string> framingVocabulary,
        string? structuralDecisiveCause = null)
    {
        var segments = SplitIntoCauseSegments(reportedCause);

        // A cause captured at the throw site is authoritative: no need to re-infer it from flattened
        // prose, which the zero-signal case cannot do. Used exactly as supplied — trimming would
        // forfeit the verbatim delivery FR-5a requires for leading or trailing whitespace.
        var hasCapturedCause = HasCapturedDecisiveCause(structuralDecisiveCause);
        var decisive = hasCapturedCause
            ? structuralDecisiveCause!
            : SelectDecisiveCauseSegment(segments, framingVocabulary);

        if (!hasCapturedCause && decisive.Length > DecisiveCauseMaxChars)
        {
            // MUST NOT be a head/tail cut: a cause stated late inside one delimiter-free segment
            // would be removed verbatim. Narrow by salience over the whole segment instead.
            decisive = SelectSalientWindow(decisive, DecisiveCauseMaxChars, framingVocabulary);
        }

        const string contextPrefix = " Context: ";
        var context = string.Join(" ", segments.Where(segment =>
            segment.IndexOf(decisive, StringComparison.Ordinal) < 0));

        // The marker's FULL length is reserved before any other text is allocated, so the final
        // composition can never cut through it. See
        // internal documentation — SOE-D5.
        var marker = BuildOmissionMarker(reportedCause.Length, spillSummary);
        var budgetForText = SessionOpenErrorMaxChars - marker.Length;

        var head = actionableCore + decisive;
        if (head.Length > budgetForText)
        {
            // Only reachable when the actionable core alone approaches the bound; the decisive
            // span is preserved in preference to the framing prose.
            head = decisive.Length <= budgetForText
                ? decisive
                : SelectSalientWindow(decisive, Math.Max(0, budgetForText), framingVocabulary);
        }

        var keptContext = string.Empty;
        for (var pass = 0; pass < 3; pass++)
        {
            var room = SessionOpenErrorMaxChars - marker.Length - head.Length - contextPrefix.Length;
            keptContext = room <= 0
                ? string.Empty
                : context.Substring(0, Math.Min(room, context.Length));
            var reproduced = decisive.Length + keptContext.Length;
            marker = BuildOmissionMarker(Math.Max(0, reportedCause.Length - reproduced), spillSummary);
        }

        var composed = keptContext.Length > 0
            ? head + contextPrefix + keptContext + marker
            : head + marker;

        if (composed.Length <= SessionOpenErrorMaxChars)
        {
            return composed;
        }

        // Drop context — never the marker — to land inside the bound.
        composed = head + marker;
        return composed.Length <= SessionOpenErrorMaxChars
            ? composed
            : head.Substring(0, Math.Max(0, SessionOpenErrorMaxChars - marker.Length)) + marker;
    }

    /// <summary>
    /// Narrows an over-long span to <paramref name="maxChars"/> by picking the highest-salience
    /// window out of the complete set of fixed-width windows.
    /// </summary>
    /// <remarks>
    /// Admission, scoring and tie-break each have to be right, because a defect in any one of
    /// them loses the decisive statement on its own.
    ///
    /// Admission enumerates every start offset, so the window beginning at a statement's own
    /// first character is always a candidate whatever that character is — letter, quote, dash,
    /// bullet or combining mark alike. Deriving starts from a segmentation instead (fixed stride,
    /// token boundaries, or any other rule) permanently bisects the statements that begin between
    /// two derived starts.
    ///
    /// Scoring ranks a whole statement above a window that cuts one. Statement runs are swept
    /// alongside tokens and a window is credited only for the runs it reproduces end to end, so
    /// a window holding the decisive statement intact outranks a competitor that merely shares
    /// its cause token while bisecting it. The two are otherwise separated only by vocabulary
    /// counts, which is how a bisecting head-of-text window used to win.
    ///
    /// Scoring counts a token only where the window contains it whole. This is a deliberate
    /// change from tokenizing the candidate substring, which credited a window for fragments its
    /// own edges had cut: a fragment is not evidence that the window preserved that token, and
    /// crediting it let edge placement rather than content decide between windows.
    ///
    /// The tie-break is a fingerprint of the window's own characters, so equal-scoring candidates
    /// are separated by what they contain and never by where they were found. Retaining the
    /// earliest offset — which is what comparing nothing amounts to — is a positional preference
    /// and reintroduces the head bias the rest of the function removes. Candidates whose
    /// fingerprints also agree are, on collision-free content, the same string, so which one is
    /// kept is not observable in the result.
    ///
    /// The guarantee is relative to the statement grammar in
    /// <see cref="SplitIntoCauseSegments"/>: a run the grammar cannot see is not a statement this
    /// function can promise to keep whole. Exact preservation of a known cause does not rest on
    /// this heuristic at all — the throw site carries the decisive statement structurally and it
    /// is retained verbatim whenever it fits the budget, and only a structural value that is
    /// itself over budget is narrowed here.
    ///
    /// All of this stays affordable because candidates are never materialized: scores ride
    /// incrementally on the tokens and statement runs entering and leaving a constant-width
    /// window, the fingerprint rolls in constant time per offset, and only the winner is cut.
    /// Time is linear in the span and retained storage is the span's own tokens and runs plus the
    /// winner, so a large diagnostic cannot trigger the quadratic allocation that building every
    /// candidate string would.
    /// See internal documentation — SOE-D11.
    /// </remarks>
    private static string SelectSalientWindow(
        string span,
        int maxChars,
        HashSet<string> framingVocabulary)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }
        if (span.Length <= maxChars)
        {
            return span;
        }

        var tokens = ExtractTokenSpans(span);
        var statements = ExtractStatementSpans(span);
        var presentCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var distinctCount = 0;
        var tierACount = 0;
        var tierBCount = 0;
        var noveltyCount = 0;

        void Enter(string token)
        {
            presentCounts.TryGetValue(token, out var occurrences);
            presentCounts[token] = occurrences + 1;
            if (occurrences != 0)
            {
                return;
            }
            distinctCount++;
            if (CauseSignalTierASet.Contains(token)) tierACount++;
            if (CauseSignalTierBSet.Contains(token)) tierBCount++;
            if (!framingVocabulary.Contains(token)) noveltyCount++;
        }

        void Leave(string token)
        {
            var occurrences = presentCounts[token];
            if (occurrences > 1)
            {
                presentCounts[token] = occurrences - 1;
                return;
            }
            presentCounts.Remove(token);
            distinctCount--;
            if (CauseSignalTierASet.Contains(token)) tierACount--;
            if (CauseSignalTierBSet.Contains(token)) tierBCount--;
            if (!framingVocabulary.Contains(token)) noveltyCount--;
        }

        var firstInside = 0;
        var pastLastInside = 0;
        var firstStatementInside = 0;
        var pastLastStatementInside = 0;
        var wholeStatementChars = 0;

        var bestStart = 0;
        var bestWeight = -1;
        var bestTierACount = -1;
        var bestWholeStatementChars = -1;
        var bestNovelty = -1;
        var bestDistinctCount = -1;
        ulong bestFingerprint = 0;
        var lastStart = span.Length - maxChars;

        // Polynomial fingerprint of the window's characters, slid in constant time. Unchecked
        // wrap-around is intended: this is an order key over content, not a checksum.
        const ulong fingerprintBase = 1315423911UL;
        var leadingPower = 1UL;
        var fingerprint = 0UL;
        unchecked
        {
            for (var offset = 0; offset < maxChars; offset++)
            {
                fingerprint = (fingerprint * fingerprintBase) + span[offset];
                if (offset > 0)
                {
                    leadingPower *= fingerprintBase;
                }
            }
        }

        for (var start = 0; start <= lastStart; start++)
        {
            var windowEnd = start + maxChars;

            // Tokens do not overlap and are ordered by start, so both ends advance monotonically
            // and each token is entered and left at most once across the whole scan.
            while (pastLastInside < tokens.Count &&
                   tokens[pastLastInside].Start + tokens[pastLastInside].Text.Length <= windowEnd)
            {
                Enter(tokens[pastLastInside].Text);
                pastLastInside++;
            }
            while (firstInside < pastLastInside && tokens[firstInside].Start < start)
            {
                Leave(tokens[firstInside].Text);
                firstInside++;
            }

            // Statement runs are likewise disjoint and ordered, so the same two-pointer sweep
            // credits each run's length exactly while it is wholly inside the window.
            while (pastLastStatementInside < statements.Count &&
                   statements[pastLastStatementInside].End <= windowEnd)
            {
                wholeStatementChars += statements[pastLastStatementInside].Length;
                pastLastStatementInside++;
            }
            while (firstStatementInside < pastLastStatementInside &&
                   statements[firstStatementInside].Start < start)
            {
                wholeStatementChars -= statements[firstStatementInside].Length;
                firstStatementInside++;
            }

            var weight = (tierACount * 2) + tierBCount;

            // Whole-statement preservation outranks every vocabulary score. Ranking tier-A
            // density first let a window that merely mentions more signal words beat the window
            // that reproduces the decisive statement intact, which is the opposite of the
            // guarantee: a bisected statement is not a usable cause however rich it looks.
            // See internal documentation — SOE-D11.
            var better =
                wholeStatementChars > bestWholeStatementChars
                || (wholeStatementChars == bestWholeStatementChars && weight > bestWeight)
                || (wholeStatementChars == bestWholeStatementChars && weight == bestWeight
                    && tierACount > bestTierACount)
                || (wholeStatementChars == bestWholeStatementChars && weight == bestWeight
                    && tierACount == bestTierACount && noveltyCount > bestNovelty)
                || (wholeStatementChars == bestWholeStatementChars && weight == bestWeight
                    && tierACount == bestTierACount && noveltyCount == bestNovelty
                    && distinctCount > bestDistinctCount)
                || (wholeStatementChars == bestWholeStatementChars && weight == bestWeight
                    && tierACount == bestTierACount && noveltyCount == bestNovelty
                    && distinctCount == bestDistinctCount && fingerprint < bestFingerprint);

            if (start == 0 || better)
            {
                bestStart = start;
                bestWeight = weight;
                bestTierACount = tierACount;
                bestWholeStatementChars = wholeStatementChars;
                bestNovelty = noveltyCount;
                bestDistinctCount = distinctCount;
                bestFingerprint = fingerprint;
            }

            if (start < lastStart)
            {
                unchecked
                {
                    fingerprint = ((fingerprint - (leadingPower * span[start])) * fingerprintBase)
                        + span[windowEnd];
                }
            }
        }

        return span.Substring(bestStart, maxChars);
    }

    /// <summary>Maximal runs of token characters, in order, with their offsets.</summary>
    private static List<TokenSpan> ExtractTokenSpans(string text)
    {
        var spans = new List<TokenSpan>();
        var runStart = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (TokenMatcher.IsTokenChar(text[index]))
            {
                if (runStart < 0)
                {
                    runStart = index;
                }
            }
            else if (runStart >= 0)
            {
                spans.Add(new TokenSpan(runStart, text.Substring(runStart, index - runStart)));
                runStart = -1;
            }
        }
        if (runStart >= 0)
        {
            spans.Add(new TokenSpan(runStart, text.Substring(runStart)));
        }
        return spans;
    }

    private readonly record struct TokenSpan(int Start, string Text);

    private readonly record struct StatementSpan(int Start, int End)
    {
        public int Length => End - Start;
    }

    /// <summary>
    /// Statement runs, as maximal stretches between the terminators of the same grammar
    /// <see cref="SplitIntoCauseSegments"/> uses, trimmed of surrounding whitespace.
    /// </summary>
    /// <remarks>
    /// Offsets are kept rather than text so a window can be credited for reproducing a run whole
    /// without either side being materialized.
    /// </remarks>
    private static List<StatementSpan> ExtractStatementSpans(string text)
    {
        var spans = new List<StatementSpan>();
        var runStart = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var isTerminator = index == text.Length || IsStatementTerminator(text[index]);
            if (!isTerminator)
            {
                if (runStart < 0)
                {
                    runStart = index;
                }
                continue;
            }
            if (runStart < 0)
            {
                continue;
            }

            var start = runStart;
            var end = index;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end > start)
            {
                spans.Add(new StatementSpan(start, end));
            }
            runStart = -1;
        }
        return spans;
    }

    /// <summary>
    /// Clause boundaries recognized by both the span extractor and
    /// <see cref="SplitIntoCauseSegments"/>. Colon and the Unicode dashes are included because
    /// real diagnostics delimit the decisive clause with them at least as often as with a
    /// period ("Context: access denied — retry pending"); without them such a message is one
    /// undivided run and the decisive clause can never earn whole-statement credit.
    /// </summary>
    private static readonly char[] StatementTerminators =
        { '\n', '\r', '.', '!', '?', ';', ':', '\u2014', '\u2013' };

    private static bool IsStatementTerminator(char character) =>
        Array.IndexOf(StatementTerminators, character) >= 0;

    /// <summary>
    /// The in-text omission indication. Never dropped — a bounded message with no path to the full
    /// detail would trade an information-disclosure defect for an information-loss one. No new
    /// envelope field is introduced. See
    /// internal documentation — SOE-D5.
    /// </summary>
    /// <remarks>
    /// The spill reference is clamped to a compact identifier so an oversized summary can never
    /// consume the budget the marker itself needs.
    /// </remarks>
    private static string BuildOmissionMarker(int omittedCharacters, string? spillSummary)
    {
        if (string.IsNullOrWhiteSpace(spillSummary))
        {
            return $" [… omitted; {omittedCharacters} characters of diagnostic detail; no spill available]";
        }
        var reference = spillSummary!.Trim();
        if (reference.Length > SpillReferenceMaxChars)
        {
            reference = reference.Substring(0, SpillReferenceMaxChars);
        }
        return $" [… omitted; {omittedCharacters} characters of diagnostic detail; see server log spill {reference}]";
    }

    /// <summary>Upper bound on the spill identifier embedded in the omission marker.</summary>
    private const int SpillReferenceMaxChars = 120;

    /// <summary>
    /// Splits the reported cause on line breaks and sentence terminators.
    /// </summary>
    private static List<string> SplitIntoCauseSegments(string cause)
    {
        var segments = cause
            .Split(StatementTerminators, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToList();
        return segments.Count > 0 ? segments : new List<string> { cause.Trim() };
    }

    /// <summary>
    /// Picks the segment that states the cause, by content alone.
    /// </summary>
    /// <remarks>
    /// Position is consulted only as the final tie-break among segments already indistinguishable
    /// by content, and the all-zero-score branch falls to novelty — never to "first" or "last".
    /// See internal documentation — SOE-D11.
    /// </remarks>
    private static string SelectDecisiveCauseSegment(
        List<string> segments,
        HashSet<string> framingVocabulary)
    {
        string? best = null;
        var bestWeight = -1;
        var bestTierACount = -1;
        var bestNovelty = -1;
        var bestDistinctCount = -1;

        foreach (var segment in segments)
        {
            var distinctTokens = TokenizeDistinct(segment);
            var tierAMatches = CauseSignalTierA.Count(token => distinctTokens.Contains(token));
            var tierBMatches = CauseSignalTierB.Count(token => distinctTokens.Contains(token));
            var weight = (tierAMatches * 2) + tierBMatches;
            var novelty = distinctTokens.Count(token => !framingVocabulary.Contains(token));

            var better =
                weight > bestWeight
                || (weight == bestWeight && tierAMatches > bestTierACount)
                || (weight == bestWeight && tierAMatches == bestTierACount && novelty > bestNovelty)
                || (weight == bestWeight && tierAMatches == bestTierACount && novelty == bestNovelty
                    && distinctTokens.Count > bestDistinctCount)
                // Separate fully-tied segments by their own text rather than keeping whichever the
                // split yielded first, which would be a positional preference.
                || (weight == bestWeight && tierAMatches == bestTierACount && novelty == bestNovelty
                    && distinctTokens.Count == bestDistinctCount
                    && best is not null && string.CompareOrdinal(segment, best) < 0);

            if (best is null || better)
            {
                best = segment;
                bestWeight = weight;
                bestTierACount = tierAMatches;
                bestNovelty = novelty;
                bestDistinctCount = distinctTokens.Count;
            }
        }

        // The zero-score branch is reached through the same ladder above with every weight at
        // zero, so novelty then distinct-token count decide it. Nothing here consults position.
        return best ?? string.Empty;
    }

    private static HashSet<string> TokenizeDistinct(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new StringBuilder();
        foreach (var character in text)
        {
            if (TokenMatcher.IsTokenChar(character))
            {
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }
        return tokens;
    }

    /// <summary>
    /// Tokens the server itself contributed. A segment scores novelty only for vocabulary the
    /// server did not supply, so the framing text cannot win the zero-score branch.
    /// </summary>
    private static HashSet<string> BuildFramingVocabulary(string operation, string vmId, string errorCode)
    {
        var vocabulary = TokenizeDistinct($"{operation} {vmId} {errorCode}");
        foreach (var word in new[] { "failed", "for", "vm", "decisive", "cause", "context", "omitted", "characters", "diagnostic", "detail", "server", "log", "spill" })
        {
            vocabulary.Add(word);
        }
        return vocabulary;
    }

    /// <summary>
    /// Composes the wire text for the untyped session-open fallback: fixed prose, an
    /// allow-listed exception type name, and a spill identifier the operator can open.
    /// </summary>
    /// <remarks>
    /// The exception message is withheld from the wire AND from the spill. This arm catches throw
    /// sites never proven to redact, and it has no correlated request context, so the request
    /// password is not in scope here. A password-independent pass (<c>RedactCredentials(password:
    /// null)</c>, <c>RedactDefensively</c>) erases shaped patterns only; an arbitrary literal
    /// secret has no shape, so it survives. That argument holds identically at the disk sink as at
    /// the wire — spilling <c>ex.ToString()</c> would move a credential disclosure from the wire to
    /// rest rather than remove it.
    ///
    /// The spill therefore carries only structurally safe facets: the exception type chain, taken
    /// from loaded assembly metadata, and stack-free counts. No <c>Message</c>, no
    /// <c>StackTrace</c>, no <c>Data</c>, no <c>ToString()</c> — nothing on that record derives
    /// from caller-supplied text, so no caller-supplied password can reach disk by this path.
    /// See internal documentation — SOE-D3.
    /// </remarks>
    private static string ComposeUntypedSessionOpenFallback(Exception ex)
    {
        // Never let a diagnostic-capture failure replace the mapped error.
        string reference;
        try
        {
            var spilled = StderrSpillHelper.Spill(DescribeExceptionStructurally(ex));
            reference = spilled.Length > SpillReferenceMaxChars
                ? spilled.Substring(0, SpillReferenceMaxChars)
                : spilled;
        }
        catch
        {
            reference = "<unavailable>";
        }

        return "Open PowerShell Direct guest session failed (SESSION_FAILED). Exception type: "
            + DescribeTypeSafely(ex.GetType())
            + ". The underlying message is withheld from this envelope and from the server spill "
            + "because it reached the mapper untyped and could not be proven free of credential "
            + "material; the structural type chain is in server log spill " + reference + ".";
    }

    /// <summary>
    /// Renders an exception as type identity alone — never message, stack, or data.
    /// </summary>
    /// <remarks>
    /// Every emitted value comes from assembly metadata or a count, so this record is
    /// closed to caller-supplied text by construction. The inner chain is depth-bounded so a
    /// self-referential or pathologically deep chain cannot spin or bloat the spill.
    /// </remarks>
    private static string DescribeExceptionStructurally(Exception ex)
    {
        const int maxChainDepth = 16;
        var builder = new StringBuilder();
        builder.Append("Untyped session-open failure. Message and stack withheld: this boundary ")
            .Append("has no request credential, so arbitrary text cannot be proven password-free.")
            .Append(Environment.NewLine)
            .Append("Exception type chain (assembly metadata only):")
            .Append(Environment.NewLine);

        var current = (Exception?)ex;
        for (var depth = 0; current is not null && depth < maxChainDepth; depth++)
        {
            builder.Append("  [").Append(depth).Append("] ")
                .Append(DescribeTypeSafely(current.GetType()))
                .Append(" (hresult 0x").Append(current.HResult.ToString("X8"))
                .Append(", data entries ").Append(current.Data.Count)
                .Append(", has stack ").Append(current.StackTrace is not null)
                .Append(')')
                .Append(Environment.NewLine);

            if (current is AggregateException aggregate)
            {
                builder.Append("      aggregate inner count ")
                    .Append(aggregate.InnerExceptions.Count)
                    .Append(Environment.NewLine);
            }

            var next = current.InnerException;
            current = ReferenceEquals(next, current) ? null : next;
        }
        if (current is not null)
        {
            builder.Append("  [… inner chain truncated at depth ").Append(maxChainDepth)
                .Append(']').Append(Environment.NewLine);
        }
        return builder.ToString();
    }

    /// <summary>Upper bound on the characters emitted for any one exception type name.</summary>
    private const int TypeNameMaxChars = 128;

    /// <summary>
    /// Renders a type name that is bounded and closed to caller-derived text.
    /// </summary>
    /// <remarks>
    /// A runtime type name is NOT inherently safe metadata: a dynamically emitted type carries
    /// its creator's chosen name, and a constructed generic carries its type arguments, so the
    /// name can both reproduce caller-influenced text and grow without limit (a deeply nested
    /// generic produced a five-figure name and blew the envelope bound). Three guards close that:
    /// a dynamic assembly yields a placeholder instead of its name, a constructed generic is
    /// reduced to its open definition so arguments never appear, and the survivor — which comes
    /// from a statically loaded assembly's manifest — is hard-capped. The cap is what keeps the
    /// wire envelope bound total, since this is the only variable-length part of the fallback.
    /// See internal documentation — SOE-D3.
    /// </remarks>
    private static string DescribeTypeSafely(Type type)
    {
        try
        {
            if (type.IsConstructedGenericType)
            {
                type = type.GetGenericTypeDefinition();
            }
            if (type.Assembly.IsDynamic || type.IsGenericParameter)
            {
                return "<runtime-emitted type withheld>";
            }

            var name = type.FullName ?? type.Name;
            return name.Length > TypeNameMaxChars
                ? name.Substring(0, TypeNameMaxChars) + "…(truncated)"
                : name;
        }
        catch
        {
            return "<type unavailable>";
        }
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
    /// Composes the reported cause for an SSH session-open failure from the whole inner
    /// exception chain, not just the outer message.
    /// </summary>
    /// <remarks>
    /// SSH.NET states the decisive cause in a nested exception (the outer wrap only names the
    /// guest), so passing the outer message alone would drop it before salience ranking ever
    /// sees it. The chain is appended, and the empty-message case still contributes a synthetic
    /// lead so the arm never returns fixed text in place of an actionable cause. See
    /// internal documentation — SOE-D3, SOE-D4.
    /// </remarks>
    private static string ComposeSshSessionOpenError(SshSessionOpenException ex)
    {
        var parts = new List<string>();
        var primary = string.IsNullOrWhiteSpace(ex.Message)
            ? $"Failed to open an SSH session to the Linux guest VM '{ex.VmId}'."
            : ex.Message;
        parts.Add(primary);

        // The chain is read from the pre-redacted property, NEVER from InnerException: this mapper
        // has no password and cannot remove a reversed/Base64/URL-encoded secret, so walking a raw
        // chain here would reintroduce the credential-exposure path.
        var chain = ex.RedactedCauseChain;
        if (!string.IsNullOrWhiteSpace(chain) &&
            primary.IndexOf(chain, StringComparison.Ordinal) < 0)
        {
            parts.Add(chain);
        }

        return string.Join(" -- ", parts);
    }

    /// <summary>
    /// Composes the actionable guest-credential-rejection message: attempted username,
    /// guest channel, operation, and both plausible causes with operator next steps.
    ///
    /// <para>The retained platform text is already scrubbed with the actual password at the
    /// throw site. The pass below is defense-in-depth for script-shaped patterns only and
    /// cannot scrub a raw password, so this MUST NOT be re-pointed at unredacted stderr.
    /// See internal documentation — GCR-D4.</para>
    /// </summary>
    private static string ComposeGuestCredentialRejectedError(SessionOpenFailedException ex)
    {
        var platformText = SanitizeSessionOpenErrorText(StderrSpillHelper.RedactDefensively(
            RedactCredentials(ComposeSessionOpenFailedError(ex), password: null)));

        var attemptedUser = string.IsNullOrWhiteSpace(ex.Username) ? "(unknown)" : ex.Username;

        var guidance =
            $"The guest rejected the supplied credential for user '{attemptedUser}' while opening a " +
            "PowerShell Direct session (operation: open guest session). " +
            "Two causes are possible and the server cannot tell them apart. " +
            "1) The credential is genuinely wrong for this image — verify the username and password " +
            "that this image was built with, then retry. " +
            "2) The guest is not yet accepting logons — if the VM was started recently, wait for it to " +
            "finish booting and retry. ";

        var composed = guidance + $"Underlying platform text: {platformText}";
        if (composed.Length <= SessionOpenErrorMaxChars)
        {
            return composed;
        }

        // The guidance is the actionable part and is never elided; only the platform text is
        // bounded, with the decisive cause reproduced verbatim.
        //
        // The structural cause captured at the throw site MUST be threaded through here as well
        // as on the SESSION_FAILED arm: without it this arm falls back to re-inferring the cause
        // from flattened platform prose, so an overlong credential-rejection envelope can omit
        // the very statement the throw site already isolated. It is put through the same
        // redaction and sanitization as the platform text because it reaches the same wire.
        // See internal documentation — SOE-D3.
        var structuralCause = string.IsNullOrWhiteSpace(ex.DecisiveCause)
            ? null
            : SanitizeSessionOpenErrorText(StderrSpillHelper.RedactDefensively(
                RedactCredentials(ex.DecisiveCause!, password: null)));

        return BoundWithActionableCore(
            guidance + "Underlying platform text (decisive cause): ",
            platformText,
            ex.SpillSummary,
            BuildFramingVocabulary("Open PowerShell Direct guest session", ex.VmId, ErrorCodes.AuthFailed),
            structuralCause);
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
    /// See internal documentation — Phase 1: Credential Redaction.
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

        return RedactLinuxSeedCredentialShapes(result);
    }

    /// <summary>
    /// Ceiling for surfaced diagnostic cause text. Tunable, NOT an invariant — the invariants are
    /// the sanitize-then-truncate ordering, tail preservation, and the truncation disclosure.
    /// Single definition site by design.
    /// See internal documentation — ISO-D38.
    /// </summary>
    internal const int DiagnosticCauseMaxChars = 2000;

    /// <summary>Marker prefixed to a capped cause so elision is visible rather than silent.</summary>
    internal const string DiagnosticCauseElisionMarker = "…[truncated] ";

    /// <summary>
    /// Additional credential shapes that can reach a diagnostic through the Ubuntu seed path.
    /// Kept inside the shared <see cref="RedactCredentials"/> chain rather than duplicated locally.
    /// See internal documentation — ISO-D39.
    /// </summary>
    private static string RedactLinuxSeedCredentialShapes(string text)
    {
        var result = text;

        // -EncodedCommand blobs are redacted wholesale: the plaintext cannot be inspected safely.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(-Encoded(?:Command|Arguments))\s+[A-Za-z0-9+/=]{16,}",
            "$1 ***REDACTED***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Any PEM block, not just private keys: a certificate body can carry embedded key material
        // and is never useful in a diagnostic.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"-----BEGIN [A-Z0-9 ]+-----[\s\S]*?-----END [A-Z0-9 ]+-----",
            "***REDACTED***");

        // Unix crypt hashes in ANY scheme, matched by the $id$[salt]$hash shape rather than the
        // $6$ literal, so a future move to yescrypt or argon2 cannot silently defeat the matcher.
        // The optional version and comma-delimited parameter segments must be consumed too, or an
        // Argon2 encoding's salt and hash survive as a visible suffix.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"\$(?:1|2[aby]|5|6|y|gy|7|argon2[a-z0-9]*)\$(?:[A-Za-z0-9=+/,.\-]*\$)*[^\s:,""']+",
            "***REDACTED***");

        // cloud-init / NoCloud YAML credential keys echoed back by a failing authoring step.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?<key>\b(?:hashed_passwd|plain_text_passwd|passwd|password)\b\s*[:=]\s*)(?<value>""[^""]*""|'[^']*'|[^\s,}]+)",
            "${key}***REDACTED***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // The literal caller parameter name carrying a secret on the Ubuntu path.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(\badminPassword\b\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,}]+)",
            "$1***REDACTED***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Shapes below are matched structurally so redaction never depends on the caller knowing
        // the secret: shared callers pass a null password.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(-AdminPassword\s+)(""[^""]*""|'[^']*'|\S+)",
            "$1***REDACTED***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Both `net user` operands are parsed positionally as quoted-or-bare tokens: a display
        // name such as "Jane Doe" is a first-class form, so assuming a bare first argument leaks
        // the second one. A token starting with `/` or `-` is a switch (`/active:yes`, `/add`,
        // `/domain`), never a password — matching it would corrupt passwordless commands this
        // repo emits, which is a byte-difference on credential-free text.
        result = NetUserCredentialRegex.Replace(result, "$1***REDACTED***");

        // Answer-file credential XML. Anchored on the ENCLOSING password-bearing element rather
        // than on an exact sibling sequence, because the shapes this repo emits interleave
        // <PlainText> between </Value> and the closing tag; enumerating sibling orders kept
        // missing variants. Any element whose name ends in "Password" qualifies. The gap to
        // <Value> is a negated-class skip: it cannot cross any Password element boundary, so the
        // match stays inside the element that opened it, and each skipped character is decided
        // locally instead of by backtracking retry.
        if (result.Contains("Password", StringComparison.OrdinalIgnoreCase))
        {
            result = ReplaceFailingClosed(
                AnswerFilePasswordValueRegex,
                result,
                "$1***REDACTED***$2");
        }

        return result;
    }

    /// <summary>
    /// Applies <paramref name="pattern"/> and, if its bounded match timeout fires, discards the
    /// text entirely. Failing closed is required: this runs on the shared error path, so emitting
    /// the input unchanged after a timeout would surface exactly the unsanitized string the
    /// pattern exists to redact.
    /// </summary>
    private static string ReplaceFailingClosed(
        System.Text.RegularExpressions.Regex pattern,
        string text,
        string replacement)
    {
        try
        {
            return pattern.Replace(text, replacement);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return "***REDACTED***";
        }
    }

    /// <summary>Bounds the work a pathological error string can cause on the shared error path.</summary>
    private static readonly TimeSpan RedactionMatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly System.Text.RegularExpressions.Regex NetUserCredentialRegex = new(
        @"(\bnet\s+user\s+(?:""[^""]*""|'[^']*'|[^\s/\-]\S*)\s+)(""[^""]*""|'[^']*'|[^\s""'/\-][^\s""']*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled,
        RedactionMatchTimeout);

    private static readonly System.Text.RegularExpressions.Regex AnswerFilePasswordValueRegex = new(
        @"(<[\w.:\-]*Password\b[^>]*>(?:(?!</?[\w.:\-]*Password\b)[\s\S]){0,400}?<[\w.:\-]*Value\b[^>]*>)[^<]*(</[\w.:\-]*Value>)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled,
        RedactionMatchTimeout);

    /// <summary>
    /// Prepares raw step output for surfacing: sanitize FIRST, then cap. Truncation after
    /// sanitization is the load-bearing ordering — a cut applied first could bisect a credential
    /// pattern into an unmatched, and therefore unredacted, fragment. The tail is retained because
    /// PowerShell and native tools report the operative error last.
    /// See internal documentation — ISO-D38 / ISO-D39.
    /// </summary>
    internal static (string? Cause, bool Truncated) PrepareDiagnosticCause(string? rawCause, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(rawCause))
        {
            return (null, false);
        }

        var sanitized = SanitizePowerShellErrorText(RedactCredentials(rawCause, password)).Trim();
        if (sanitized.Length == 0)
        {
            return (null, false);
        }

        if (sanitized.Length <= DiagnosticCauseMaxChars)
        {
            return (sanitized, false);
        }

        // The marker is spent from the same budget, never added on top of it, so the surfaced
        // string cannot exceed the cap.
        var tailBudget = DiagnosticCauseMaxChars - DiagnosticCauseElisionMarker.Length;
        var tail = sanitized[^tailBudget..];

        // Prefer a newline boundary so the retained text starts on a whole line.
        var firstNewline = tail.IndexOf('\n');
        if (firstNewline >= 0 && firstNewline < 200)
        {
            tail = tail[(firstNewline + 1)..];
        }

        return (DiagnosticCauseElisionMarker + tail, true);
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
    /// <summary>
    /// Session-open variant of <see cref="SanitizePowerShellErrorText"/>: strips script bodies,
    /// positional tokens and stack frames but RETAINS the named <c>FullyQualifiedErrorId</c> and
    /// <c>CategoryInfo</c> facets.
    /// </summary>
    /// <remarks>
    /// Those two are stable identifiers that disclose no script content and are the signal a
    /// caller classifies on — stripping them would trade an information-disclosure defect for an
    /// undiagnosable envelope. See
    /// internal documentation — SOE-D10.
    /// </remarks>
    internal static string SanitizeSessionOpenErrorText(string text)
        => SanitizePowerShellErrorText(text, retainNamedFacets: true);

    internal static string SanitizePowerShellErrorText(string text)
        => SanitizePowerShellErrorText(text, retainNamedFacets: false);

    private static string SanitizePowerShellErrorText(string text, bool retainNamedFacets)
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

        // Caret-pointer lines: "+ ~~~~~~~~~~~~~~~~~~~~~~~".
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"^\s*\+\s*~+\s*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // The "+ <source line>" rendering that accompanies a positional record. Retaining it
        // while stripping paths and positions would still put script body on the wire, so it is
        // removed too. The named facets ("+ CategoryInfo", "+ FullyQualifiedErrorId") are
        // excluded from this pattern because they are stable identifiers, not script content.
        // See internal documentation — SOE-D10.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"^\s*\+\s*(?!CategoryInfo\b)(?!FullyQualifiedErrorId\b)(?!RecommendedAction\b)\S[^\r\n]*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!retainNamedFacets)
        {
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
        }

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
