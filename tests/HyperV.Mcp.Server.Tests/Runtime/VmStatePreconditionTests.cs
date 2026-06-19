using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for VM state precondition checks added in Issue #21.
/// When vm_run_command, vm_run_script, vm_copy_file, or vm_get_file is dispatched
/// on a VM that is not Running, the dispatcher must return VM_NOT_RUNNING error.
/// When the VM is Running, the tool proceeds normally.
/// </summary>
[Trait("Category", "Runtime")]
public class VmStatePreconditionTests
{
    private const string TestVmGuid = "12345678-1234-1234-1234-123456789abc";

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
            new ServerOptions());
    }

    private void MockVmState(string state)
    {
        _hvManager.Setup(m => m.GetVmStatusAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, State = state, HostId = "local" });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tool argument dictionaries
    // ═══════════════════════════════════════════════════════════════════

    private static Dictionary<string, object?> RunCommandArgs() => new()
    {
        ["vmId"] = TestVmGuid,
        ["command"] = "echo test"
    };

    private static Dictionary<string, object?> RunScriptArgs() => new()
    {
        ["vmId"] = TestVmGuid,
        ["script"] = "Write-Output test"
    };

    private static Dictionary<string, object?> CopyFileArgs(string sourcePath) => new()
    {
        ["vmId"] = TestVmGuid,
        ["sourcePath"] = sourcePath,
        ["destPath"] = @"C:\dest.txt"
    };

    private static Dictionary<string, object?> GetFileArgs() => new()
    {
        ["vmId"] = TestVmGuid,
        ["sourcePath"] = @"C:\test.txt",
        ["destPath"] = @"C:\dest.txt"
    };

    // ═══════════════════════════════════════════════════════════════════
    // vm_run_command — non-running states
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Off")]
    [InlineData("Saved")]
    [InlineData("Paused")]
    public async Task VmRunCommand_NonRunningState_Returns_VmNotRunning(string state)
    {
        MockVmState(state);
        var dispatcher = CreateDispatcher();

        var json = await dispatcher.DispatchAsync("vm_run_command", RunCommandArgs(), CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);

        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("VM_NOT_RUNNING",
            $"vm_run_command on a VM in '{state}' state must return VM_NOT_RUNNING (Issue #21)");
        _commandExecutor.Verify(
            e => e.ExecuteCommandAsync("local", TestVmGuid, "echo test", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            $"vm_run_command on a VM in '{state}' state must not attempt guest command execution (Issue #21)");
    }

    [Fact]
    public async Task VmRunCommand_Running_Proceeds()
    {
        MockVmState("Running");
        _commandExecutor.Setup(e => e.ExecuteCommandAsync("local", TestVmGuid, "echo test", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult { ExitCode = 0, Stdout = "test", Stderr = "", TimedOut = false, DurationMs = 100 });

        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync("vm_run_command", RunCommandArgs(), CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);

        response!.Success.Should().BeTrue();
        response.ErrorCode.Should().BeNull();
        _commandExecutor.Verify(e => e.ExecuteCommandAsync("local", TestVmGuid, "echo test", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_run_script — non-running states
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Off")]
    [InlineData("Saved")]
    [InlineData("Paused")]
    public async Task VmRunScript_NonRunningState_Returns_VmNotRunning(string state)
    {
        MockVmState(state);
        var dispatcher = CreateDispatcher();

        var json = await dispatcher.DispatchAsync("vm_run_script", RunScriptArgs(), CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);

        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("VM_NOT_RUNNING",
            $"vm_run_script on a VM in '{state}' state must return VM_NOT_RUNNING (Issue #21)");
        _commandExecutor.Verify(
            e => e.ExecuteScriptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            $"vm_run_script on a VM in '{state}' state must not attempt guest script execution (Issue #21)");
    }

    [Fact]
    public async Task VmRunScript_Running_Proceeds()
    {
        MockVmState("Running");
        _commandExecutor.Setup(e => e.ExecuteScriptAsync("local", TestVmGuid, "Write-Output test", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult { ExitCode = 0, Stdout = "test", Stderr = "", TimedOut = false, DurationMs = 100 });

        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync("vm_run_script", RunScriptArgs(), CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);

        response!.Success.Should().BeTrue();
        response.ErrorCode.Should().BeNull();
        _commandExecutor.Verify(e => e.ExecuteScriptAsync("local", TestVmGuid, "Write-Output test", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_copy_file — non-running states
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Off")]
    [InlineData("Saved")]
    [InlineData("Paused")]
    public async Task VmCopyFile_NonRunningState_Returns_VmNotRunning(string state)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            MockVmState(state);
            var dispatcher = CreateDispatcher();

            var json = await dispatcher.DispatchAsync("vm_copy_file", CopyFileArgs(tempFile), CancellationToken.None);
            var response = JsonSerializer.Deserialize<McpToolResponse>(json);

            response!.Success.Should().BeFalse();
            response.ErrorCode.Should().Be("VM_NOT_RUNNING",
                $"vm_copy_file on a VM in '{state}' state must return VM_NOT_RUNNING (Issue #21)");
            _fileTransfer.Verify(
                f => f.CopyToGuestAsync("local", TestVmGuid, tempFile, @"C:\dest.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never,
                $"vm_copy_file on a VM in '{state}' state must not attempt guest file copy/session acquisition (Issue #21)");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VmCopyFile_Running_Proceeds()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            MockVmState("Running");
            _fileTransfer.Setup(f => f.CopyToGuestAsync("local", TestVmGuid, tempFile, @"C:\dest.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FileTransferResult { BytesTransferred = 100, SourcePath = tempFile, DestPath = @"C:\dest.txt" });

            var dispatcher = CreateDispatcher();
            var json = await dispatcher.DispatchAsync("vm_copy_file", CopyFileArgs(tempFile), CancellationToken.None);
            var response = JsonSerializer.Deserialize<McpToolResponse>(json);

            response!.Success.Should().BeTrue();
            response.ErrorCode.Should().BeNull();
            _fileTransfer.Verify(f => f.CopyToGuestAsync("local", TestVmGuid, tempFile, @"C:\dest.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_get_file — non-running states
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Off")]
    [InlineData("Saved")]
    [InlineData("Paused")]
    public async Task VmGetFile_NonRunningState_Returns_VmNotRunning(string state)
    {
        MockVmState(state);
        var dispatcher = CreateDispatcher();

        var json = await dispatcher.DispatchAsync("vm_get_file", GetFileArgs(), CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);

        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("VM_NOT_RUNNING",
            $"vm_get_file on a VM in '{state}' state must return VM_NOT_RUNNING (Issue #21)");
        _fileTransfer.Verify(
            f => f.CopyFromGuestAsync("local", TestVmGuid, @"C:\test.txt", @"C:\dest.txt", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            $"vm_get_file on a VM in '{state}' state must not attempt guest file retrieval (Issue #21)");
    }

    [Fact]
    public async Task VmGetFile_Running_Proceeds()
    {
        MockVmState("Running");
        _fileTransfer.Setup(f => f.CopyFromGuestAsync("local", TestVmGuid, @"C:\test.txt", @"C:\dest.txt", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileTransferResult { BytesTransferred = 100, SourcePath = @"C:\test.txt", DestPath = @"C:\dest.txt" });

        var dispatcher = CreateDispatcher();
        var json = await dispatcher.DispatchAsync("vm_get_file", GetFileArgs(), CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);

        response!.Success.Should().BeTrue();
        response.ErrorCode.Should().BeNull();
        _fileTransfer.Verify(f => f.CopyFromGuestAsync("local", TestVmGuid, @"C:\test.txt", @"C:\dest.txt", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
