using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #38: vm_copy_file early source path validation.
/// Verifies that HandleCopyFileAsync checks source file/directory existence
/// BEFORE lock acquisition and VM resolution, so callers get FILE_NOT_FOUND
/// instead of VM_NOT_FOUND when both source path and VM are invalid.
/// </summary>
[Trait("Category", "Runtime")]
public class CopyFileEarlyValidationTests
{
    private const string NonExistentVmGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IConcurrencyGate> _gate = new();

    private ToolDispatcher CreateDispatcher()
    {
        // Concurrency gates succeed immediately
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // Do NOT set up GetVmStatusAsync — the VM doesn't exist. If early validation
        // works, we should never reach VM resolution.

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

    /// <summary>
    /// Issue #38: vm_copy_file with a non-existent source file path should return
    /// FILE_NOT_FOUND error (mapped from FileNotFoundException), even when the VM
    /// doesn't exist. This proves the source path check happens before VM resolution.
    /// </summary>
    [Fact]
    public async Task VmCopyFile_NonExistentSourceFile_ReturnsFileNotFound_EvenWhenVmInvalid()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_copy_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = NonExistentVmGuid,
                ["sourcePath"] = @"C:\definitely\does\not\exist\file.txt",
                ["destPath"] = @"C:\guest\file.txt",
                ["hostId"] = "local",
                ["isDirectory"] = false
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("FILE_NOT_FOUND",
            "Issue #38: non-existent source file must produce FILE_NOT_FOUND, not VM_NOT_FOUND");

        // Verify concurrency gate was never acquired (early exit before locks)
        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never,
            "Early validation should prevent lock acquisition");

        // Verify VM status was never checked
        _hvManager.Verify(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "Early validation should prevent VM status check");
    }

    /// <summary>
    /// Issue #38: vm_copy_file with isDirectory=true and a non-existent source directory
    /// should return an error mapped from DirectoryNotFoundException, even when the VM
    /// doesn't exist. This proves the source directory check happens before VM resolution.
    /// </summary>
    [Fact]
    public async Task VmCopyFile_NonExistentSourceDirectory_ReturnsError_EvenWhenVmInvalid()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_copy_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = NonExistentVmGuid,
                ["sourcePath"] = @"C:\definitely\does\not\exist\dir",
                ["destPath"] = @"C:\guest\dir",
                ["hostId"] = "local",
                ["isDirectory"] = true
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        // DirectoryNotFoundException maps through ErrorMapper to FILE_NOT_FOUND
        response.ErrorCode.Should().Be("FILE_NOT_FOUND",
            "Issue #38: non-existent source directory must produce FILE_NOT_FOUND, not VM_NOT_FOUND");

        // Verify concurrency gate was never acquired
        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never,
            "Early validation should prevent lock acquisition");

        // Verify VM status was never checked
        _hvManager.Verify(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "Early validation should prevent VM status check");
    }

    /// <summary>
    /// Issue #38: vm_copy_file with an existing source file should proceed past early
    /// validation and reach VM resolution (which will fail for the non-existent VM).
    /// This confirms the early validation doesn't block valid source paths.
    /// </summary>
    [Fact]
    public async Task VmCopyFile_ExistingSourceFile_ProceedsPastEarlyValidation()
    {
        // Create a real temp file
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content");

            // Don't set up VM status — it will throw, proving we got past file validation
            var dispatcher = CreateDispatcher();
            var resultJson = await dispatcher.DispatchAsync("vm_copy_file",
                new Dictionary<string, object?>
                {
                    ["vmId"] = NonExistentVmGuid,
                    ["sourcePath"] = tempFile,
                    ["destPath"] = @"C:\guest\file.txt",
                    ["hostId"] = "local",
                    ["isDirectory"] = false
                },
                CancellationToken.None);

            var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
            response.Should().NotBeNull();
            // With valid source but invalid VM, we should get past early validation
            // and hit VM resolution. The concurrency gate should have been acquired.
            _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
                "Valid source file should pass early validation and reach lock acquisition");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
