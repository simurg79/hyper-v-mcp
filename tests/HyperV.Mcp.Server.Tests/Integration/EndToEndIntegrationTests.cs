using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// End-to-end integration tests that cover gaps not exercised by EndToEndPipelineTests.
/// Exercises the full tool dispatch chain:
/// DispatchAsync → argument extraction → concurrency gate → service call → response envelope.
/// Uses real ToolDispatcher, ErrorMapper, HostResolver, ConcurrencyGate with mocked services.
///
/// These tests verify additional tool behaviors, input validation edge cases,
/// stub tool handling, and inline command result processing without requiring
/// Hyper-V infrastructure.
/// See /myplans/execution-plan.md — Stage 1.6: Integration Testing.
/// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy.
/// </summary>
[Trait("Category", "Integration")]
public class EndToEndIntegrationTests : IDisposable
{
    /// <summary>
    /// Canonical VM GUIDs for end-to-end tests.
    /// All handlers now call InputValidation.ValidateVmId() which requires valid GUIDs.
    /// </summary>
    private const string VmGuid1 = "11111111-1111-1111-1111-111111111111";
    private const string VmGuid2 = "22222222-2222-2222-2222-222222222222";

    private readonly ServerOptions _serverOptions;
    private readonly HostResolver _hostResolver;
    private readonly ConcurrencyGate _concurrencyGate;
    private readonly ErrorMapper _errorMapper;
    private readonly Mock<IHyperVManager> _mockHyperVManager;
    private readonly Mock<ICommandExecutor> _mockCommandExecutor;
    private readonly Mock<IFileTransferService> _mockFileTransferService;
    private readonly Mock<ICheckpointManager> _mockCheckpointManager;
    private readonly Mock<IPowerShellExecutor> _mockPowerShellExecutor;
    private readonly Mock<IPowerShellDirectChannel> _mockPowerShellDirectChannel;
    private readonly ToolDispatcher _dispatcher;

