using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for P1 tool handlers in ToolDispatcher.
/// Each test exercises the full dispatch pipeline: ToolDispatcher.DispatchAsync →
/// argument extraction → concurrency gate → service delegation → response wrapping.
/// See /myplans/execution-plan.md — Stage 1.5: P1 Tool Handlers.
///
/// All tests use mocked infrastructure services to verify:
/// - Correct argument extraction and forwarding to service methods
/// - Default values for optional arguments (hostId → "local", shell → "powershell", etc.)
/// - Concurrency gate acquisition (global slot + per-VM/host locks)
/// - Error mapping when services throw domain exceptions
/// - Missing required parameter validation
/// </summary>
[Trait("Category", "Runtime")]
public class P1ToolHandlerTests
{
    /// <summary>
    /// Canonical VM GUID used across all P1 tool tests.
    /// All handlers now call InputValidation.ValidateVmId() which requires a valid GUID.
    /// </summary>
    private const string TestVmGuid = "12345678-1234-1234-1234-123456789abc";

    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
    private readonly Mock<ICheckpointManager> _checkpointManager = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IConcurrencyGate> _gate = new();

    /// <summary>
    /// Creates a ToolDispatcher with the class-level mocks and real ErrorMapper.
    /// All concurrency gates grant locks immediately by default.
    /// </summary>
    private ToolDispatcher CreateDispatcher(ServerOptions? options = null)
    {
        var serverOptions = options ?? new ServerOptions();

        // Default: all concurrency locks succeed immediately
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // Default: GetVmStatusAsync returns a Running VM (needed for vm_run_script, vm_get_file state precondition)
        _hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

        return new ToolDispatcher(
            _hvManager.Object,
            _commandExecutor.Object,
            _fileTransfer.Object,
            _checkpointManager.Object,
            _hostResolver.Object,
            new ErrorMapper(),
            _gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            serverOptions);
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_run_script Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_run_script dispatches to ICommandExecutor.ExecuteScriptAsync with correct arguments.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1.
    /// </summary>
    [Fact]
    public async Task VmRunScript_Dispatches_To_CommandExecutor_ExecuteScriptAsync()
    {
        var expected = new CommandResult { ExitCode = 0, Stdout = "script output", Stderr = "", TimedOut = false, DurationMs = 1500 };
        _commandExecutor.Setup(e => e.ExecuteScriptAsync("local", TestVmGuid, "Get-Process | Select -First 5", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["script"] = "Get-Process | Select -First 5",
                ["hostId"] = "local",
                ["shell"] = "powershell",
                ["timeoutSeconds"] = 60
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _commandExecutor.Verify(e => e.ExecuteScriptAsync("local", TestVmGuid, "Get-Process | Select -First 5", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_run_script must forward all parameters to ExecuteScriptAsync");
    }

    /// <summary>
    /// vm_run_script defaults shell to "powershell" when not provided.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1: default shell for scripts.
    /// </summary>
    [Fact]
    public async Task VmRunScript_Default_Shell_Is_Powershell()
    {
        var expected = new CommandResult { ExitCode = 0, Stdout = "output", Stderr = "", TimedOut = false, DurationMs = 100 };
        _commandExecutor.Setup(e => e.ExecuteScriptAsync("local", TestVmGuid, "Write-Host 'hello'", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["script"] = "Write-Host 'hello'"
                // shell omitted — should default to "powershell"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _commandExecutor.Verify(e => e.ExecuteScriptAsync("local", TestVmGuid, "Write-Host 'hello'", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_run_script must default shell to 'powershell' when not provided");
    }

    /// <summary>
    /// vm_run_script defaults timeout to 60 seconds when not provided.
    /// </summary>
    [Fact]
    public async Task VmRunScript_Default_Timeout_Is_60()
    {
        var expected = new CommandResult { ExitCode = 0, Stdout = "output", Stderr = "", TimedOut = false, DurationMs = 100 };
        _commandExecutor.Setup(e => e.ExecuteScriptAsync("local", TestVmGuid, "Get-Date", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["script"] = "Get-Date"
                // timeoutSeconds omitted — should default to 60
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _commandExecutor.Verify(e => e.ExecuteScriptAsync("local", TestVmGuid, "Get-Date", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_run_script must default timeoutSeconds to 60 when not provided");
    }

    /// <summary>
    /// vm_run_script returns success=false with COMMAND_TIMEOUT errorCode
    /// when the script execution times out.
    /// See /myplans/execution/commands/commands-design.md — CMD-D4.
    /// </summary>
    [Fact]
    public async Task VmRunScript_TimedOut_Returns_Failure()
    {
        var timedOut = new CommandResult { ExitCode = -1, Stdout = "partial", Stderr = "", TimedOut = true, DurationMs = 60000 };
        _commandExecutor.Setup(e => e.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(timedOut);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["script"] = "Start-Sleep -Seconds 999"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse(
            "timed-out script must produce error response");
        response.ErrorCode.Should().Be("COMMAND_TIMEOUT",
            "timed-out script must map to COMMAND_TIMEOUT error code");
    }

    /// <summary>
    /// vm_run_script returns success=false with COMMAND_FAILED errorCode
    /// when the script exits with non-zero exit code.
    /// </summary>
    [Fact]
    public async Task VmRunScript_NonZeroExitCode_Returns_Failure()
    {
        var failed = new CommandResult { ExitCode = 1, Stdout = "", Stderr = "error occurred", TimedOut = false, DurationMs = 200 };
        _commandExecutor.Setup(e => e.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failed);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["script"] = "exit 1"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse(
            "non-zero exit code must produce error response");
        response.ErrorCode.Should().Be("COMMAND_FAILED",
            "non-zero exit code must map to COMMAND_FAILED error code");
    }

    /// <summary>
    /// vm_run_script without required 'vmId' parameter returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task VmRunScript_Missing_VmId_Returns_Error()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["script"] = "Get-Date"
                // 'vmId' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "missing required 'vmId' must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_run_script without required 'script' parameter returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task VmRunScript_Missing_Script_Returns_Error()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid
                // 'script' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "missing required 'script' must map to INVALID_PARAMETER");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_get_file Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_get_file dispatches to IFileTransferService.CopyFromGuestAsync with correct arguments.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D2, FT-D3.
    /// </summary>
    [Fact]
    public async Task VmGetFile_Dispatches_To_FileTransferService_CopyFromGuestAsync()
    {
        var expected = new FileTransferResult
        {
            BytesTransferred = 4096,
            SourcePath = @"C:\guest\log.txt",
            DestPath = @"C:\host\log.txt",
            IsDirectory = false,
            FileCount = 1,
            Verified = true
        };
        _fileTransfer.Setup(f => f.CopyFromGuestAsync("local", TestVmGuid, @"C:\guest\log.txt", @"C:\host\log.txt", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_get_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["sourcePath"] = @"C:\guest\log.txt",
                ["destPath"] = @"C:\host\log.txt",
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _fileTransfer.Verify(f => f.CopyFromGuestAsync("local", TestVmGuid, @"C:\guest\log.txt", @"C:\host\log.txt", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_get_file must delegate to IFileTransferService.CopyFromGuestAsync with correct arguments");
    }

    /// <summary>
    /// vm_get_file defaults hostId to "local" when not provided.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: default hostId = "local".
    /// </summary>
    [Fact]
    public async Task VmGetFile_Default_HostId_Is_Local()
    {
        var expected = new FileTransferResult
        {
            BytesTransferred = 1024,
            SourcePath = @"C:\guest\file.txt",
            DestPath = @"C:\host\file.txt",
            IsDirectory = false,
            FileCount = 1,
            Verified = true
        };
        _fileTransfer.Setup(f => f.CopyFromGuestAsync("local", TestVmGuid, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_get_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["sourcePath"] = @"C:\guest\file.txt",
                ["destPath"] = @"C:\host\file.txt"
                // hostId omitted — should default to "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _fileTransfer.Verify(f => f.CopyFromGuestAsync("local", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "hostId must default to 'local' when not provided (MCP-D3)");
    }

    /// <summary>
    /// vm_get_file without required 'sourcePath' parameter returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task VmGetFile_Missing_SourcePath_Returns_Error()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_get_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["destPath"] = @"C:\host\file.txt"
                // 'sourcePath' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "missing required 'sourcePath' must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_get_file without required 'destPath' parameter returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task VmGetFile_Missing_DestPath_Returns_Error()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_get_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["sourcePath"] = @"C:\guest\file.txt"
                // 'destPath' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "missing required 'destPath' must map to INVALID_PARAMETER");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_restart Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_restart dispatches to IHyperVManager.RestartVmAsync with correct arguments.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_restart.
    /// </summary>
    [Fact]
    public async Task VmRestart_Dispatches_To_HyperVManager_RestartVmAsync()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.RestartVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_restart",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.RestartVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "vm_restart must delegate to IHyperVManager.RestartVmAsync with correct vmId and hostId");
    }

    /// <summary>
    /// vm_restart defaults hostId to "local" when not provided.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: default hostId = "local".
    /// </summary>
    [Fact]
    public async Task VmRestart_Default_HostId_Is_Local()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.RestartVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_restart",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid
                // hostId omitted — should default to "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.RestartVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "hostId must default to 'local' when not provided (MCP-D3)");
    }

    /// <summary>
    /// vm_restart returns VM_NOT_FOUND when the VM does not exist.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_NOT_FOUND.
    /// </summary>
    [Fact]
    public async Task VmRestart_VmNotFound_Returns_Error()
    {
        var nonExistentGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _hvManager.Setup(m => m.RestartVmAsync("local", nonExistentGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", nonExistentGuid));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_restart",
            new Dictionary<string, object?>
            {
                ["vmId"] = nonExistentGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("VM_NOT_FOUND",
            "VmNotFoundException from service must map to VM_NOT_FOUND through dispatcher error pipeline");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_wait_ready Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_wait_ready dispatches to IHyperVManager.WaitForReadyAsync with correct arguments.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Readiness Probes.
    /// </summary>
    [Fact]
    public async Task VmWaitReady_Dispatches_To_HyperVManager_WaitForReadyAsync()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.WaitForReadyAsync("local", TestVmGuid, 300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_wait_ready",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["timeoutSeconds"] = 300
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.WaitForReadyAsync("local", TestVmGuid, 300, It.IsAny<CancellationToken>()), Times.Once,
            "vm_wait_ready must delegate to IHyperVManager.WaitForReadyAsync with correct arguments");
    }

    /// <summary>
    /// vm_wait_ready defaults timeout to 300 seconds when not provided.
    /// </summary>
    [Fact]
    public async Task VmWaitReady_Default_Timeout_Is_300()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.WaitForReadyAsync("local", TestVmGuid, 300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_wait_ready",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid
                // timeoutSeconds omitted — should default to 300
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.WaitForReadyAsync("local", TestVmGuid, 300, It.IsAny<CancellationToken>()), Times.Once,
            "vm_wait_ready must default timeoutSeconds to 300 when not provided");
    }

    /// <summary>
    /// vm_wait_ready maps TimeoutException from service to an error response.
    /// </summary>
    [Fact]
    public async Task VmWaitReady_Timeout_Returns_Error()
    {
        _hvManager.Setup(m => m.WaitForReadyAsync("local", TestVmGuid, 10, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("VM did not become ready within 10 seconds."));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_wait_ready",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["timeoutSeconds"] = 10
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse(
            "TimeoutException must produce error response");
        // TimeoutException maps through ErrorMapper — verify it produces a fail response
        response.Error.Should().NotBeNullOrWhiteSpace(
            "error message should be populated for timeout");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_checkpoint Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_checkpoint action="create" dispatches to ICheckpointManager.CreateCheckpointAsync.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_Create_Dispatches_Correctly()
    {
        var expected = new CheckpointResult
        {
            Action = "create",
            VmId = TestVmGuid,
            CheckpointName = "before-update",
            Checkpoints = new List<CheckpointInfo>
            {
                new() { Name = "before-update", Id = "cp-001", CreatedAt = DateTimeOffset.UtcNow }
            }
        };
        _checkpointManager.Setup(cp => cp.CreateCheckpointAsync("local", TestVmGuid, "before-update", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["action"] = "create",
                ["name"] = "before-update",
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _checkpointManager.Verify(cp => cp.CreateCheckpointAsync("local", TestVmGuid, "before-update", It.IsAny<CancellationToken>()), Times.Once,
            "vm_checkpoint action=create must delegate to ICheckpointManager.CreateCheckpointAsync");
    }

    /// <summary>
    /// vm_checkpoint action="restore" dispatches to ICheckpointManager.RestoreCheckpointAsync.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_Restore_Dispatches_Correctly()
    {
        var expected = new CheckpointResult
        {
            Action = "restore",
            VmId = TestVmGuid,
            CheckpointName = "before-update"
        };
        _checkpointManager.Setup(cp => cp.RestoreCheckpointAsync("local", TestVmGuid, "before-update", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["action"] = "restore",
                ["name"] = "before-update",
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _checkpointManager.Verify(cp => cp.RestoreCheckpointAsync("local", TestVmGuid, "before-update", It.IsAny<CancellationToken>()), Times.Once,
            "vm_checkpoint action=restore must delegate to ICheckpointManager.RestoreCheckpointAsync");
    }

    /// <summary>
    /// vm_checkpoint action="list" dispatches to ICheckpointManager.ListCheckpointsAsync.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_List_Dispatches_Correctly()
    {
        var expected = new CheckpointResult
        {
            Action = "list",
            VmId = TestVmGuid,
            Checkpoints = new List<CheckpointInfo>
            {
                new() { Name = "cp1", Id = "id-1", CreatedAt = DateTimeOffset.UtcNow },
                new() { Name = "cp2", Id = "id-2", CreatedAt = DateTimeOffset.UtcNow }
            }
        };
        _checkpointManager.Setup(cp => cp.ListCheckpointsAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["action"] = "list",
                ["hostId"] = "local"
                // name not required for "list" action
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _checkpointManager.Verify(cp => cp.ListCheckpointsAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "vm_checkpoint action=list must delegate to ICheckpointManager.ListCheckpointsAsync");
    }

    /// <summary>
    /// vm_checkpoint action="delete" dispatches to ICheckpointManager.DeleteCheckpointAsync.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_Delete_Dispatches_Correctly()
    {
        var expected = new CheckpointResult
        {
            Action = "delete",
            VmId = TestVmGuid,
            CheckpointName = "old-checkpoint"
        };
        _checkpointManager.Setup(cp => cp.DeleteCheckpointAsync("local", TestVmGuid, "old-checkpoint", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["action"] = "delete",
                ["name"] = "old-checkpoint",
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _checkpointManager.Verify(cp => cp.DeleteCheckpointAsync("local", TestVmGuid, "old-checkpoint", It.IsAny<CancellationToken>()), Times.Once,
            "vm_checkpoint action=delete must delegate to ICheckpointManager.DeleteCheckpointAsync");
    }

    /// <summary>
    /// vm_checkpoint with invalid action returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_InvalidAction_Returns_Error()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["action"] = "snapshot",
                ["name"] = "test"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "invalid checkpoint action must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_checkpoint action="create" without required 'name' parameter returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_Create_MissingName_Returns_Error()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["action"] = "create"
                // 'name' is missing — required for create
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "missing 'name' for create action must map to INVALID_PARAMETER");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_cleanup_orphans Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_cleanup_orphans with dryRun=true returns orphan list without destroying.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Orphan Cleanup.
    /// </summary>
    [Fact]
    public async Task VmCleanupOrphans_DryRun_Returns_OrphanList()
    {
        var orphans = new List<VmInfo>
        {
            new() { VmId = "orphan-1", Name = "old-vm-1", State = "Off", HostId = "local" },
            new() { VmId = "orphan-2", Name = "old-vm-2", State = "Running", HostId = "local" }
        };
        _hvManager.Setup(m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orphans.AsReadOnly());

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_cleanup_orphans",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["dryRun"] = true
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()), Times.Once,
            "vm_cleanup_orphans with dryRun=true must call CleanupOrphansAsync with dryRun=true");
    }

    /// <summary>
    /// vm_cleanup_orphans defaults dryRun to true when not provided.
    /// </summary>
    [Fact]
    public async Task VmCleanupOrphans_Default_DryRun_Is_True()
    {
        var orphans = new List<VmInfo>();
        _hvManager.Setup(m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orphans.AsReadOnly());

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_cleanup_orphans",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local"
                // dryRun omitted — should default to true
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()), Times.Once,
            "vm_cleanup_orphans must default dryRun to true when not provided");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Lock Verification Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_checkpoint must acquire global + host + VM locks (lifecycle-grade operation).
    /// Issue 1 fix: HandleCheckpointAsync now acquires all 3 lock levels per concurrency design.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_checkpoint needs Global+Host+VM.
    /// </summary>
    [Fact]
    public async Task VmCheckpoint_Acquires_Global_Host_VM_Locks()
    {
        var expected = new CheckpointResult
        {
            Action = "list",
            VmId = TestVmGuid,
            Checkpoints = new List<CheckpointInfo>()
        };
        _checkpointManager.Setup(cp => cp.ListCheckpointsAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["action"] = "list",
                ["hostId"] = "local"
            },
            CancellationToken.None);

        // Verify all 3 lock levels were acquired
        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_checkpoint must acquire global slot");
        _gate.Verify(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_checkpoint must acquire per-host lock (lifecycle-grade operation)");
        _gate.Verify(g => g.AcquireVmLockAsync("local", TestVmGuid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_checkpoint must acquire per-VM lock");
    }

    /// <summary>
    /// vm_wait_ready must acquire global + VM locks to prevent overlap with same-VM mutations.
    /// Issue 2 fix: HandleWaitReadyAsync now acquires global + VM locks per concurrency design.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_wait_ready needs Global+VM.
    /// </summary>
    [Fact]
    public async Task VmWaitReady_Acquires_Global_And_VM_Locks()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.WaitForReadyAsync("local", TestVmGuid, 300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchAsync("vm_wait_ready",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        // Verify global + VM locks were acquired
        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_wait_ready must acquire global slot");
        _gate.Verify(g => g.AcquireVmLockAsync("local", TestVmGuid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_wait_ready must acquire per-VM lock to prevent overlap with same-VM mutations");

        // Verify host lock is NOT acquired (wait_ready is not lifecycle-grade)
        _gate.Verify(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never,
            "vm_wait_ready should NOT acquire per-host lock (not a lifecycle operation)");
    }
}
