using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// End-to-end pipeline tests that exercise the full tool dispatch chain:
/// DispatchAsync → argument extraction → concurrency gate → service call → response envelope.
/// Uses real ToolDispatcher, ErrorMapper, HostResolver, ConcurrencyGate with mocked services.
///
/// These tests verify the complete pipeline without requiring Hyper-V infrastructure.
/// See /myplans/execution-plan.md — Stage 1.6: Integration Testing.
/// </summary>
[Trait("Category", "Integration")]
public class EndToEndPipelineTests : IDisposable
{
    /// <summary>
    /// Canonical VM GUIDs for end-to-end tests.
    /// All handlers now call InputValidation.ValidateVmId() which requires valid GUIDs.
    /// </summary>
    private const string VmGuid1 = "11111111-1111-1111-1111-111111111111";
    private const string VmGuid2 = "22222222-2222-2222-2222-222222222222";
    private const string NonExistentVmGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private readonly ServerOptions _serverOptions;
    private readonly HostResolver _hostResolver;
    private readonly ConcurrencyGate _concurrencyGate;
    private readonly ErrorMapper _errorMapper;
    private readonly Mock<IHyperVManager> _mockHyperVManager;
    private readonly Mock<ICommandExecutor> _mockCommandExecutor;
    private readonly Mock<IFileTransferService> _mockFileTransferService;
    private readonly ToolDispatcher _dispatcher;

    public EndToEndPipelineTests()
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

