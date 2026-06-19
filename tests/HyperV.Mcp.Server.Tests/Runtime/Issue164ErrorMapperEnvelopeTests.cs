using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #164 / LF-D17: <see cref="ErrorMapper"/> envelope-shape tests for
/// <see cref="VmCreateRollbackException"/>.
///
/// Verifies that <c>ErrorMapper.MapException</c> produces an
/// <see cref="McpToolResponse"/> whose:
/// - <c>Success</c> is <c>false</c>.
/// - <c>ErrorCode</c> matches <see cref="VmCreateRollbackException.ErrorCode"/>
///   across all three legal values (<c>OPERATION_CANCELED</c>,
///   <c>COMMAND_TIMEOUT</c>, <c>COMMAND_FAILED</c>).
/// - <c>Details</c> serializes to the LF-D17 contract:
///   <c>{ vmName, phase, rollback: { performed, succeeded, elapsedMs, residualArtifacts } }</c>.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue164ErrorMapperEnvelopeTests
{
    private readonly ErrorMapper _mapper = new();

    private static JsonElement Roundtrip(McpToolResponse response)
    {
        var json = JsonSerializer.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData(ErrorCodes.OperationCanceled)]
    [InlineData(ErrorCodes.CommandTimeout)]
    [InlineData(ErrorCodes.CommandFailed)]
    public void MapException_VmCreateRollback_Produces_LfD17_Envelope(string errorCode)
    {
        var rollback = new VmCreateRollbackInfo
        {
            Performed = true,
            Succeeded = true,
            ElapsedMs = 1234,
            ResidualArtifacts = new[] { @"C:\HyperVMCP\VMs\test\test.vhdx" },
        };
        var ex = new VmCreateRollbackException(
            vmName: "test-vm-164",
            errorCode: errorCode,
            phase: "register",
            rollback: rollback,
            message: "vm_create failed and was rolled back.");

        var response = _mapper.MapException(ex);

        response.Should().NotBeNull();
        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(errorCode,
            "LF-D17: VmCreateRollbackException.ErrorCode must drive the envelope errorCode");
        response.Error.Should().Contain("rolled back");
        response.Details.Should().NotBeNull("LF-D17 requires a structured details block");

        // Serialize to JSON and assert the property graph matches LF-D17 verbatim.
        var root = Roundtrip(response);

        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("errorCode").GetString().Should().Be(errorCode);

        var details = root.GetProperty("details");
        details.GetProperty("vmName").GetString().Should().Be("test-vm-164");
        details.GetProperty("phase").GetString().Should().Be("register");

        var rb = details.GetProperty("rollback");
        rb.GetProperty("performed").GetBoolean().Should().BeTrue();
        rb.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        rb.GetProperty("elapsedMs").GetInt64().Should().Be(1234);

        rb.GetProperty("residualArtifacts").ValueKind.Should().Be(JsonValueKind.Array);
        rb.GetProperty("residualArtifacts").EnumerateArray()
            .Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { @"C:\HyperVMCP\VMs\test\test.vhdx" });
    }

    [Fact]
    public void MapException_VmCreateRollback_SucceededFalse_Surfaces_Through_Details()
    {
        var rollback = new VmCreateRollbackInfo
        {
            Performed = true,
            Succeeded = false,
            ElapsedMs = 31000,
            ResidualArtifacts = new[] { @"C:\HyperVMCP\VMs\bad\bad.vhdx", @"C:\HyperVMCP\VMs\bad" },
        };
        var ex = new VmCreateRollbackException(
            vmName: "bad-vm",
            errorCode: ErrorCodes.OperationCanceled,
            phase: "create",
            rollback: rollback,
            message: "vm_create cancelled; rollback exceeded budget.");

        var response = _mapper.MapException(ex);
        var root = Roundtrip(response);

        var rb = root.GetProperty("details").GetProperty("rollback");
        rb.GetProperty("performed").GetBoolean().Should().BeTrue();
        rb.GetProperty("succeeded").GetBoolean().Should().BeFalse(
            "budget-exceeded rollback must surface succeeded=false through the envelope");
        rb.GetProperty("residualArtifacts").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void MapException_VmCreateRollback_EmptyResidual_Serializes_As_EmptyArray()
    {
        var ex = new VmCreateRollbackException(
            vmName: "ok-vm",
            errorCode: ErrorCodes.CommandFailed,
            phase: "create",
            rollback: new VmCreateRollbackInfo
            {
                Performed = true,
                Succeeded = true,
                ElapsedMs = 50,
                ResidualArtifacts = Array.Empty<string>(),
            },
            message: "vm_create failed; rollback clean.");

        var response = _mapper.MapException(ex);
        var root = Roundtrip(response);

        var residual = root.GetProperty("details")
            .GetProperty("rollback")
            .GetProperty("residualArtifacts");
        residual.ValueKind.Should().Be(JsonValueKind.Array,
            "AC#2: residualArtifacts must always serialize as a JSON array, never null");
        residual.GetArrayLength().Should().Be(0);
    }
}
