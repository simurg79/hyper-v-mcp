using System.Management.Automation;
using System.Management.Automation.Remoting;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Runtime tests for error envelope mapping from internal exceptions to MCP response shapes.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy.
///
/// These tests exercise the REAL ErrorMapper implementation. They will fail with
/// NotImplementedException until the mapper is fully implemented.
///
/// Expected runtime flows:
/// - HostNotFoundException → HOST_NOT_FOUND error code
/// - VmNotFoundException → VM_NOT_FOUND error code
/// - VmAlreadyExistsException → VM_ALREADY_EXISTS error code
/// - ConcurrencyLimitException → CONCURRENCY_LIMIT error code
/// - CommandTimeoutException → COMMAND_TIMEOUT with partial data
/// - ToolNotFoundException → TOOL_NOT_FOUND error code
/// - FileNotFoundException → FILE_NOT_FOUND error code
/// - ArgumentException → INVALID_PARAMETER error code
/// - UnauthorizedAccessException → AUTH_FAILED error code
/// - TimeoutException → COMMAND_TIMEOUT error code
/// - InvalidOperationException → COMMAND_FAILED error code
/// - NotSupportedException → INVALID_PARAMETER error code
/// - IOException → TRANSFER_FAILED error code
/// - Unknown Exception types → INTERNAL_ERROR catch-all
/// - All mappings produce success=false, non-null error message
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement ErrorMapper.MapException with a switch/pattern-match on exception type.
/// 2. Map each known exception type to the correct error code constant.
/// 3. For CommandTimeoutException, include partial output in the data field.
/// 4. For unknown exception types, use INTERNAL_ERROR as catch-all.
/// </summary>
[Trait("Category", "Runtime")]
public class ErrorMappingRuntimeTests
{
    private readonly ErrorMapper _mapper = new();

    // ─── HostNotFoundException → HOST_NOT_FOUND ────────────────────────

