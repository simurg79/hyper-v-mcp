using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #63 / MCP-D9 — strict boolean argument parsing for <c>vm_stop.force</c>
/// and <c>vm_copy_file.isDirectory</c>.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D9.
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/63.
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/72 — adds the
/// explicit <c>null</c> row to both theories.
///
/// Non-canonical bool values (typos, numbers, JSON object, <c>null</c>) must
/// produce a structured INVALID_PARAMETER envelope identifying the offending
/// parameter instead of being silently coerced to <c>false</c>. Canonical
/// <c>true</c> / <c>false</c> (and JSON booleans) must continue to route
/// through normally.
///
/// Note: case- and whitespace-variant string forms (<c>"True"</c>,
/// <c>" true "</c>, <c>"1"</c>, <c>"0"</c>, <c>""</c>, <c>"yes"</c>,
/// <c>"no"</c>, JsonElement-of-kind-String, …) are exhaustively covered by
/// <see cref="Issue71StrictBoolCanonicalizationTests"/>. The row-split between
/// these two files is intentional: this file owns the dispatcher-level
/// INVALID_PARAMETER envelope shape (incl. <c>null</c>), Issue71's file owns
/// the canonicalization matrix.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue63StrictBoolArgsTests
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

        // VM is Running for vm_copy_file's pre-flight check.
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

    // ── vm_stop.force ───────────────────────────────────────────────────

    public static IEnumerable<object?[]> NonCanonicalForceValues => new object?[][]
    {
        new object?[] { "yse" },                  // typo
        new object?[] { 1.5 },                    // float
        new object?[] { new Dictionary<string, object?> { ["nested"] = true } }, // JSON object
        new object?[] { null },                   // explicit null (Issue #72 AC row)
    };

    [Theory]
    [MemberData(nameof(NonCanonicalForceValues))]
    public async Task VmStop_NonCanonicalForce_Returns_InvalidParameter(object? badForce)
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
            $"non-canonical force value '{badForce ?? "<null>"}' must produce a structured failure envelope (MCP-D9 / Issue #63).");
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
        response.Error.Should().Contain("force",
            "INVALID_PARAMETER messages must name the offending parameter.");

        _hvManager.Verify(
            m => m.StopVmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "manager must not be reached when force fails strict parsing.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VmStop_BooleanForce_RoutesNormally(bool force)
    {
        _hvManager.Setup(m => m.StopVmAsync(LocalHostId, TestVmGuid, force, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Off", HostId = LocalHostId });

        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync(
            "vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = LocalHostId,
                ["force"] = force,
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeTrue();
        _hvManager.Verify(
            m => m.StopVmAsync(LocalHostId, TestVmGuid, force, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task VmStop_JsonElementBool_RoutesNormally(string jsonLiteral, bool expectedForce)
    {
        // Simulate JSON-parsed booleans (the MCP transport actually delivers JsonElement).
        using var doc = JsonDocument.Parse(jsonLiteral);
        var je = doc.RootElement.Clone();

        _hvManager.Setup(m => m.StopVmAsync(LocalHostId, TestVmGuid, expectedForce, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Off", HostId = LocalHostId });

        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync(
            "vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = LocalHostId,
                ["force"] = je,
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeTrue(
            "canonical JSON-element booleans must continue to route through normally (regression guard).");
    }

    // ── vm_copy_file.isDirectory ────────────────────────────────────────

    public static IEnumerable<object?[]> NonCanonicalIsDirectoryValues => new object?[][]
    {
        new object?[] { "maybe" },
        new object?[] { 1.5 },
        new object?[] { new Dictionary<string, object?> { ["nested"] = true } },
        new object?[] { null },                   // explicit null (Issue #72 AC row)
    };

    [Theory]
    [MemberData(nameof(NonCanonicalIsDirectoryValues))]
    public async Task VmCopyFile_NonCanonicalIsDirectory_Returns_InvalidParameter(object? badValue)
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
        response.Error.Should().Contain("isDirectory",
            "INVALID_PARAMETER must name 'isDirectory' so the caller can fix it (Issue #63).");

        _fileTransfer.Verify(
            f => f.CopyToGuestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "file-transfer service must not be reached when isDirectory fails strict parsing.");
    }

    /// <summary>
    /// Regression: canonical <c>isDirectory=false</c> still routes through to the
    /// file-existence early validation. We assert the existing FILE_NOT_FOUND
    /// envelope (rather than the parsing-failure envelope) is produced — proving
    /// the strict-bool change did NOT regress the happy path.
    /// </summary>
    [Fact]
    public async Task VmCopyFile_BooleanIsDirectoryFalse_ContinuesPipeline()
    {
        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync(
            "vm_copy_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = LocalHostId,
                ["sourcePath"] = @"C:\definitely\does\not\exist\file.txt",
                ["destPath"] = @"C:\guest\file.txt",
                ["isDirectory"] = false,
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.FileNotFound,
            "isDirectory=false must keep routing through; the failure here is the missing source file, not arg parsing.");
    }
}
