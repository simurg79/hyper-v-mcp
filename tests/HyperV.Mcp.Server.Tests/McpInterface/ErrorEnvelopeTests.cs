using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.McpInterface;

/// <summary>
/// Tests for the structured error envelope convention.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D2: Consistent response envelope.
/// See /myplans/mcp-interface/mcp-interface-design.md — ADR-8: Rich error envelope with errorCode.
///
/// These tests validate that:
/// - Success responses have the correct shape
/// - Failure responses include errorCode and optional state
/// - All error codes from the taxonomy are defined
/// - Exceptions are caught and wrapped (MCP-D6)
/// - JSON serialization produces the expected wire format
///
/// HOW TO MAKE THESE PASS:
/// 1. The McpToolResponse model is already defined — these tests should pass now.
/// 2. When tool handlers are implemented, ensure they use McpToolResponse.Ok/Fail.
/// 3. Ensure MCP-D6: exceptions are caught in tool handlers and wrapped in Fail responses.
/// </summary>
public class ErrorEnvelopeTests
{
    // ─── Success Envelope Shape ────────────────────────────────────────

    /// <summary>
    /// Success responses must have success=true, data set, error null.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Success Response.
    /// </summary>
    [Fact]
    public void Success_Response_Has_Correct_Shape()
    {
        var response = McpToolResponse.Ok(new { vmId = "test-vm-001" });

        response.Success.Should().BeTrue();
        response.Data.Should().NotBeNull();
        response.Error.Should().BeNull();
        response.ErrorCode.Should().BeNull();
        response.State.Should().BeNull();
    }

    /// <summary>
    /// Success response without data should still be valid (e.g., vm_echo).
    /// </summary>
    [Fact]
    public void Success_Response_Without_Data_Is_Valid()
    {
        var response = McpToolResponse.Ok();

        response.Success.Should().BeTrue();
        response.Data.Should().BeNull("some tools like vm_echo may not return data");
    }

    // ─── Failure Envelope Shape ────────────────────────────────────────

