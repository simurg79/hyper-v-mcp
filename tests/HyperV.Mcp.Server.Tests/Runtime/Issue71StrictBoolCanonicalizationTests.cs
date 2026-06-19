using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #71 / MCP-D9 — tighten <c>GetStrictBoolArg</c> to reject non-canonical
/// string values such as <c>"True"</c>, <c>"FALSE"</c>, <c>" true "</c>, <c>"1"</c>.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D9.
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/71.
///
/// Contract (option B from triage): accept JSON bool, .NET <c>bool</c>,
/// <see cref="JsonElement"/>.True/.False, and EXACT-lowercase string
/// <c>"true"</c>/<c>"false"</c> (raw or as a <see cref="JsonElement"/> of kind
/// <see cref="JsonValueKind.String"/>); reject everything else with
/// <c>INVALID_PARAMETER</c>.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue71StrictBoolCanonicalizationTests
{
    private const string TestVmGuid = "12345678-1234-1234-1234-123456789abc";
    private const string LocalHostId = "local";

    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IConcurrencyGate> _gate = new();

    private ToolDispatcher CreateDispatcher()
    {
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        _hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = LocalHostId });

        return new ToolDispatcher(
            _hvManager.Object,
            _commandExecutor.Object,
            _fileTransfer.Object,
            new Mock<ICheckpointManager>().Object,
            _hostResolver.Object,
            new ErrorMapper(),
            _gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            new ServerOptions { DefaultHostId = LocalHostId });
    }

    private static JsonElement JsonString(string s)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s));
        return doc.RootElement.Clone();
    }

    // ── Non-canonical string values that MUST be rejected ───────────────

    public static IEnumerable<object[]> NonCanonicalStringValues()
    {
        // Case variants
        yield return new object[] { "True" };
        yield return new object[] { "TRUE" };
        yield return new object[] { "FALSE" };
        yield return new object[] { "False" };
        // Whitespace-padded
        yield return new object[] { " true " };
        yield return new object[] { "true " };
        yield return new object[] { " true" };
        // Numeric strings
        yield return new object[] { "1" };
        yield return new object[] { "0" };
        // Empty
        yield return new object[] { "" };
        // Other truthy-ish
        yield return new object[] { "yes" };
        yield return new object[] { "no" };
        // JsonElement-of-kind-String with value "True" (actual MCP transport shape).
        yield return new object[] { JsonString("True") };
    }

    [Theory]
    [MemberData(nameof(NonCanonicalStringValues))]
    public async Task VmStop_NonCanonicalStringForce_Returns_InvalidParameter(object badForce)
    {
        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync(
            "vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = LocalHostId,
                ["force"] = badForce,
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse(
            $"non-canonical string force value '{badForce}' must be rejected under MCP-D9 / Issue #71.");
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
        response.Error.Should().Contain("force");

        _hvManager.Verify(
            m => m.StopVmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [MemberData(nameof(NonCanonicalStringValues))]
    public async Task VmCopyFile_NonCanonicalStringIsDirectory_Returns_InvalidParameter(object badValue)
    {
        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync(
            "vm_copy_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = LocalHostId,
                ["sourcePath"] = @"C:\does-not-matter.txt",
                ["destPath"] = @"C:\guest\file.txt",
                ["isDirectory"] = badValue,
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
        response.Error.Should().Contain("isDirectory");

        _fileTransfer.Verify(
            f => f.CopyToGuestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Canonical lowercase strings that MUST be accepted (option B) ────

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task VmStop_RawLowercaseStringForce_RoutesNormally(string raw, bool expectedForce)
    {
        _hvManager.Setup(m => m.StopVmAsync(LocalHostId, TestVmGuid, expectedForce, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Off", HostId = LocalHostId });

        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync(
            "vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = LocalHostId,
                ["force"] = raw,
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeTrue(
            $"raw lowercase string '{raw}' must be accepted as canonical (option B).");
        _hvManager.Verify(
            m => m.StopVmAsync(LocalHostId, TestVmGuid, expectedForce, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task VmStop_JsonStringLowercaseForce_RoutesNormally(string s, bool expectedForce)
    {
        _hvManager.Setup(m => m.StopVmAsync(LocalHostId, TestVmGuid, expectedForce, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Off", HostId = LocalHostId });

        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync(
            "vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = LocalHostId,
                ["force"] = JsonString(s),
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeTrue(
            $"JsonElement-of-kind-String '{s}' must be accepted as canonical (option B).");
        _hvManager.Verify(
            m => m.StopVmAsync(LocalHostId, TestVmGuid, expectedForce, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