        // Default: GetVmStatusAsync returns a Running VM (needed for vm_run_command, vm_copy_file state precondition)
        _mockHyperVManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = VmGuid1, Name = "test-vm", State = "Running", HostId = "local" });

        _dispatcher = new ToolDispatcher(
            _mockHyperVManager.Object,
            _mockCommandExecutor.Object,
            _mockFileTransferService.Object,
            new Mock<ICheckpointManager>().Object,
            _hostResolver,
            _errorMapper,
            _concurrencyGate,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
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
    /// Deserializes a JSON response string into a dictionary for assertion.
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
    // 1. Full VM Lifecycle Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Exercises the full VM lifecycle: Create → Start → Status → Stop → Destroy.
    /// Each step goes through the complete dispatch pipeline with concurrency gate
    /// and mocked service layer, verifying correct response shapes.
    /// See /myplans/execution-plan.md — Stage 1.6: Full VM lifecycle flow.
    /// </summary>
    [Fact]
    public async Task FullVmLifecycle_CreateStartStatusStopDestroy_AllSucceed()
    {
        // Arrange — set up mock returns for the full lifecycle
        var createdVm = new VmInfo
        {
            VmId = VmGuid1,
            Name = "test-vm",
            State = "Off",
            HostId = "local",
            CpuCount = 2,
            MemoryMB = 4096,
        };
        var startedVm = new VmInfo
        {
            VmId = VmGuid1,
            Name = "test-vm",
            State = "Running",
            HostId = "local",
            CpuCount = 2,
            MemoryMB = 4096,
            UptimeSeconds = 5,
        };
        var statusVm = new VmInfo
        {
            VmId = VmGuid1,
            Name = "test-vm",
            State = "Running",
            HostId = "local",
            CpuCount = 2,
            MemoryMB = 4096,
            UptimeSeconds = 120,
        };
        var stoppedVm = new VmInfo
        {
            VmId = VmGuid1,
            Name = "test-vm",
            State = "Off",
            HostId = "local",
            CpuCount = 2,
            MemoryMB = 4096,
            UptimeSeconds = 0,
        };

        _mockHyperVManager
            .Setup(m => m.CreateVmAsync("local", "test-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdVm);
        _mockHyperVManager
            .Setup(m => m.StartVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(startedVm);
        _mockHyperVManager
            .Setup(m => m.GetVmStatusAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(statusVm);
        _mockHyperVManager
            .Setup(m => m.StopVmAsync("local", VmGuid1, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stoppedVm);
        _mockHyperVManager
            .Setup(m => m.DestroyVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act & Assert — Create
        var createResult = await _dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?> { ["name"] = "test-vm", ["autoStart"] = false });
        AssertSuccessResponse(createResult);
        using (var doc = ParseResponse(createResult))
        {
            doc.RootElement.GetProperty("data").GetProperty("vmId").GetString().Should().Be(VmGuid1);
            doc.RootElement.GetProperty("data").GetProperty("state").GetString().Should().Be("Off");
        }

        // Act & Assert — Start
        var startResult = await _dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });
        AssertSuccessResponse(startResult);
        using (var doc = ParseResponse(startResult))
        {
            doc.RootElement.GetProperty("data").GetProperty("state").GetString().Should().Be("Running");
        }

        // Act & Assert — Status
        var statusResult = await _dispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });
        AssertSuccessResponse(statusResult);
        using (var doc = ParseResponse(statusResult))
        {
            doc.RootElement.GetProperty("data").GetProperty("uptimeSeconds").GetInt64().Should().Be(120);
        }

        // Act & Assert — Stop
        var stopResult = await _dispatcher.DispatchAsync("vm_stop",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });
        AssertSuccessResponse(stopResult);
        using (var doc = ParseResponse(stopResult))
        {
            doc.RootElement.GetProperty("data").GetProperty("state").GetString().Should().Be("Off");
        }

        // Act & Assert — Destroy
        var destroyResult = await _dispatcher.DispatchAsync("vm_destroy",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });
        AssertSuccessResponse(destroyResult);
        using (var doc = ParseResponse(destroyResult))
        {
            doc.RootElement.GetProperty("data").GetProperty("destroyed").GetBoolean().Should().BeTrue();
        }

        // Verify all service calls were made in sequence
        _mockHyperVManager.Verify(m => m.CreateVmAsync("local", "test-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()), Times.Once);
        _mockHyperVManager.Verify(m => m.StartVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()), Times.Once);
        _mockHyperVManager.Verify(m => m.GetVmStatusAsync("local", VmGuid1, It.IsAny<CancellationToken>()), Times.Once);
        _mockHyperVManager.Verify(m => m.StopVmAsync("local", VmGuid1, false, It.IsAny<CancellationToken>()), Times.Once);
        _mockHyperVManager.Verify(m => m.DestroyVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. Command Execution Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies vm_run_command dispatches through the full pipeline and returns
    /// CommandResult fields (exitCode, stdout, stderr, durationMs) in the response.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1.
    /// </summary>
    [Fact]
    public async Task CommandExecution_VmRunCommand_ReturnsCommandResultFields()
    {
        // Arrange
        var expectedResult = new CommandResult
        {
            ExitCode = 0,
            Stdout = "Hello World",
            Stderr = "",
            TimedOut = false,
            Cancelled = false,
            Truncated = false,
            DurationMs = 150,
        };

        _mockCommandExecutor
            .Setup(m => m.ExecuteCommandAsync("local", VmGuid1, "echo Hello World", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["command"] = "echo Hello World",
                ["shell"] = "cmd",
                ["timeoutSeconds"] = 30,
            });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("exitCode").GetInt32().Should().Be(0);
        data.GetProperty("stdout").GetString().Should().Be("Hello World");
        data.GetProperty("stderr").GetString().Should().BeEmpty();
        data.GetProperty("timedOut").GetBoolean().Should().BeFalse();
        data.GetProperty("durationMs").GetInt64().Should().Be(150);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. File Transfer Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies vm_copy_file dispatches through the full pipeline and returns
    /// FileTransferResult fields in the response.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D1.
    /// </summary>
    [Fact]
    public async Task FileTransfer_VmCopyFile_ReturnsFileTransferResultFields()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Arrange
            var expectedResult = new FileTransferResult
            {
                BytesTransferred = 1024,
                SourcePath = tempFile,
                DestPath = @"C:\dest\file.txt",
                IsDirectory = false,
                FileCount = 1,
                Verified = true,
            };

            _mockFileTransferService
                .Setup(m => m.CopyToGuestAsync("local", VmGuid1, tempFile, @"C:\dest\file.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedResult);

            // Act
            var result = await _dispatcher.DispatchAsync("vm_copy_file",
                new Dictionary<string, object?>
                {
                    ["vmId"] = VmGuid1,
                    ["sourcePath"] = tempFile,
                    ["destPath"] = @"C:\dest\file.txt",
                });

            // Assert
            AssertSuccessResponse(result);
            using var doc = ParseResponse(result);
            var data = doc.RootElement.GetProperty("data");
            data.GetProperty("bytesTransferred").GetInt64().Should().Be(1024);
            data.GetProperty("verified").GetBoolean().Should().BeTrue();
            data.GetProperty("fileCount").GetInt32().Should().Be(1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. Error Propagation Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a VmNotFoundException thrown by the service layer propagates
    /// through the pipeline and is mapped to a VM_NOT_FOUND error envelope.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_NOT_FOUND.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_VmNotFound_ReturnsMappedErrorEnvelope()
    {
        // Arrange
        _mockHyperVManager
            .Setup(m => m.GetVmStatusAsync("local", NonExistentVmGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", NonExistentVmGuid));

        // Act
        var result = await _dispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?> { ["vmId"] = NonExistentVmGuid });

        // Assert
        AssertErrorResponse(result, ErrorCodes.VmNotFound);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("error").GetString()
            .Should().Contain(NonExistentVmGuid);
    }

    /// <summary>
    /// Verifies that a ConcurrencyLimitException thrown by the concurrency gate
    /// is mapped to a CONCURRENCY_LIMIT error envelope.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D4.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_ConcurrencyLimitException_ReturnsMappedErrorEnvelope()
    {
        // Arrange — service throws ConcurrencyLimitException
        _mockHyperVManager
            .Setup(m => m.StartVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyLimitException("Global concurrency limit reached"));

        // Act
        var result = await _dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });

        // Assert
        AssertErrorResponse(result, ErrorCodes.ConcurrencyLimit);
    }

    /// <summary>
    /// Verifies that VmAlreadyExistsException maps to VM_ALREADY_EXISTS error code.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_ALREADY_EXISTS.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_VmAlreadyExists_ReturnsMappedErrorEnvelope()
    {
        // Arrange
        _mockHyperVManager
            .Setup(m => m.CreateVmAsync("local", "existing-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmAlreadyExistsException("local", "existing-vm"));

        // Act
        var result = await _dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?> { ["name"] = "existing-vm" });

        // Assert
        AssertErrorResponse(result, ErrorCodes.VmAlreadyExists);
    }

    /// <summary>
    /// Verifies that CommandTimeoutException maps to COMMAND_TIMEOUT and includes
    /// partial output in the data field per ADR-9.
    /// See /myplans/execution/commands/commands-design.md — CMD-D4.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_CommandTimeout_IncludesPartialOutput()
    {
        // Arrange
        _mockCommandExecutor
            .Setup(m => m.ExecuteCommandAsync("local", VmGuid1, "long-running", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CommandTimeoutException("Timed out", "partial stdout", "partial stderr", 30000));

        // Act
        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["command"] = "long-running",
            });

        // Assert
        AssertErrorResponse(result, ErrorCodes.CommandTimeout);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("stdout").GetString().Should().Be("partial stdout");
        data.GetProperty("stderr").GetString().Should().Be("partial stderr");
        data.GetProperty("timedOut").GetBoolean().Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. Concurrency Limit Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that with MaxConcurrentOperations=1, only one dispatch executes at
    /// a time and a second concurrent dispatch waits until the first completes.
    /// Uses a TaskCompletionSource to control when the first operation finishes.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D2.
    /// </summary>
    [Fact]
    public async Task ConcurrencyLimit_MaxOne_SecondOperationWaitsForFirst()
    {
        // Arrange — create a dispatcher with MaxConcurrentOperations=1
        var restrictedOptions = new ServerOptions
        {
            MaxConcurrentOperations = 1,
            MaxPerHostOperations = 1,
            DefaultHostId = "local",
        };
        restrictedOptions.Hosts["local"] = new HostProfile
        {
            HostId = "local",
            ComputerName = "localhost",
            TrustPolicy = "local",
        };

        using var restrictedGate = new ConcurrencyGate(restrictedOptions);
        var tcs = new TaskCompletionSource<VmInfo>();
        var mockManager = new Mock<IHyperVManager>();

        // First call blocks until tcs is completed
        mockManager
            .Setup(m => m.GetVmStatusAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);
        // Second call returns immediately
        mockManager
            .Setup(m => m.GetVmStatusAsync("local", VmGuid2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = VmGuid2, State = "Running", HostId = "local" });

        var restrictedDispatcher = new ToolDispatcher(
            mockManager.Object,
            _mockCommandExecutor.Object,
            _mockFileTransferService.Object,
            new Mock<ICheckpointManager>().Object,
            new HostResolver(restrictedOptions),
            _errorMapper,
            restrictedGate,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            restrictedOptions);

        // Act — launch both dispatches concurrently
        var task1 = restrictedDispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });
        // Allow task1 to acquire the semaphore
        await Task.Delay(50);

        var task2 = restrictedDispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?> { ["vmId"] = VmGuid2 });

        // Verify task2 hasn't completed while task1 is blocking
        task2.IsCompleted.Should().BeFalse("second dispatch should wait for global slot");

        // Release the first operation
        tcs.SetResult(new VmInfo { VmId = VmGuid1, State = "Running", HostId = "local" });

        // Both tasks should now complete
        var result1 = await task1;
        var result2 = await task2;

        AssertSuccessResponse(result1);
        AssertSuccessResponse(result2);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. Unknown Tool Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that dispatching an unregistered tool name returns a TOOL_NOT_FOUND
    /// error envelope without throwing an exception.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
    /// </summary>
    [Fact]
    public async Task UnknownTool_ReturnsToolNotFoundError()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_unknown",
            new Dictionary<string, object?>());

        // Assert
        AssertErrorResponse(result, RuntimeErrorCodes.ToolNotFound);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("error").GetString()
            .Should().Contain("vm_unknown");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. Missing Argument Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_create without a required "name" parameter returns
    /// INVALID_PARAMETER error envelope. The ArgumentException from
    /// GetRequiredStringArg is caught and mapped by ErrorMapper.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task MissingArgument_VmCreateWithoutName_ReturnsInvalidParameter()
    {
        // Act — dispatch vm_create with no arguments
        var result = await _dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>());

        // Assert
        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("error").GetString()
            .Should().Contain("name");
    }

    /// <summary>
    /// Verifies that vm_start without a required "vmId" parameter returns
    /// INVALID_PARAMETER error envelope.
    /// </summary>
    [Fact]
    public async Task MissingArgument_VmStartWithoutVmId_ReturnsInvalidParameter()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?>());

        // Assert
        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }

    /// <summary>
    /// Verifies that vm_run_command without required "command" parameter returns
    /// INVALID_PARAMETER error envelope.
    /// </summary>
    [Fact]
    public async Task MissingArgument_VmRunCommandWithoutCommand_ReturnsInvalidParameter()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });

        // Assert
        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. Cancellation Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that passing a pre-cancelled CancellationToken causes
    /// DispatchAsync to throw OperationCanceledException before invoking the handler.
    /// See /myplans/execution/commands/commands-design.md — Timeout and Cancellation.
    /// </summary>
    [Fact]
    public async Task Cancellation_PreCancelledToken_ThrowsOperationCancelled()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        var act = () => _dispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Verify the service was never called
        _mockHyperVManager.Verify(
            m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Verifies that cancellation during the second check (after handler lookup,
    /// before handler invocation) also throws OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task Cancellation_PreCancelledToken_ForKnownTool_ThrowsOperationCancelled()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        var act = () => _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = "hello" },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 9. Default hostId Flow
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that omitting hostId from arguments defaults to "local" per MCP-D3.
    /// The mock is set up to expect hostId="local", confirming the default was applied.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: All tools accept optional hostId.
    /// </summary>
    [Fact]
    public async Task DefaultHostId_OmittedHostId_UsesLocal()
    {
        // Arrange — mock expects hostId="local" (the default)
        _mockHyperVManager
            .Setup(m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>
            {
                new() { VmId = VmGuid1, Name = "test-vm", State = "Running", HostId = "local" },
            });

        // Act — call vm_list without specifying hostId
        var result = await _dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?>());

        // Assert
        AssertSuccessResponse(result);
        _mockHyperVManager.Verify(
            m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()),
            Times.Once,
            "Should have called ListVmsAsync with hostId='local' as the default");
    }

    /// <summary>
    /// Verifies that explicitly specifying a hostId passes it through the pipeline.
    /// </summary>
    [Fact]
    public async Task ExplicitHostId_PassedThroughToService()
    {
        // Arrange
        _mockHyperVManager
            .Setup(m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>());

        // Act — specify hostId explicitly
        var result = await _dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?> { ["hostId"] = "local" });

        // Assert
        AssertSuccessResponse(result);
        _mockHyperVManager.Verify(
            m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 10. Response JSON Format Verification
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies the complete response envelope format for success responses,
    /// ensuring all expected fields (success, data, error, errorCode, state) are present.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D2: Consistent response envelope.
    /// </summary>
    [Fact]
    public async Task ResponseFormat_SuccessResponse_HasCorrectShape()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = "format-test" });

        // Assert — verify the complete JSON shape
        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        // Success field must be true
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        // Data field must be present and contain the echoed message
        root.TryGetProperty("data", out var dataProp).Should().BeTrue();
        dataProp.ValueKind.Should().NotBe(JsonValueKind.Null);
        dataProp.GetProperty("message").GetString().Should().Be("format-test");

        // Error field must be present (null for success)
        root.TryGetProperty("error", out var errorProp).Should().BeTrue();
        (errorProp.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(errorProp.GetString()))
            .Should().BeTrue();

        // ErrorCode field must be present (null for success)
        root.TryGetProperty("errorCode", out var errorCodeProp).Should().BeTrue();
        (errorCodeProp.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(errorCodeProp.GetString()))
            .Should().BeTrue();

        // State field must be present (null for success)
        root.TryGetProperty("state", out var stateProp).Should().BeTrue();
        (stateProp.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(stateProp.GetString()))
            .Should().BeTrue();
    }

    /// <summary>
    /// Verifies the complete response envelope format for error responses,
    /// ensuring all expected fields are present with correct values.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D2: Consistent response envelope.
    /// </summary>
    [Fact]
    public async Task ResponseFormat_ErrorResponse_HasCorrectShape()
    {
        // Act — trigger an error via unknown tool
        var result = await _dispatcher.DispatchAsync("vm_nonexistent",
            new Dictionary<string, object?>());

        // Assert — verify the complete JSON shape
        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        // Success field must be false
        root.GetProperty("success").GetBoolean().Should().BeFalse();

        // Error field must be present with a message
        root.TryGetProperty("error", out var errorProp).Should().BeTrue();
        errorProp.GetString().Should().NotBeNullOrEmpty();

        // ErrorCode field must be present with a valid code
        root.TryGetProperty("errorCode", out var errorCodeProp).Should().BeTrue();
        errorCodeProp.GetString().Should().Be(RuntimeErrorCodes.ToolNotFound);

        // Data field must be present (null for this error type)
        root.TryGetProperty("data", out var dataProp).Should().BeTrue();

        // State field must be present
        root.TryGetProperty("state", out _).Should().BeTrue();
    }

    /// <summary>
    /// Verifies that the response JSON is valid and deserializable back to
    /// McpToolResponse with all fields correctly round-tripped.
    /// </summary>
    [Fact]
    public async Task ResponseFormat_CanDeserializeToMcpToolResponse()
    {
        // Act
        var result = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = "roundtrip" });

        // Assert — deserialize to McpToolResponse
        var response = JsonSerializer.Deserialize<McpToolResponse>(result);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();
        response.Error.Should().BeNullOrEmpty();
        response.ErrorCode.Should().BeNullOrEmpty();
        response.State.Should().BeNullOrEmpty();
        response.Data.Should().NotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Additional regression tests for error mapping
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression test: Verifies that generic exceptions (not domain-specific)
    /// are mapped to INTERNAL_ERROR and don't leak implementation details.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_GenericException_MapsToInternalError()
    {
        // Arrange
        _mockHyperVManager
            .Setup(m => m.GetVmStatusAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NullReferenceException("Object reference not set"));

        // Act
        var result = await _dispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });

        // Assert — should be INTERNAL_ERROR with sanitized message
        AssertErrorResponse(result, RuntimeErrorCodes.InternalError);
        using var doc = ParseResponse(result);
        // The error message should NOT contain the raw exception details
        doc.RootElement.GetProperty("error").GetString()
            .Should().NotContain("Object reference not set");
    }

    /// <summary>
    /// Regression test: Verifies that FileNotFoundException in file transfer
    /// maps to FILE_NOT_FOUND error code.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: FILE_NOT_FOUND.
    /// </summary>
    [Fact]
    public async Task ErrorPropagation_FileNotFound_MapsToFileNotFoundCode()
    {
        // Arrange — use a real temp file so early validation passes,
        // then have the mocked service throw FileNotFoundException.
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content");

            _mockFileTransferService
                .Setup(m => m.CopyToGuestAsync("local", VmGuid1, tempFile, @"C:\dest.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new FileNotFoundException("File not found", tempFile));

            // Act
            var result = await _dispatcher.DispatchAsync("vm_copy_file",
                new Dictionary<string, object?>
                {
                    ["vmId"] = VmGuid1,
                    ["sourcePath"] = tempFile,
                    ["destPath"] = @"C:\dest.txt",
                });

            // Assert
            AssertErrorResponse(result, ErrorCodes.FileNotFound);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Regression test: Verifies vm_list with nameFilter passes the filter through.
    /// </summary>
    [Fact]
    public async Task VmList_WithNameFilter_PassesFilterToService()
    {
        // Arrange
        _mockHyperVManager
            .Setup(m => m.ListVmsAsync("local", "test-*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>
            {
                new() { VmId = "vm-001", Name = "test-vm-1", State = "Running", HostId = "local" },
                new() { VmId = "vm-002", Name = "test-vm-2", State = "Off", HostId = "local" },
            });

        // Act
        var result = await _dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?> { ["nameFilter"] = "test-*" });

        // Assert
        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("count").GetInt32().Should().Be(2);
    }
}