    public EndToEndIntegrationTests()
    {
        _serverOptions = new ServerOptions
        {
            MaxConcurrentOperations = 10,
            MaxPerHostOperations = 5,
            DefaultHostId = "local",
        };
        _serverOptions.Hosts["local"] = new HostProfile
        {
            HostId = "local",
            ComputerName = "localhost",
            TrustPolicy = "local",
        };

        _hostResolver = new HostResolver(_serverOptions);
        _concurrencyGate = new ConcurrencyGate(_serverOptions);
        _errorMapper = new ErrorMapper();
        _mockHyperVManager = new Mock<IHyperVManager>();
        _mockCommandExecutor = new Mock<ICommandExecutor>();
        _mockFileTransferService = new Mock<IFileTransferService>();
        _mockCheckpointManager = new Mock<ICheckpointManager>();
        _mockPowerShellExecutor = new Mock<IPowerShellExecutor>();

        // Default: GetVmStatusAsync returns a Running VM (needed for vm_run_command, vm_run_script, vm_copy_file, vm_get_file state precondition)
        _mockHyperVManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = VmGuid1, Name = "test-vm", State = "Running", HostId = "local" });

        _mockPowerShellDirectChannel = new Mock<IPowerShellDirectChannel>();

        _dispatcher = new ToolDispatcher(
            _mockHyperVManager.Object,
            _mockCommandExecutor.Object,
            _mockFileTransferService.Object,
            _mockCheckpointManager.Object,
            _hostResolver,
            _errorMapper,
            _concurrencyGate,
            _mockPowerShellExecutor.Object,
            _mockPowerShellDirectChannel.Object,
            _serverOptions);
    }

    public void Dispose()
    {
        _concurrencyGate.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helper methods
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deserializes a JSON response string into a JsonDocument for assertion.
    /// Uses JsonElement-based deserialization to match the McpToolResponse shape.
    /// </summary>
    private static JsonDocument ParseResponse(string json)
    {
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Asserts that the response JSON represents a success envelope with the expected shape.
    /// </summary>
    private static void AssertSuccessResponse(string json)
    {
        using var doc = ParseResponse(json);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue(
            $"Expected success response but got: {json}");
        root.TryGetProperty("error", out var errorProp).Should().BeTrue();
        (errorProp.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(errorProp.GetString()))
            .Should().BeTrue();
    }

    /// <summary>
    /// Asserts that the response JSON represents an error envelope with the expected error code.
    /// </summary>
    private static void AssertErrorResponse(string json, string expectedErrorCode)
    {
        using var doc = ParseResponse(json);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse(
            $"Expected error response but got success: {json}");
        root.GetProperty("errorCode").GetString().Should().Be(expectedErrorCode);
        root.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 1. vm_list_images — Full pipeline (P1 tool with real handler)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies vm_list_images dispatches through the full pipeline and returns
    /// images array with count in the response envelope.
    /// This is a P1 tool with a real handler, not a stub.
    /// See /myplans/vm-management/storage/storage-design.md — Base Image Enumeration.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog: vm_list_images.
    /// </summary>
    [Fact]
    public async Task VmListImages_FullPipeline_ReturnsImagesWithCount()
    {
        // Arrange
        var images = new List<ImageInfo>
        {
            new()
            {
                Name = "Windows Server 2022",
                Path = @"C:\HyperVMCP\Images\ws2022.vhdx",
                SizeGB = 12.5,
                MaxSizeGB = 127.0,
                VhdType = "Dynamic",
                ParentPath = null,
            },
            new()
            {
                Name = "Ubuntu 22.04",
                Path = @"C:\HyperVMCP\Images\ubuntu2204.vhdx",
                SizeGB = 8.2,
                MaxSizeGB = 64.0,
                VhdType = "Dynamic",
                ParentPath = null,
            },
        };

        _mockHyperVManager
            .Setup(m => m.ListImagesAsync("local", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageListResult
            {
                Images = images,
                Count = images.Count,
                Configured = true,
                ImageDir = @"C:\HyperVMCP\Images",
                Hint = null,
            });

        // Act
        var result = await _dispatcher.DispatchAsync("vm_list_images",
            new Dictionary<string, object?>());

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("count").GetInt32().Should().Be(2);

        var imagesArray = data.GetProperty("images");
        imagesArray.GetArrayLength().Should().Be(2);
        imagesArray[0].GetProperty("name").GetString().Should().Be("Windows Server 2022");
        imagesArray[0].GetProperty("sizeGB").GetDouble().Should().Be(12.5);
        imagesArray[1].GetProperty("name").GetString().Should().Be("Ubuntu 22.04");
        imagesArray[1].GetProperty("vhdType").GetString().Should().Be("Dynamic");

        _mockHyperVManager.Verify(
            m => m.ListImagesAsync("local", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. Force stop behavior — vm_stop with force=true
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_stop with force=true passes the force flag through
    /// the full pipeline to the IHyperVManager.StopVmAsync call.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_stop (graceful + force).
    /// </summary>
    [Fact]
    public async Task VmStop_ForceTrue_PassesForceFlagToService()
    {
        // Arrange
        var stoppedVm = new VmInfo
        {
            VmId = VmGuid1,
            Name = "test-vm",
            State = "Off",
            HostId = "local",
            CpuCount = 2,
            MemoryMB = 4096,
        };

        _mockHyperVManager
            .Setup(m => m.StopVmAsync("local", VmGuid1, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stoppedVm);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["force"] = true,
            });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("data").GetProperty("state").GetString().Should().Be("Off");

        _mockHyperVManager.Verify(
            m => m.StopVmAsync("local", VmGuid1, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "Should have called StopVmAsync with force=true");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. Non-zero exit code handling — COMMAND_FAILED
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_run_command with a non-zero exit code returns
    /// success: false with errorCode: COMMAND_FAILED per the review round 2 fix.
    /// The data field must still contain the full CommandResult.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: COMMAND_FAILED.
    /// </summary>
    [Fact]
    public async Task VmRunCommand_NonZeroExitCode_ReturnsCommandFailed()
    {
        // Arrange
        var commandResult = new CommandResult
        {
            ExitCode = 1,
            Stdout = "partial output",
            Stderr = "error: file not found",
            TimedOut = false,
            Cancelled = false,
            Truncated = false,
            DurationMs = 200,
        };

        _mockCommandExecutor
            .Setup(m => m.ExecuteCommandAsync("local", VmGuid1, "cat /nonexistent", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commandResult);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["command"] = "cat /nonexistent",
                ["shell"] = "cmd",
                ["timeoutSeconds"] = 30,
            });

        // Assert
        AssertErrorResponse(result, ErrorCodes.CommandFailed);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("exitCode").GetInt32().Should().Be(1);
        data.GetProperty("stdout").GetString().Should().Be("partial output");
        data.GetProperty("stderr").GetString().Should().Be("error: file not found");
        data.GetProperty("durationMs").GetInt64().Should().Be(200);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. Timed-out command — inline timeout handling
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_run_command with a timed-out CommandResult (TimedOut=true)
    /// returns success: false with errorCode: COMMAND_TIMEOUT.
    /// This tests the inline timeout handling in ToolDispatcher.HandleRunCommandAsync,
    /// not the exception-based path through ErrorMapper.
    /// See /myplans/execution/commands/commands-design.md — CMD-D4: Timeout returns success:false.
    /// </summary>
    [Fact]
    public async Task VmRunCommand_TimedOut_ReturnsCommandTimeout()
    {
        // Arrange
        var commandResult = new CommandResult
        {
            ExitCode = -1,
            Stdout = "partial stdout before timeout",
            Stderr = "",
            TimedOut = true,
            Cancelled = false,
            Truncated = false,
            DurationMs = 30000,
        };

        _mockCommandExecutor
            .Setup(m => m.ExecuteCommandAsync("local", VmGuid1, "long-running-cmd", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commandResult);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["command"] = "long-running-cmd",
                ["shell"] = "cmd",
                ["timeoutSeconds"] = 30,
            });

        // Assert
        AssertErrorResponse(result, ErrorCodes.CommandTimeout);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("stdout").GetString().Should().Be("partial stdout before timeout");
        data.GetProperty("timedOut").GetBoolean().Should().BeTrue();
        data.GetProperty("durationMs").GetInt64().Should().Be(30000);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. Cancelled command — inline cancellation handling
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_run_command with a cancelled CommandResult (Cancelled=true)
    /// returns success: false with errorCode: COMMAND_FAILED.
    /// Cancelled commands use COMMAND_FAILED, not COMMAND_TIMEOUT.
    /// See /myplans/execution/commands/commands-design.md — Timeout and Cancellation.
    /// </summary>
    [Fact]
    public async Task VmRunCommand_Cancelled_ReturnsCommandFailed()
    {
        // Arrange
        var commandResult = new CommandResult
        {
            ExitCode = -1,
            Stdout = "output before cancel",
            Stderr = "",
            TimedOut = false,
            Cancelled = true,
            Truncated = false,
            DurationMs = 5000,
        };

        _mockCommandExecutor
            .Setup(m => m.ExecuteCommandAsync("local", VmGuid1, "cancelled-cmd", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commandResult);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["command"] = "cancelled-cmd",
                ["shell"] = "cmd",
                ["timeoutSeconds"] = 30,
            });

        // Assert
        AssertErrorResponse(result, ErrorCodes.CommandFailed);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("cancelled").GetBoolean().Should().BeTrue();
        data.GetProperty("stdout").GetString().Should().Be("output before cancel");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. Input validation — Invalid vmId format (not a GUID)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that dispatching vm_start with an invalid vmId (not a GUID)
    /// returns INVALID_PARAMETER error. InputValidation.ValidateVmId() throws
    /// ArgumentException which ErrorMapper maps to INVALID_PARAMETER.
    /// See /myplans/security/security-design.md — Input Validation.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task InputValidation_InvalidVmIdFormat_ReturnsInvalidParameter()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?> { ["vmId"] = "not-a-guid" });

        // Assert
        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. Input validation — VM name path traversal attempt
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_create with a path traversal VM name ("../../evil")
    /// returns INVALID_PARAMETER error. InputValidation.ValidateVmName() throws
    /// ArgumentException for names containing ".." or path separators.
    /// See /myplans/security/security-design.md — SEC-D7: VM name path traversal validation.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task InputValidation_VmNamePathTraversal_ReturnsInvalidParameter()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?> { ["name"] = "../../evil" });

        // Assert
        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. Input validation — VM name with control characters
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_create with a VM name containing a null byte (\x00)
    /// returns INVALID_PARAMETER error. InputValidation.ValidateVmName() rejects
    /// names with non-printable ASCII characters.
    /// See /myplans/security/security-design.md — SEC-D7: VM name path traversal validation.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task InputValidation_VmNameWithNullByte_ReturnsInvalidParameter()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?> { ["name"] = "vm\x00test" });

        // Assert
        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 9. vm_wait_ready — P1 tool (now implemented)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_wait_ready dispatches through the full pipeline and returns
    /// a success envelope. This tool was promoted from stub to P1 implementation.
    /// See /myplans/execution-plan.md — Phase 2: P1 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task P1Tool_VmWaitReady_ReturnsSuccess()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_wait_ready",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["timeoutSeconds"] = 300,
            });

        // Assert
        AssertSuccessResponse(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 10. vm_get_file — Guest→Host file transfer (P1 tool, now implemented)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_get_file dispatches through the full pipeline and returns
    /// a success envelope. This tool was promoted from stub to P1 implementation.
    /// See /myplans/execution-plan.md — Phase 2: P1 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task P1Tool_VmGetFile_ReturnsSuccess()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_get_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["sourcePath"] = @"C:\guest\file.txt",
                ["destPath"] = @"C:\host\file.txt",
            });

        // Assert
        AssertSuccessResponse(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 11. vm_run_script — Full pipeline (P1 tool)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies vm_run_script dispatches through the full pipeline and returns
    /// a success envelope with stdout, stderr, and exitCode in the data.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1.
    /// See /myplans/execution-plan.md — Phase 2: P1 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task VmRunScript_FullPipeline_ReturnsCommandResult()
    {
        // Arrange
        var commandResult = new CommandResult
        {
            ExitCode = 0,
            Stdout = "script output line 1\nscript output line 2",
            Stderr = "",
            TimedOut = false,
            Cancelled = false,
            Truncated = false,
            DurationMs = 1500,
        };

        _mockCommandExecutor
            .Setup(m => m.ExecuteScriptAsync("local", VmGuid1, "Get-Process | Select-Object -First 5",
                "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commandResult);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["script"] = "Get-Process | Select-Object -First 5",
                ["shell"] = "powershell",
                ["timeoutSeconds"] = 60,
            });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("exitCode").GetInt32().Should().Be(0);
        data.GetProperty("stdout").GetString().Should().Be("script output line 1\nscript output line 2");
        data.GetProperty("stderr").GetString().Should().BeEmpty();
        data.GetProperty("durationMs").GetInt64().Should().Be(1500);

        _mockCommandExecutor.Verify(
            m => m.ExecuteScriptAsync("local", VmGuid1, "Get-Process | Select-Object -First 5",
                "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that vm_run_script with a non-zero exit code returns
    /// success: false with errorCode: COMMAND_FAILED per the same pattern as vm_run_command.
    /// See /myplans/execution/commands/commands-design.md — CMD-D4: Non-zero exit returns success:false.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: COMMAND_FAILED.
    /// </summary>
    [Fact]
    public async Task VmRunScript_FullPipeline_NonZeroExitCode_ReturnsCommandFailed()
    {
        // Arrange
        var commandResult = new CommandResult
        {
            ExitCode = 1,
            Stdout = "partial script output",
            Stderr = "error: access denied",
            TimedOut = false,
            Cancelled = false,
            Truncated = false,
            DurationMs = 500,
        };

        _mockCommandExecutor
            .Setup(m => m.ExecuteScriptAsync("local", VmGuid1, "Get-RestrictedData",
                "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(commandResult);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["script"] = "Get-RestrictedData",
                ["shell"] = "powershell",
                ["timeoutSeconds"] = 60,
            });

        // Assert
        AssertErrorResponse(result, ErrorCodes.CommandFailed);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("exitCode").GetInt32().Should().Be(1);
        data.GetProperty("stdout").GetString().Should().Be("partial script output");
        data.GetProperty("stderr").GetString().Should().Be("error: access denied");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 12. vm_restart — Full pipeline (P1 tool)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies vm_restart dispatches through the full pipeline and returns
    /// VM info in the success envelope.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_restart.
    /// See /myplans/execution-plan.md — Phase 2: P1 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task VmRestart_FullPipeline_ReturnsVmInfo()
    {
        // Arrange
        var vmInfo = new VmInfo
        {
            VmId = VmGuid1,
            Name = "test-vm",
            State = "Running",
            HostId = "local",
            CpuCount = 2,
            MemoryMB = 4096,
            UptimeSeconds = 5,
        };

        _mockHyperVManager
            .Setup(m => m.RestartVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(vmInfo);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_restart",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
            });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("vmId").GetString().Should().Be(VmGuid1);
        data.GetProperty("name").GetString().Should().Be("test-vm");
        data.GetProperty("state").GetString().Should().Be("Running");
        data.GetProperty("uptimeSeconds").GetInt64().Should().Be(5);

        _mockHyperVManager.Verify(
            m => m.RestartVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that vm_restart with a non-existent VM returns
    /// success: false with errorCode: VM_NOT_FOUND.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_NOT_FOUND.
    /// </summary>
    [Fact]
    public async Task VmRestart_FullPipeline_VmNotFound_ReturnsError()
    {
        // Arrange
        _mockHyperVManager
            .Setup(m => m.RestartVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", VmGuid1));

        // Act
        var result = await _dispatcher.DispatchAsync("vm_restart",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
            });

        // Assert
        AssertErrorResponse(result, ErrorCodes.VmNotFound);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 13. vm_checkpoint — Full pipeline (P1 tool)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies vm_checkpoint with action="create" dispatches through the full pipeline
    /// and returns a success envelope with the checkpoint result.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
    /// See /myplans/execution-plan.md — Phase 2: P1 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_Create_FullPipeline_ReturnsCheckpointResult()
    {
        // Arrange
        var checkpointResult = new CheckpointResult
        {
            Action = "create",
            VmId = VmGuid1,
            CheckpointName = "test-checkpoint",
            Checkpoints = null,
        };

        _mockCheckpointManager
            .Setup(m => m.CreateCheckpointAsync("local", VmGuid1, "test-checkpoint",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(checkpointResult);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["action"] = "create",
                ["name"] = "test-checkpoint",
            });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("action").GetString().Should().Be("create");
        data.GetProperty("vmId").GetString().Should().Be(VmGuid1);
        data.GetProperty("checkpointName").GetString().Should().Be("test-checkpoint");

        _mockCheckpointManager.Verify(
            m => m.CreateCheckpointAsync("local", VmGuid1, "test-checkpoint",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies vm_checkpoint with action="list" dispatches through the full pipeline
    /// and returns a success envelope with the checkpoint list.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_List_FullPipeline_ReturnsCheckpointList()
    {
        // Arrange
        var checkpointResult = new CheckpointResult
        {
            Action = "list",
            VmId = VmGuid1,
            CheckpointName = string.Empty,
            Checkpoints = new List<CheckpointInfo>
            {
                new()
                {
                    Name = "checkpoint-1",
                    Id = "aaaa-bbbb-cccc",
                    CreatedAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero),
                },
                new()
                {
                    Name = "checkpoint-2",
                    Id = "dddd-eeee-ffff",
                    CreatedAt = new DateTimeOffset(2026, 1, 16, 14, 0, 0, TimeSpan.Zero),
                },
            },
        };

        _mockCheckpointManager
            .Setup(m => m.ListCheckpointsAsync("local", VmGuid1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(checkpointResult);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["action"] = "list",
            });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("action").GetString().Should().Be("list");
        var checkpoints = data.GetProperty("checkpoints");
        checkpoints.GetArrayLength().Should().Be(2);
        checkpoints[0].GetProperty("name").GetString().Should().Be("checkpoint-1");
        checkpoints[1].GetProperty("name").GetString().Should().Be("checkpoint-2");

        _mockCheckpointManager.Verify(
            m => m.ListCheckpointsAsync("local", VmGuid1,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies vm_checkpoint with an invalid action returns
    /// success: false with errorCode: INVALID_PARAMETER.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_InvalidAction_FullPipeline_ReturnsError()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["action"] = "invalid",
            });

        // Assert
        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 14. vm_cleanup_orphans — Full pipeline (P1 tool)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies vm_cleanup_orphans with dryRun=true dispatches through the full pipeline
    /// and returns a success envelope with orphan list, count, and dryRun flag.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Orphan Cleanup.
    /// See /myplans/execution-plan.md — Phase 2: P1 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task VmCleanupOrphans_DryRun_FullPipeline_ReturnsOrphanList()
    {
        // Arrange
        var orphans = new List<VmInfo>
        {
            new()
            {
                VmId = VmGuid1,
                Name = "orphan-vm-1",
                State = "Running",
                HostId = "local",
                CpuCount = 2,
                MemoryMB = 4096,
                UptimeSeconds = 90000,
            },
            new()
            {
                VmId = VmGuid2,
                Name = "orphan-vm-2",
                State = "Off",
                HostId = "local",
                CpuCount = 1,
                MemoryMB = 2048,
                UptimeSeconds = 0,
            },
        };

        _mockHyperVManager
            .Setup(m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orphans);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_cleanup_orphans",
            new Dictionary<string, object?>
            {
                ["dryRun"] = true,
            });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("count").GetInt32().Should().Be(2);
        data.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        data.GetProperty("action").GetString().Should().Be("detected");

        var orphansArray = data.GetProperty("orphans");
        orphansArray.GetArrayLength().Should().Be(2);
        orphansArray[0].GetProperty("name").GetString().Should().Be("orphan-vm-1");
        orphansArray[1].GetProperty("name").GetString().Should().Be("orphan-vm-2");

        _mockHyperVManager.Verify(
            m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies vm_cleanup_orphans with dryRun=false dispatches through the full pipeline
    /// and returns a success envelope with destroyed VM list and dryRun=false.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Orphan Cleanup.
    /// </summary>
    [Fact]
    public async Task VmCleanupOrphans_Execute_FullPipeline_ReturnsDestroyedList()
    {
        // Arrange
        // LF-D10: action="destroyed" requires at least one row classified as
        // Reason="orphan" in non-dryRun mode. Rows without a Reason (or
        // Reason="unknown-age") are report-only and yield action="detected".
        // The HyperVManager PowerShell pipeline tags actually-destroyed rows
        // with Reason="orphan"; the mock must mirror that contract.
        var destroyed = new List<VmInfo>
        {
            new()
            {
                VmId = VmGuid1,
                Name = "orphan-vm-1",
                State = "Off",
                HostId = "local",
                CpuCount = 2,
                MemoryMB = 4096,
                UptimeSeconds = 0,
                Reason = "orphan",
            },
        };

        _mockHyperVManager
            .Setup(m => m.CleanupOrphansAsync("local", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(destroyed);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_cleanup_orphans",
            new Dictionary<string, object?>
            {
                ["dryRun"] = false,
            });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("count").GetInt32().Should().Be(1);
        data.GetProperty("dryRun").GetBoolean().Should().BeFalse();
        data.GetProperty("action").GetString().Should().Be("destroyed");

        var orphansArray = data.GetProperty("orphans");
        orphansArray.GetArrayLength().Should().Be(1);
        orphansArray[0].GetProperty("vmId").GetString().Should().Be(VmGuid1);

        _mockHyperVManager.Verify(
            m => m.CleanupOrphansAsync("local", false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 15. vm_pause and vm_resume — Stub lifecycle operations
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_pause dispatches to the real handler (no longer a stub).
    /// The mock IHyperVManager returns null by default, which is wrapped as success.
    /// </summary>
    [Fact]
    public async Task VmPause_DispatchesToHandler()
    {
        // Arrange
        var expected = new VmInfo { VmId = VmGuid1, Name = "test-vm", State = "Paused", HostId = "local" };
        _mockHyperVManager.Setup(m => m.PauseVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_pause",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });

        // Assert
        var root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        _mockHyperVManager.Verify(
            m => m.PauseVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that vm_resume dispatches to the real handler and invokes ResumeVmAsync.
    /// </summary>
    [Fact]
    public async Task VmResume_DispatchesToHandler()
    {
        // Arrange
        var expected = new VmInfo { VmId = VmGuid1, Name = "test-vm", State = "Running", HostId = "local" };
        _mockHyperVManager.Setup(m => m.ResumeVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_resume",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });

        // Assert
        var root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        _mockHyperVManager.Verify(
            m => m.ResumeVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 16. vm_configure — Stub configuration operation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_configure with neither cpuCount nor memoryMB returns
    /// INVALID_PARAMETER (precondition failure). Replaces the previous
    /// StubTool_VmConfigure_ReturnsInternalError test which asserted the
    /// pre-fix behavior; vm_configure now has a real handler (Issue #56).
    /// See GitHub Issue #56.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog: vm_configure (P2).
    /// </summary>
    [Fact]
    public async Task VmConfigure_MissingBothSettings_ReturnsInvalidParameter()
    {
        // Arrange — happy-path mock so we can detect any unexpected delegation.
        _mockHyperVManager
            .Setup(m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = VmGuid1, Name = "x", State = "Off", HostId = "local" });

        // Act — neither cpuCount nor memoryMB provided.
        var result = await _dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
            });

        // Assert
        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
        _mockHyperVManager.Verify(
            m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ConfigureVmAsync must NOT be called when both optional settings are null");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 17. Multi-tool sequencing — List then Status flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies sequential dispatch: vm_list followed by vm_status using a VM ID
    /// extracted from the first response. The second dispatch uses the parsed VM ID
    /// rather than a preselected constant, ensuring the list response shape and
    /// serialization are also validated as part of the chain.
    /// Concurrency-release behavior is tested separately in
    /// EndToEndPipelineTests.ConcurrencyLimit_MaxOne_SecondOperationWaitsForFirst().
    /// </summary>
    [Fact]
    public async Task MultiToolSequence_ListThenStatus_BothSucceed()
    {
        // Arrange
        _mockHyperVManager
            .Setup(m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>
            {
                new() { VmId = VmGuid1, Name = "vm-alpha", State = "Running", HostId = "local" },
                new() { VmId = VmGuid2, Name = "vm-beta", State = "Off", HostId = "local" },
            });

        _mockHyperVManager
            .Setup(m => m.GetVmStatusAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo
            {
                VmId = VmGuid1,
                Name = "vm-alpha",
                State = "Running",
                HostId = "local",
                CpuCount = 4,
                MemoryMB = 8192,
                UptimeSeconds = 3600,
            });

        // Act — Step 1: list VMs
        var listResult = await _dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?>());

        // Assert list succeeded and extract first VM ID from response
        AssertSuccessResponse(listResult);
        string extractedVmId;
        using (var doc = ParseResponse(listResult))
        {
            var data = doc.RootElement.GetProperty("data");
            data.GetProperty("count").GetInt32().Should().Be(2);
            var vmsArray = data.GetProperty("vms");
            vmsArray.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
            extractedVmId = vmsArray[0].GetProperty("vmId").GetString()!;
            extractedVmId.Should().NotBeNullOrEmpty("first VM in the list must have a vmId");
        }

        // Act — Step 2: get status using the VM ID extracted from the list response
        var statusResult = await _dispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?> { ["vmId"] = extractedVmId });

        // Assert status succeeded with data matching the extracted VM
        AssertSuccessResponse(statusResult);
        using (var doc = ParseResponse(statusResult))
        {
            var data = doc.RootElement.GetProperty("data");
            data.GetProperty("vmId").GetString().Should().Be(extractedVmId);
            data.GetProperty("name").GetString().Should().Be("vm-alpha");
            data.GetProperty("uptimeSeconds").GetInt64().Should().Be(3600);
        }

        // Verify both calls were made
        _mockHyperVManager.Verify(
            m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()), Times.Once);
        _mockHyperVManager.Verify(
            m => m.GetVmStatusAsync("local", extractedVmId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 18. vm_echo with empty message
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_echo with an explicit empty-string message returns success
    /// with data.message as an empty string. This covers only the empty-string case;
    /// the missing-key case is tested separately in VmEcho_MissingMessageKey.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog: vm_echo.
    /// </summary>
    [Fact]
    public async Task VmEcho_EmptyMessage_ReturnsSuccessWithEmptyMessage()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = "" });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("data").GetProperty("message").GetString()
            .Should().BeEmpty();
    }

    /// <summary>
    /// Verifies that vm_echo with no "message" key in the arguments dictionary
    /// returns success with data.message defaulting to an empty string.
    /// This is the missing-argument case, complementing the explicit empty-string test above.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog: vm_echo.
    /// </summary>
    [Fact]
    public async Task VmEcho_MissingMessageKey_ReturnsSuccessWithEmptyMessage()
    {
        // Act — no "message" key at all
        var result = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?>());

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("data").GetProperty("message").GetString()
            .Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 19. vm_echo with special characters
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_echo with a message containing JSON special characters
    /// (braces, quotes, colons) round-trips correctly through JSON serialization.
    /// This ensures the response envelope handles special characters without corruption.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D2: Consistent response envelope.
    /// </summary>
    [Fact]
    public async Task VmEcho_SpecialCharacters_ReturnsCorrectMessage()
    {
        // Arrange
        const string specialMessage = "{\"key\": \"value\"}";

        // Act
        var result = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = specialMessage });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("data").GetProperty("message").GetString()
            .Should().Be(specialMessage);
    }
}