    /// <summary>
    /// HostNotFoundException must map to HOST_NOT_FOUND error code.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: HOST_NOT_FOUND.
    /// </summary>
    [Fact]
    public void Maps_HostNotFoundException_To_HostNotFound()
    {
        var ex = new HostNotFoundException("nonexistent-host");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.HostNotFound,
            "HostNotFoundException must map to HOST_NOT_FOUND " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy)");
        response.Error.Should().Contain("nonexistent-host",
            "sanitized error message should include the unresolved hostId");
        response.Data.Should().BeNull();
    }

    // ─── VmNotFoundException → VM_NOT_FOUND ────────────────────────────

    /// <summary>
    /// VmNotFoundException must map to VM_NOT_FOUND error code.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_NOT_FOUND.
    /// </summary>
    [Fact]
    public void Maps_VmNotFoundException_To_VmNotFound()
    {
        var ex = new VmNotFoundException("local", "test-vm-001");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.VmNotFound,
            "VmNotFoundException must map to VM_NOT_FOUND");
        response.Error.Should().Contain("test-vm-001",
            "sanitized error message should include the VM identifier");
    }

    // ─── VmAlreadyExistsException → VM_ALREADY_EXISTS ──────────────────

    /// <summary>
    /// VmAlreadyExistsException must map to VM_ALREADY_EXISTS error code.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_ALREADY_EXISTS.
    /// </summary>
    [Fact]
    public void Maps_VmAlreadyExistsException_To_VmAlreadyExists()
    {
        var ex = new VmAlreadyExistsException("local", "my-vm");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.VmAlreadyExists,
            "VmAlreadyExistsException must map to VM_ALREADY_EXISTS");
        response.Error.Should().Contain("my-vm",
            "sanitized error message should include the VM name");
    }

    // ─── ConcurrencyLimitException → CONCURRENCY_LIMIT ─────────────────

    /// <summary>
    /// ConcurrencyLimitException must map to CONCURRENCY_LIMIT error code.
    /// See /myplans/operational/concurrency/concurrency-design.md — Backpressure Response.
    /// </summary>
    [Fact]
    public void Maps_ConcurrencyLimitException_To_ConcurrencyLimit()
    {
        var ex = new ConcurrencyLimitException("VM test-vm is busy");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.ConcurrencyLimit,
            "ConcurrencyLimitException must map to CONCURRENCY_LIMIT " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Backpressure Response)");
        response.Error.Should().Contain("concurrency limit",
            "sanitized message should describe the concurrency limit rejection");
    }

    // ─── CommandTimeoutException → COMMAND_TIMEOUT + partial data ───────

    /// <summary>
    /// CommandTimeoutException must map to COMMAND_TIMEOUT with partial output in data.
    /// See /myplans/execution/commands/commands-design.md — CMD-D4: Timeout returns success:false with partial output.
    /// See /myplans/design.md §4 — ADR-9.
    /// </summary>
    [Fact]
    public void Maps_CommandTimeoutException_To_CommandTimeout_With_Partial_Data()
    {
        var ex = new CommandTimeoutException(
            "Command exceeded timeout of 30 seconds",
            partialStdout: "partial output before timeout...",
            partialStderr: "some error output",
            durationMs: 30000);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CommandTimeout,
            "CommandTimeoutException must map to COMMAND_TIMEOUT " +
            "(see /myplans/execution/commands/commands-design.md — CMD-D4)");
        response.Data.Should().NotBeNull(
            "timeout responses must include partial data (ADR-9)");
    }

    // ─── ToolNotFoundException → TOOL_NOT_FOUND ────────────────────────

    /// <summary>
    /// ToolNotFoundException must map to TOOL_NOT_FOUND error code.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6.
    /// </summary>
    [Fact]
    public void Maps_ToolNotFoundException_To_ToolNotFound()
    {
        var ex = new ToolNotFoundException("vm_nonexistent");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(RuntimeErrorCodes.ToolNotFound,
            "ToolNotFoundException must map to TOOL_NOT_FOUND");
        response.Error.Should().Contain("vm_nonexistent",
            "sanitized error message should include the tool name");
    }

    // ─── ArgumentException → INVALID_PARAMETER ──────────────────────────

    /// <summary>
    /// ArgumentException must map to INVALID_PARAMETER error code.
    /// This covers tool argument validation failures.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public void Maps_ArgumentException_To_InvalidParameter()
    {
        var ex = new ArgumentException("vmId cannot be empty", "vmId");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "ArgumentException must map to INVALID_PARAMETER " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy)");
        response.Error.Should().Contain("vmId",
            "sanitized message should include the parameter name");
    }

    /// <summary>
    /// ArgumentNullException (subclass of ArgumentException) must also map to INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public void Maps_ArgumentNullException_To_InvalidParameter()
    {
        var ex = new ArgumentNullException("hostId");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "ArgumentNullException must map to INVALID_PARAMETER");
    }

    // ─── UnauthorizedAccessException → AUTH_FAILED ──────────────────────

    /// <summary>
    /// UnauthorizedAccessException must map to AUTH_FAILED error code.
    /// Covers WinRM/PS Direct credential failures and permission issues.
    /// See /myplans/security/security-design.md — Error Code Taxonomy: AUTH_FAILED.
    /// </summary>
    [Fact]
    public void Maps_UnauthorizedAccessException_To_AuthFailed()
    {
        var ex = new UnauthorizedAccessException("Access denied to remote host with secret password=p@ss123");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.AuthFailed,
            "UnauthorizedAccessException must map to AUTH_FAILED " +
            "(see /myplans/security/security-design.md — Error Code Taxonomy)");
        response.Error.Should().NotContain("p@ss123",
            "sanitized message must not leak credentials from the raw exception");
        response.Error.Should().Contain("denied",
            "sanitized message should describe the auth failure");
    }

    // ─── TimeoutException → COMMAND_TIMEOUT ────────────────────────────

    /// <summary>
    /// TimeoutException must map to COMMAND_TIMEOUT error code.
    /// Covers generic System.TimeoutException from WinRM connection timeouts.
    /// See /myplans/execution/commands/commands-design.md — CMD-D4.
    /// </summary>
    [Fact]
    public void Maps_TimeoutException_To_CommandTimeout()
    {
        var ex = new TimeoutException("WinRM connection timed out after 30 seconds");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CommandTimeout,
            "TimeoutException must map to COMMAND_TIMEOUT " +
            "(see /myplans/execution/commands/commands-design.md — CMD-D4)");
        response.Data.Should().BeNull(
            "generic TimeoutException should not include partial data (only CommandTimeoutException does)");
    }

    // ─── CheckpointFailedException → CHECKPOINT_FAILED ──────────────────

    /// <summary>
    /// CheckpointFailedException must map to CHECKPOINT_FAILED error code.
    /// This is the canonical mapping for checkpoint operation failures.
    /// Must be matched before InvalidOperationException in the pattern chain
    /// since CheckpointFailedException extends InvalidOperationException.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: CHECKPOINT_FAILED.
    /// </summary>
    [Fact]
    public void Maps_CheckpointFailedException_To_CheckpointFailed()
    {
        var ex = new CheckpointFailedException("local", "test-vm",
            "Checkpoint creation failed: insufficient disk space",
            checkpointName: "bad-checkpoint");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CheckpointFailed,
            "CheckpointFailedException must map to CHECKPOINT_FAILED " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy)");
        response.Error.Should().Contain("test-vm",
            "sanitized message should include the VM identifier");
        response.Error.Should().Contain("bad-checkpoint",
            "sanitized message should include the checkpoint name");
    }

    // ─── InvalidOperationException → COMMAND_FAILED ─────────────────────

    /// <summary>
    /// InvalidOperationException must map to COMMAND_FAILED error code and forward the
    /// original exception message — not a fixed generic string.
    /// These exceptions are thrown by HyperVManager.HandleError() and contain user-relevant
    /// diagnostic info (PowerShell cmdlet errors, exit codes, stderr output).
    /// See GitHub Issue #10: ErrorMapper swallows real error messages from InvalidOperationException.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: COMMAND_FAILED.
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_To_CommandFailed()
    {
        var ex = new InvalidOperationException("VM is in an invalid state for this operation");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "InvalidOperationException must map to COMMAND_FAILED, not INTERNAL_ERROR " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy)");
        response.Error.Should().Contain("VM is in an invalid state for this operation",
            "the original exception message must be forwarded, not replaced with a fixed string " +
            "(see GitHub Issue #10)");
    }

    /// <summary>
    /// InvalidOperationException with a PowerShell error message must forward the actual
    /// error text, not a fixed generic string. This is the primary scenario from Issue #10:
    /// HyperVManager.HandleError() wraps PowerShell cmdlet failures in InvalidOperationException
    /// with messages like "Get-VM : The operation cannot be performed while the VM is in its
    /// current state." — the mapper must preserve this diagnostic info.
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_Forwards_PowerShell_Error_Message()
    {
        const string psErrorMessage =
            "PowerShell execution failed (exit code 1): Get-VM : The operation cannot be performed while the VM is in its current state.";
        var ex = new InvalidOperationException(psErrorMessage);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "InvalidOperationException must map to COMMAND_FAILED");
        response.Error.Should().Contain("Get-VM",
            "the PowerShell cmdlet name from the original error must be forwarded " +
            "(see GitHub Issue #10)");
        response.Error.Should().Contain("current state",
            "the VM state diagnostic message must be forwarded " +
            "(see GitHub Issue #10)");
    }

    /// <summary>
    /// InvalidOperationException with an empty message must still map to COMMAND_FAILED
    /// with a non-empty error string. Even when the exception has no message,
    /// the response must include a usable error description.
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_With_Empty_Message_To_CommandFailed()
    {
        var ex = new InvalidOperationException(string.Empty);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "InvalidOperationException with empty message must still map to COMMAND_FAILED");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "even with an empty exception message, the response must include a non-empty error");
    }

    /// <summary>
    /// InvalidOperationException with HyperVManager.HandleError() format must forward the
    /// stderr content. HandleError() produces messages like:
    ///   "PowerShell execution failed (exit code {exitCode}): {errorText}"
    /// The mapper must preserve the stderr content ({errorText}) in the response so the
    /// caller can see what actually went wrong.
    /// See GitHub Issue #10.
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_Forwards_HandleError_Stderr_Content()
    {
        const string stderrContent = "Start-VM : The virtual machine 'test-vm' could not be started because the hypervisor is not running.";
        var handleErrorMessage = $"PowerShell execution failed (exit code 1): {stderrContent}";
        var ex = new InvalidOperationException(handleErrorMessage);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "InvalidOperationException from HandleError must map to COMMAND_FAILED");
        response.Error.Should().Contain(stderrContent,
            "the stderr content from HandleError format must be forwarded in the response " +
            "(see GitHub Issue #10)");
    }

    /// <summary>
    /// Regression: CheckpointFailedException (which extends InvalidOperationException) must
    /// still map to CHECKPOINT_FAILED, NOT COMMAND_FAILED. The pattern-match ordering in
    /// ErrorMapper must check CheckpointFailedException before InvalidOperationException.
    /// If this order is broken, checkpoint failures will be misclassified as COMMAND_FAILED.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: CHECKPOINT_FAILED.
    /// </summary>
    [Fact]
    public void Regression_CheckpointFailedException_Maps_To_CheckpointFailed_Not_CommandFailed()
    {
        var ex = new CheckpointFailedException("local", "regression-vm",
            "Checkpoint restore failed: VM state is incompatible",
            checkpointName: "snap-001");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CheckpointFailed,
            "CheckpointFailedException must map to CHECKPOINT_FAILED, not COMMAND_FAILED — " +
            "pattern-match ordering must check CheckpointFailedException before InvalidOperationException");
        response.ErrorCode.Should().NotBe(ErrorCodes.CommandFailed,
            "CheckpointFailedException must NOT fall through to COMMAND_FAILED " +
            "(regression test for pattern-match ordering)");
        response.Error.Should().Contain("regression-vm",
            "sanitized message should include the VM identifier");
        response.Error.Should().Contain("snap-001",
            "sanitized message should include the checkpoint name");
    }

    // ─── NotSupportedException → INVALID_PARAMETER ──────────────────────

    /// <summary>
    /// NotSupportedException must map to INVALID_PARAMETER error code.
    /// Covers unsupported shell types, unsupported operations, etc.
    /// </summary>
    [Fact]
    public void Maps_NotSupportedException_To_InvalidParameter()
    {
        var ex = new NotSupportedException("Shell 'bash' is not supported on Windows guest");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "NotSupportedException must map to INVALID_PARAMETER");
        response.Error.Should().Contain("not supported",
            "sanitized message should describe that the operation is unsupported");
    }

    // ─── IOException → TRANSFER_FAILED ──────────────────────────────────

    /// <summary>
    /// IOException must map to TRANSFER_FAILED error code.
    /// Covers disk I/O failures during file transfer, checkpoint operations, etc.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: TRANSFER_FAILED.
    /// </summary>
    [Fact]
    public void Maps_IOException_To_TransferFailed()
    {
        var ex = new IOException("Disk full: cannot write to C:\\temp\\transfer.zip");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.TransferFailed,
            "IOException must map to TRANSFER_FAILED " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy)");
    }

    // ─── Unknown Exception → INTERNAL_ERROR ─────────────────────────────

    /// <summary>
    /// Truly unknown/unexpected exceptions must map to INTERNAL_ERROR as catch-all.
    /// The raw exception message must NOT be exposed — use a fixed safe message.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6.
    /// </summary>
    [Fact]
    public void Maps_Unknown_Exception_To_InternalError()
    {
        // Use a custom exception type that has no specific mapping, with a message
        // that contains sensitive information to verify it is not leaked.
        var ex = new AccessViolationException("ConnectionString=Server=db;Password=secret123");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(RuntimeErrorCodes.InternalError,
            "truly unknown exceptions must produce INTERNAL_ERROR");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "error message must be present for diagnostics");
        response.Error.Should().NotContain("secret123",
            "raw exception messages must never be exposed for unknown exception types — " +
            "they may contain secrets, credentials, or connection strings");
        response.Error.Should().NotContain("ConnectionString",
            "raw exception messages must never be exposed for unknown exception types");
    }

    // ─── VmNotRunningException → VM_NOT_RUNNING ────────────────────────

    /// <summary>
    /// VmNotRunningException must map to VM_NOT_RUNNING error code.
    /// See GitHub Issue #21.
    /// </summary>
    [Fact]
    public void Maps_VmNotRunningException_To_VmNotRunning()
    {
        var ex = new VmNotRunningException("local", "test-vm-001", "Off");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.VmNotRunning,
            "VmNotRunningException must map to VM_NOT_RUNNING (Issue #21)");
        response.Error.Should().Contain("test-vm-001",
            "sanitized error message should include the VM identifier");
        response.Error.Should().Contain("Off",
            "sanitized error message should include the actual VM state");
    }

    // ─── VM State Forwarding ───────────────────────────────────────────

    /// <summary>
    /// When vmState is provided, it must be included in the response state field.
    /// See /myplans/mcp-interface/mcp-interface-design.md — ADR-8: Rich error envelope with state.
    /// </summary>
    [Fact]
    public void Maps_Exception_With_VmState()
    {
        var ex = new VmNotFoundException("local", "test-vm");

        var response = _mapper.MapException(ex, vmState: "Off");

        response.State.Should().Be("Off",
            "VM state must be forwarded to the response envelope " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — ADR-8)");
    }

    // ─── All Mappings Return success=false ──────────────────────────────

    /// <summary>
    /// All error mappings must produce success=false.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllExceptionTypes))]
    public void All_Mappings_Return_Success_False(Exception ex)
    {
        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse(
            $"mapping {ex.GetType().Name} must produce success=false");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "error message must always be present");
        response.ErrorCode.Should().NotBeNullOrWhiteSpace(
            "errorCode must always be present for programmatic handling " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — ADR-8)");
    }

    public static IEnumerable<object[]> AllExceptionTypes()
    {
        yield return new object[] { new HostNotFoundException("test") };
        yield return new object[] { new VmNotFoundException("local", "vm") };
        yield return new object[] { new VmAlreadyExistsException("local", "vm") };
        yield return new object[] { new VmNotRunningException("local", "vm", "Off") };
        yield return new object[] { new ConcurrencyLimitException("busy") };
        yield return new object[] { new CommandTimeoutException("timeout") };
        yield return new object[] { new ToolNotFoundException("vm_fake") };
        yield return new object[] { new CheckpointFailedException("local", "vm", "checkpoint failed") };
        yield return new object[] { new FileNotFoundException("missing file") };
        yield return new object[] { new ArgumentException("bad arg") };
        yield return new object[] { new ArgumentNullException("param") };
        yield return new object[] { new UnauthorizedAccessException("denied") };
        yield return new object[] { new TimeoutException("timed out") };
        yield return new object[] { new InvalidOperationException("invalid op") };
        yield return new object[] { new NotSupportedException("not supported") };
        yield return new object[] { new IOException("io error") };
        yield return new object[] { new AccessViolationException("unknown") };
    }

    // ─── FileNotFoundException Regression ────────────────────────────────

    /// <summary>
    /// FileNotFoundException must still map to FILE_NOT_FOUND (not TRANSFER_FAILED from IOException inheritance).
    /// This is a regression test ensuring FileNotFoundException is matched before IOException
    /// in the pattern-matching chain.
    /// </summary>
    [Fact]
    public void Maps_FileNotFoundException_To_FileNotFound_Not_TransferFailed()
    {
        var ex = new FileNotFoundException("Source file does not exist");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.FileNotFound,
            "FileNotFoundException must map to FILE_NOT_FOUND, not TRANSFER_FAILED, " +
            "even though FileNotFoundException inherits from IOException");
    }

    // ─── Message Sanitization Regression Tests ───────────────────────────

    /// <summary>
    /// Regression: IOException with filesystem paths must not leak those paths.
    /// Raw IOException messages may contain full filesystem paths with usernames.
    /// </summary>
    [Fact]
    public void Sanitization_IOException_Does_Not_Leak_Filesystem_Paths()
    {
        var ex = new IOException(@"Access denied: C:\Users\admin\AppData\secrets\config.dat");

        var response = _mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.TransferFailed);
        response.Error.Should().NotContain(@"C:\Users",
            "sanitized message must not leak filesystem paths from IOException");
        response.Error.Should().NotContain("admin",
            "sanitized message must not leak user account names");
    }

    /// <summary>
    /// Regression: UnauthorizedAccessException must not leak credential details.
    /// Raw messages from WinRM auth failures may contain server names and credential hints.
    /// </summary>
    [Fact]
    public void Sanitization_UnauthorizedAccess_Does_Not_Leak_Credentials()
    {
        var ex = new UnauthorizedAccessException(
            "Access denied for user DOMAIN\\serviceaccount on host hyperv-prod-01.corp.internal:5986");

        var response = _mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.AuthFailed);
        response.Error.Should().NotContain("serviceaccount",
            "sanitized message must not leak account names");
        response.Error.Should().NotContain("hyperv-prod-01",
            "sanitized message must not leak internal hostnames");
        response.Error.Should().NotContain("5986",
            "sanitized message must not leak port numbers");
    }

    /// <summary>
    /// Regression: TimeoutException must not leak connection string details.
    /// </summary>
    [Fact]
    public void Sanitization_TimeoutException_Does_Not_Leak_Connection_Details()
    {
        var ex = new TimeoutException(
            "Connection to wsman://hyperv-host.internal:5986/wsman timed out after 30000ms");

        var response = _mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.CommandTimeout);
        response.Error.Should().NotContain("hyperv-host.internal",
            "sanitized message must not leak internal hostnames");
        response.Error.Should().NotContain("wsman://",
            "sanitized message must not leak connection URIs");
    }

    /// <summary>
    /// Regression: Domain exceptions with user-supplied identifiers are safe to include.
    /// These identifiers were provided by the caller and are not secrets.
    /// </summary>
    [Fact]
    public void Sanitization_Domain_Exceptions_Include_Safe_Identifiers()
    {
        var hostEx = new HostNotFoundException("my-remote-host");
        var vmEx = new VmNotFoundException("local", "test-vm-001");
        var toolEx = new ToolNotFoundException("vm_nonexistent");

        _mapper.MapException(hostEx).Error.Should().Contain("my-remote-host");
        _mapper.MapException(vmEx).Error.Should().Contain("test-vm-001");
        _mapper.MapException(toolEx).Error.Should().Contain("vm_nonexistent");
    }

    /// <summary>
    /// Regression: ArgumentException without a ParamName still produces a safe message.
    /// </summary>
    [Fact]
    public void Sanitization_ArgumentException_Without_ParamName_Is_Safe()
    {
        var ex = new ArgumentException("Internal validation: connection string format is wrong for Server=db;Password=secret");

        var response = _mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
        response.Error.Should().NotContain("secret",
            "sanitized message must not leak secrets from ArgumentException without ParamName");
        response.Error.Should().NotContain("Password",
            "sanitized message must not leak credential keys");
    }

    // ─── InvalidOperationException with Filesystem Paths — Intentional Forwarding ───

    /// <summary>
    /// InvalidOperationException messages containing host filesystem paths MUST be forwarded
    /// verbatim. This is the intended behavior (not a bug) — the MCP server runs on the user's
    /// own machine and these paths are user-configured operational diagnostics, not secrets.
    ///
    /// This test verifies the accepted risk documented in:
    ///   - ErrorMapper.cs InvalidOperationException comment block
    ///   - /myplans/mcp-interface/mcp-interface-design.md — Accepted Risk: COMMAND_FAILED message forwarding
    ///
    /// Callers producing these messages:
    ///   - FileTransferService.CopyToGuestAsync(): "Failed to copy file to guest {vmId}: {stderr}"
    ///   - FileTransferService.CopyFromGuestAsync(): "Failed to copy file from guest {vmId}: {stderr}"
    ///   - SessionStore.GetOrCreateSessionAsync(): "Failed to create PSSession '{name}': {stderr}"
    ///   - HyperVManager.HandleError(): "PowerShell execution failed (exit code {code}): {stderr}"
    ///
    /// See GitHub Issue #10.
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_Forwards_Filesystem_Paths_Intentionally()
    {
        // Simulate a FileTransferService error message containing host filesystem paths.
        // Phase 2 (issue #52) uses Copy-Item -ToSession over a persistent PSSession; the
        // synthetic stderr below mirrors the actual format produced by Copy-Item failures
        // to keep this test aligned with current channel behavior.
        const string messageWithPath =
            @"Failed to copy file to guest 12345678-1234-1234-1234-123456789abc: " +
            @"Copy-Item : Cannot find path 'C:\HyperVMCP\VMs\test-vm\disk.vhdx' because it does not exist. " +
            @"At line:1 char:1";
        var ex = new InvalidOperationException(messageWithPath);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        // Phase 2 (issue #52): the InvalidOperationException sub-branch in ErrorMapper
        // now classifies "does not exist" / "cannot find" substrings as FILE_NOT_FOUND
        // (more specific than COMMAND_FAILED). The path-forwarding behavior covered
        // below remains the same — operational diagnostics are not secrets.
        response.ErrorCode.Should().Be(ErrorCodes.FileNotFound,
            "InvalidOperationException whose message contains 'does not exist' must map to FILE_NOT_FOUND");

        // ST-6 sub-branch uses a fixed sanitized message — paths and GUIDs are NOT echoed
        // (consistent with the FileNotFoundException branch). Path-forwarding for the
        // generic InvalidOperationException → COMMAND_FAILED branch is exercised by
        // Maps_InvalidOperationException_GenericBranch_Still_Forwards_Operational_Messages.
        response.Error.Should().NotContain(@"C:\HyperVMCP\VMs\test-vm\disk.vhdx");
        response.Error.Should().NotContain("12345678-1234-1234-1234-123456789abc");
    }

    /// <summary>
    /// Sibling regression: when the InvalidOperationException message does NOT match
    /// any of the ST-6 substring sub-branches, the generic
    /// InvalidOperationException → COMMAND_FAILED branch still forwards the message
    /// verbatim — preserving the path-forwarding behavior from GitHub Issue #10 for
    /// true operational failures (VM state conflicts, cmdlet errors, etc.).
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_GenericBranch_Still_Forwards_Operational_Messages()
    {
        const string operationalMessage =
            @"Failed to start VM 12345678-1234-1234-1234-123456789abc: " +
            @"Start-VM : The virtual machine could not be started because the requested resource is in use.";
        var ex = new InvalidOperationException(operationalMessage);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed);
        response.Error.Should().Contain("Start-VM");
        response.Error.Should().Contain("12345678-1234-1234-1234-123456789abc");
    }

    // ─── Issue #52, ST-6/ST-7: PowerShell Direct channel substring branches ─

    /// <summary>
    /// InvalidOperationException whose message contains "Cannot find path" must map to
    /// FILE_NOT_FOUND (channel-substring branch added in ST-6).
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_CannotFindPath_To_FileNotFound()
    {
        var ex = new InvalidOperationException(
            @"Failed to copy file from guest abc: Cannot find path 'C:\guest\missing.txt' because it does not exist.");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.FileNotFound,
            "channel error containing 'Cannot find path' must map to FILE_NOT_FOUND " +
            "(see ErrorMapper.cs — Issue #52, ST-6 substring branch)");
    }

    /// <summary>
    /// InvalidOperationException whose message contains "Access is denied" must map to
    /// AUTH_FAILED (channel-substring branch).
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_AccessDenied_To_AuthFailed()
    {
        var ex = new InvalidOperationException(
            "Failed to copy file to guest abc: Access is denied. (Exception from HRESULT: 0x80070005)");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.AuthFailed,
            "channel error containing 'Access is denied' must map to AUTH_FAILED " +
            "(see ErrorMapper.cs — Issue #52, ST-6 substring branch)");
    }

    /// <summary>
    /// InvalidOperationException whose message contains "There is not enough space" must
    /// map to TRANSFER_FAILED (channel-substring branch).
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_NotEnoughSpace_To_TransferFailed()
    {
        var ex = new InvalidOperationException(
            "Failed to expand directory archive on guest abc: There is not enough space on the disk.");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.TransferFailed,
            "channel error containing 'There is not enough space' must map to TRANSFER_FAILED " +
            "(see ErrorMapper.cs — Issue #52, ST-6 substring branch)");
    }

    /// <summary>
    /// InvalidOperationException whose message contains "session is broken" must map to
    /// SESSION_FAILED (channel-substring branch).
    /// </summary>
    [Fact]
    public void Maps_InvalidOperationException_SessionBroken_To_SessionFailed()
    {
        var ex = new InvalidOperationException(
            "Invoke-Command : The session state is Broken: session is broken and cannot be used.");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "channel error containing 'session is broken' must map to SESSION_FAILED " +
            "(see ErrorMapper.cs — Issue #52, ST-6 substring branch)");
    }

    // ─── Issue #52, Gate 6 re-review: PowerShellDirectChannelException unwrap ───

    /// <summary>
    /// PowerShellDirectChannelException wrapping a PSRemotingTransportException must
    /// classify as SESSION_FAILED (not INTERNAL_ERROR). The inner exception type drives
    /// the error code; the channel's redacted top-level message is what the user sees.
    /// </summary>
    [Fact]
    public void Maps_PowerShellDirectChannelException_With_TransportInner_To_SessionFailed()
    {
        var inner = new PSRemotingTransportException("session is broken");
        var ex = new PowerShellDirectChannelException(
            "PowerShell Direct invocation failed: session transport dropped.", inner);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "PowerShellDirectChannelException must unwrap and classify by inner type — " +
            "PSRemotingTransportException → SESSION_FAILED (NOT INTERNAL_ERROR)");
        response.ErrorCode.Should().NotBe(RuntimeErrorCodes.InternalError,
            "channel exceptions must NOT fall through to INTERNAL_ERROR (Gate 6 regression)");
        response.Error.Should().Contain("session transport dropped",
            "the channel's already-redacted top-level message must be preserved");
    }

    /// <summary>
    /// PowerShellDirectChannelException wrapping a RuntimeException whose message matches
    /// the broken-session signature must classify as SESSION_FAILED — same code as the
    /// existing InvalidOperationException broken-session substring branch.
    /// </summary>
    [Fact]
    public void Maps_PowerShellDirectChannelException_With_BrokenSessionRuntime_To_SessionFailed()
    {
        var inner = new RuntimeException(
            "The runspace session is not in the Opened state. session is broken.");
        var ex = new PowerShellDirectChannelException(
            "PowerShell Direct invocation failed after retry.", inner);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "RuntimeException with broken-session signature wrapped in " +
            "PowerShellDirectChannelException must map to SESSION_FAILED");
        response.Error.Should().Contain("after retry",
            "the channel's redacted top-level message must surface to the user");
    }

    /// <summary>
    /// PowerShellDirectChannelException wrapping a RuntimeException whose message contains
    /// credentials must:
    ///   1. Classify as AUTH_FAILED via the inner-type recursion.
    ///   2. Surface only the channel's redacted top-level message (the inner's
    ///      credential-bearing text must NEVER appear in the response).
    /// </summary>
    [Fact]
    public void Maps_PowerShellDirectChannelException_With_CredentialBearingInner_Redacts_And_Maps_To_AuthFailed()
    {
        const string password = "hunter2";
        // Inner carries the credential — preserved AS-IS so ErrorMapper can classify by
        // its concrete type. The inner's message would leak credentials if surfaced
        // directly, which is exactly why ErrorMapper overwrites the recursed Error text
        // with the wrapper's redacted top-level message.
        var inner = new RuntimeException($"Failed login for user admin:{password}");
        // Channel's top-level message is already redacted via CredentialResolver.RedactPassword
        // before wrapping (see PowerShellDirectChannel.ExecuteWithRetryAsync).
        var ex = new PowerShellDirectChannelException(
            "PowerShell Direct invocation failed: Failed login for user admin:***REDACTED***",
            inner);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.AuthFailed,
            "RuntimeException with 'Failed login' signature must classify as AUTH_FAILED " +
            "via the PowerShellDirectChannelException → InnerException recursion");
        response.Error.Should().NotContain(password,
            "the credential-bearing inner message must NEVER reach the response — " +
            "only the channel's already-redacted top-level message is surfaced");
        response.Error.Should().Contain("***REDACTED***",
            "the channel's redacted top-level message must be preserved on the response");
    }

    /// <summary>
    /// PowerShellDirectChannelException with a null InnerException must default to
    /// COMMAND_FAILED (NOT INTERNAL_ERROR) and surface the redacted top-level message.
    /// </summary>
    [Fact]
    public void Maps_PowerShellDirectChannelException_With_NullInner_To_CommandFailed()
    {
        var ex = new PowerShellDirectChannelException(
            "PowerShell Direct invocation failed: redacted diagnostic.");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "PowerShellDirectChannelException with null inner must default to " +
            "COMMAND_FAILED — NOT INTERNAL_ERROR (Gate 6 regression)");
        response.ErrorCode.Should().NotBe(RuntimeErrorCodes.InternalError,
            "channel exceptions must NOT fall through to INTERNAL_ERROR");
        response.Error.Should().Contain("redacted diagnostic",
            "the channel's redacted top-level message must be surfaced");
    }

    // ─── NotImplementedException → INVALID_PARAMETER (Issue #56) ────────

    /// <summary>
    /// NotImplementedException must map to INVALID_PARAMETER with a fixed safe message.
    /// This arm protects against catalog tools whose handlers fall through to the
    /// NotImplementedException stub (the original Issue #56 bug returned INTERNAL_ERROR
    /// from the catch-all). The safe message must NOT echo the raw exception text.
    /// See GitHub Issue #56.
    /// </summary>
    [Fact]
    public void MapException_NotImplementedException_ReturnsInvalidParameter()
    {
        var ex = new NotImplementedException("Tool 'vm_future_thing' is not yet wired up.");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "NotImplementedException must map to INVALID_PARAMETER (Issue #56)");
        response.Error.Should().Be("The requested tool is not implemented in this build.",
            "the mapper must surface a fixed, sanitized message for NotImplementedException — " +
            "the raw exception message must NOT be exposed");
    }

    // ─── Issue #51: vm_create_base_image error codes ──────────────────

    /// <summary>
    /// MergeNotSupportedException must map to MERGE_NOT_SUPPORTED (CP-D6) and
    /// MUST NOT fall through to COMMAND_FAILED via its InvalidOperationException base.
    /// </summary>
    [Fact]
    public void Maps_MergeNotSupportedException_To_MergeNotSupported()
    {
        var ex = new MergeNotSupportedException("local", "test-vm",
            "Checkpoint tree for VM 'test-vm' is not a linear chain (branched).");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.MergeNotSupported,
            "MergeNotSupportedException must map to MERGE_NOT_SUPPORTED (Issue #51 / CP-D6)");
        response.ErrorCode.Should().NotBe(ErrorCodes.CommandFailed,
            "must not fall through the InvalidOperationException base-class arm");
        response.Error.Should().Contain("linear chain");
    }

    /// <summary>
    /// CheckpointMergeFailedException must map to CHECKPOINT_MERGE_FAILED (CP-D6) and
    /// MUST NOT fall through to COMMAND_FAILED.
    /// </summary>
    [Fact]
    public void Maps_CheckpointMergeFailedException_To_CheckpointMergeFailed()
    {
        var ex = new CheckpointMergeFailedException("local", "test-vm",
            "Checkpoint merge failed (exit code 1): Remove-VMSnapshot : VHDX locked.");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CheckpointMergeFailed,
            "CheckpointMergeFailedException must map to CHECKPOINT_MERGE_FAILED (Issue #51 / CP-D6)");
        response.ErrorCode.Should().NotBe(ErrorCodes.CommandFailed);
        response.Error.Should().Contain("Remove-VMSnapshot",
            "operator-supplied diagnostic message must be forwarded");
    }

    /// <summary>
    /// SysprepFailedException must map to SYSPREP_FAILED (Issue #51) and
    /// MUST NOT fall through to COMMAND_FAILED.
    /// </summary>
    [Fact]
    public void Maps_SysprepFailedException_To_SysprepFailed()
    {
        var ex = new SysprepFailedException("local", "test-vm",
            "VM 'test-vm' did not reach 'Off' state within 600 seconds after sysprep.");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SysprepFailed,
            "SysprepFailedException must map to SYSPREP_FAILED (Issue #51)");
        response.ErrorCode.Should().NotBe(ErrorCodes.CommandFailed);
        response.Error.Should().Contain("sysprep");
    }

    /// <summary>
    /// ImageCopyFailedException must map to IMAGE_COPY_FAILED (Issue #51) and
    /// MUST NOT fall through to COMMAND_FAILED.
    /// </summary>
    [Fact]
    public void Maps_ImageCopyFailedException_To_ImageCopyFailed()
    {
        var ex = new ImageCopyFailedException(
            "ServerOptions.ImageDirectory is not configured.",
            sourcePath: null,
            destinationPath: null);

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.ImageCopyFailed,
            "ImageCopyFailedException must map to IMAGE_COPY_FAILED (Issue #51)");
        response.ErrorCode.Should().NotBe(ErrorCodes.CommandFailed);
        response.Error.Should().Contain("ImageDirectory");
    }
}