    /// <summary>
    /// Failure responses must have success=false, error message, errorCode, null data.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Failure Response.
    /// </summary>
    [Fact]
    public void Failure_Response_Has_Correct_Shape()
    {
        var response = McpToolResponse.Fail(
            "No VM with the specified ID exists",
            ErrorCodes.VmNotFound,
            "OsBooting");

        response.Success.Should().BeFalse();
        response.Data.Should().BeNull(
            "failed responses must have null data per design");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "error message must be human-readable");
        response.ErrorCode.Should().NotBeNullOrWhiteSpace(
            "errorCode must be machine-readable for programmatic handling (ADR-8)");
        response.State.Should().Be("OsBooting",
            "state field should reflect VM state at time of error");
    }

    /// <summary>
    /// Failure responses without state are valid — state is optional.
    /// </summary>
    [Fact]
    public void Failure_Response_Without_State_Is_Valid()
    {
        var response = McpToolResponse.Fail(
            "Required parameter missing",
            ErrorCodes.InvalidParameter);

        response.State.Should().BeNull(
            "state is optional and may not apply to all error types");
    }

    // ─── Error Code Taxonomy Completeness ──────────────────────────────

    /// <summary>
    /// All 18 error codes from the design taxonomy must be defined.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy.
    /// </summary>
    [Theory]
    [InlineData("VM_NOT_FOUND", "Lifecycle")]
    [InlineData("VM_NOT_RUNNING", "Lifecycle")]
    [InlineData("VM_ALREADY_EXISTS", "Lifecycle")]
    [InlineData("BOOTSTRAP_FAILED", "Lifecycle")]
    [InlineData("DESTROY_FAILED", "Lifecycle")]
    [InlineData("HOST_NOT_FOUND", "Remoting")]
    [InlineData("HOST_UNREACHABLE", "Remoting")]
    [InlineData("SESSION_FAILED", "Remoting")]
    [InlineData("COMMAND_TIMEOUT", "Execution")]
    [InlineData("COMMAND_FAILED", "Execution")]
    [InlineData("SCRIPT_FAILED", "Execution")]
    [InlineData("FILE_NOT_FOUND", "File Transfer")]
    [InlineData("TRANSFER_FAILED", "File Transfer")]
    [InlineData("CHECKPOINT_FAILED", "Checkpoints")]
    [InlineData("INVALID_PARAMETER", "Validation")]
    [InlineData("AUTH_FAILED", "Security")]
    [InlineData("INSUFFICIENT_PRIVILEGE", "Security")]
    [InlineData("CONCURRENCY_LIMIT", "Operational")]
    [InlineData("OPERATION_CANCELED", "Operational")]
    public void ErrorCode_Is_Defined_In_Taxonomy(string errorCode, string expectedCategory)
    {
        // Verify the error code constant exists
        var allCodes = typeof(ErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => f.GetValue(null) as string)
            .ToList();

        allCodes.Should().Contain(errorCode,
            $"error code '{errorCode}' ({expectedCategory}) must be defined " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy)");
    }

    /// <summary>
    /// ErrorCodes class must contain exactly 32 error codes matching the taxonomy.
    /// Original 18 + 4 ISO installation codes + MISSING_CREDENTIALS + IO_ERROR
    /// + OS_NOT_SUPPORTED + INSUFFICIENT_RESOURCES
    /// + 4 base-image codes (SYSPREP_FAILED, IMAGE_COPY_FAILED, MERGE_NOT_SUPPORTED,
    /// CHECKPOINT_MERGE_FAILED — Issue #51)
    /// + OPERATION_CANCELED (Issue #164 / LF-D17)
    /// + DEST_DIR_MISSING (Issue #204 / VC-DEST-D2).
    /// </summary>
    [Fact]
    public void ErrorCodes_Contains_Exactly_32_Codes()
    {
        var allCodes = typeof(ErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => f.GetValue(null) as string)
            .ToList();

        var codeCount = allCodes.Count;

        // Issue #204 / VC-DEST-D2 added DEST_DIR_MISSING → 32 total.
        codeCount.Should().Be(32,
            "the error taxonomy defines 32 error codes: original 18 + 4 ISO installation codes + MISSING_CREDENTIALS + IO_ERROR (ST-D7 / Issue #54) " +
            "+ OS_NOT_SUPPORTED + INSUFFICIENT_RESOURCES (Issue #97 / ISO-D16 + ISO-D17) " +
            "+ SYSPREP_FAILED + IMAGE_COPY_FAILED + MERGE_NOT_SUPPORTED + CHECKPOINT_MERGE_FAILED (Issue #51) " +
            "+ OPERATION_CANCELED (Issue #164 / LF-D17) " +
            "+ DEST_DIR_MISSING (Issue #204 / VC-DEST-D2) " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy)");

        allCodes.Should().Contain("DEST_DIR_MISSING",
            "Issue #204 / VC-DEST-D2: DEST_DIR_MISSING must be present in the catalog");
    }

    // ─── JSON Wire Format ──────────────────────────────────────────────

    /// <summary>
    /// Success response JSON must use camelCase property names per System.Text.Json defaults.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D5: Return Task<string> with JSON serialization.
    /// </summary>
    [Fact]
    public void Success_Response_Serializes_To_Expected_Json()
    {
        var response = McpToolResponse.Ok(new { vmId = "test-001", state = "Running" });
        var json = JsonSerializer.Serialize(response);

        json.Should().Contain("\"success\":true");
        json.Should().Contain("\"error\":null");
        json.Should().Contain("\"errorCode\":null");
        json.Should().Contain("\"data\":");
    }

    /// <summary>
    /// Failure response JSON must include errorCode field.
    /// See /myplans/mcp-interface/mcp-interface-design.md — ADR-8: Rich error envelope.
    /// </summary>
    [Fact]
    public void Failure_Response_Serializes_With_ErrorCode()
    {
        var response = McpToolResponse.Fail(
            "VM not found", ErrorCodes.VmNotFound, "Off");

        var json = JsonSerializer.Serialize(response);

        json.Should().Contain("\"success\":false");
        json.Should().Contain("\"errorCode\":\"VM_NOT_FOUND\"");
        json.Should().Contain("\"state\":\"Off\"");
        json.Should().Contain("\"data\":null");
    }

    /// <summary>
    /// Failure response JSON can be deserialized back to McpToolResponse.
    /// This validates round-trip fidelity for MCP protocol messages.
    /// </summary>
    [Fact]
    public void Response_RoundTrips_Through_Json()
    {
        var original = McpToolResponse.Fail(
            "Concurrency limit reached", ErrorCodes.ConcurrencyLimit);

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<McpToolResponse>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Success.Should().Be(original.Success);
        deserialized.Error.Should().Be(original.Error);
        deserialized.ErrorCode.Should().Be(original.ErrorCode);
        deserialized.Data.Should().BeNull();
    }

    // ─── Invalid Request Scenarios ─────────────────────────────────────

    /// <summary>
    /// Missing required vmId parameter should produce INVALID_PARAMETER error.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public void Missing_Required_Parameter_Returns_InvalidParameter()
    {
        // Simulate what a tool handler should return when vmId is missing
        var response = McpToolResponse.Fail(
            "Required parameter 'vmId' is missing",
            ErrorCodes.InvalidParameter);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER");
        response.Error.Should().Contain("vmId");
    }

    /// <summary>
    /// Operations on a non-running VM should produce VM_NOT_RUNNING error.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_NOT_RUNNING.
    /// </summary>
    [Fact]
    public void Operation_On_Stopped_VM_Returns_VmNotRunning()
    {
        var response = McpToolResponse.Fail(
            "VM 'test-vm' exists but is not in Running state",
            ErrorCodes.VmNotRunning,
            "Off");

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("VM_NOT_RUNNING");
        response.State.Should().Be("Off",
            "state should reflect actual VM state for diagnostics");
    }

    /// <summary>
    /// Timeout on command execution should include partial data.
    /// See /myplans/execution/commands/commands-design.md — CMD-D4: Timeout returns success:false with partial output.
    /// See /myplans/design.md §4 — ADR-9.
    /// </summary>
    [Fact]
    public void Command_Timeout_Returns_Failure_With_Partial_Data()
    {
        // Simulate timeout response per ADR-9
        var partialResult = new CommandResult
        {
            ExitCode = -1,
            Stdout = "partial output before timeout...",
            Stderr = "",
            TimedOut = true,
            Truncated = false,
            DurationMs = 30000
        };

        var response = new McpToolResponse
        {
            Success = false,
            Data = partialResult,
            Error = "Command exceeded timeout of 30 seconds",
            ErrorCode = ErrorCodes.CommandTimeout
        };

        response.Success.Should().BeFalse(
            "timed-out commands must return success:false (ADR-9)");
        response.Data.Should().NotBeNull(
            "partial output must be accessible in data field (ADR-9)");
        response.ErrorCode.Should().Be("COMMAND_TIMEOUT");

        var result = response.Data as CommandResult;
        result.Should().NotBeNull();
        result!.TimedOut.Should().BeTrue();
        result.Stdout.Should().NotBeNullOrEmpty(
            "partial output should be included even on timeout");
    }

    /// <summary>
    /// Auth failures should produce AUTH_FAILED error code.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: AUTH_FAILED.
    /// </summary>
    [Fact]
    public void Auth_Failure_Returns_AuthFailed()
    {
        var response = McpToolResponse.Fail(
            "Credential authentication failure for host 'hyperv-01'",
            ErrorCodes.AuthFailed);

        response.ErrorCode.Should().Be("AUTH_FAILED");
    }

    /// <summary>
    /// Duplicate VM creation should produce VM_ALREADY_EXISTS error.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_ALREADY_EXISTS.
    /// </summary>
    [Fact]
    public void Duplicate_VM_Creation_Returns_VmAlreadyExists()
    {
        var response = McpToolResponse.Fail(
            "VM with name 'my-vm' already exists on host 'local'",
            ErrorCodes.VmAlreadyExists);

        response.ErrorCode.Should().Be("VM_ALREADY_EXISTS");
    }
}
