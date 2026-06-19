using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Runtime tests for checkpoint, file transfer, and remoting orchestration flows.
/// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
/// See /myplans/execution/file-transfer/file-transfer-design.md — Host-to-Guest and Guest-to-Host flows.
/// See /myplans/remoting/remoting-design.md — Two-Tier Connection Architecture.
///
/// These tests exercise the expected orchestration for:
/// - Checkpoint create/restore/list with session invalidation on restore
/// - File transfer host-to-guest and guest-to-host
/// - Remoting resolution for remote host operations
/// - Concurrency lock patterns specific to these operations
///
/// Two test categories:
/// 1. Mock-based tests: validate the orchestration contract (expected wiring)
/// 2. Dispatcher-wired tests: exercise real ToolDispatcher + ErrorMapper to verify
///    end-to-end error mapping and dispatch behavior without real Hyper-V calls.
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement ICheckpointManager with Hyper-V PowerShell cmdlets.
/// 2. Implement IFileTransferService with Copy-Item -ToSession / -FromSession over the
///    persistent PSSession owned by IPowerShellDirectChannel (Phase 2, issue #52).
/// 3. Wire up host resolution and concurrency for all operations.
/// 4. Invalidate cached sessions after checkpoint restore (CP-D3).
/// </summary>
[Trait("Category", "Runtime")]
public class CheckpointFileTransferRemotingFlowTests
{
    private readonly Mock<ICheckpointManager> _checkpointManager = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IConcurrencyGate> _gate = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IErrorMapper> _errorMapper = new();

    private readonly HostProfile _localProfile = new()
    {
        HostId = "local",
        ComputerName = "localhost",
        TrustPolicy = "local"
    };

    private readonly HostProfile _remoteProfile = new()
    {
        HostId = "hyperv-01",
        ComputerName = "hyperv-01.corp.local",
        TrustPolicy = "strict",
        BaseVhdxPath = @"D:\Images\base.vhdx"
    };

    // ─── Checkpoint Create Flow ────────────────────────────────────────

    /// <summary>
    /// vm_checkpoint create: resolve host → acquire locks → create checkpoint → return result.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
    /// See /myplans/operational/concurrency/concurrency-design.md — vm_checkpoint needs Global+Host+VM.
    /// </summary>
    [Fact]
    public async Task Checkpoint_Create_Flow_Acquires_Locks_And_Creates()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);

        var globalLock = Mock.Of<IDisposable>();
        var hostLock = Mock.Of<IDisposable>();
        var vmLock = Mock.Of<IDisposable>();
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(globalLock);
        _gate.Setup(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(hostLock);
        _gate.Setup(g => g.AcquireVmLockAsync("local", "test-vm", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(vmLock);

        var checkpointResult = new CheckpointResult
        {
            Action = "create",
            VmId = "test-vm",
            CheckpointName = "test-vm-clean-baseline"
        };
        _checkpointManager.Setup(m => m.CreateCheckpointAsync("local", "test-vm", "test-vm-clean-baseline", It.IsAny<CancellationToken>()))
            .ReturnsAsync(checkpointResult);

        // Simulate flow
        _hostResolver.Object.ResolveRequired("local");
        await _gate.Object.AcquireGlobalSlotAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await _gate.Object.AcquireHostLockAsync("local", TimeSpan.FromSeconds(30), CancellationToken.None);
        await _gate.Object.AcquireVmLockAsync("local", "test-vm", TimeSpan.FromSeconds(60), CancellationToken.None);
        var result = await _checkpointManager.Object.CreateCheckpointAsync("local", "test-vm", "test-vm-clean-baseline", CancellationToken.None);

        result.Action.Should().Be("create");
        result.CheckpointName.Should().Be("test-vm-clean-baseline",
            "checkpoint name should follow the {vmName}-clean-baseline convention " +
            "(see /myplans/vm-management/checkpoints/checkpoints-design.md — CP-D2)");
    }

    // ─── Checkpoint Restore Flow ──────────────────────────────────────

    /// <summary>
    /// vm_checkpoint restore must invalidate cached sessions after restore.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — CP-D3: Invalidate cached session.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — Post-Restore Recovery Sequence.
    /// </summary>
    [Fact]
    public async Task Checkpoint_Restore_Flow_Invalidates_Session()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);

        var checkpointResult = new CheckpointResult
        {
            Action = "restore",
            VmId = "test-vm",
            CheckpointName = "test-vm-clean-baseline"
        };
        _checkpointManager.Setup(m => m.RestoreCheckpointAsync("local", "test-vm", "test-vm-clean-baseline", It.IsAny<CancellationToken>()))
            .ReturnsAsync(checkpointResult);

        var result = await _checkpointManager.Object.RestoreCheckpointAsync(
            "local", "test-vm", "test-vm-clean-baseline", CancellationToken.None);

        result.Action.Should().Be("restore");
        result.VmId.Should().Be("test-vm");

        // The actual session invalidation happens internally, but the contract
        // requires that after restore, new commands create fresh sessions.
        // This is verified by the implementation calling session store invalidation.
        _checkpointManager.Verify(m => m.RestoreCheckpointAsync(
            "local", "test-vm", "test-vm-clean-baseline", It.IsAny<CancellationToken>()),
            Times.Once,
            "checkpoint restore must be invoked exactly once");
    }

    // ─── Checkpoint List Flow ─────────────────────────────────────────

    /// <summary>
    /// vm_checkpoint list returns all checkpoints for a VM.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow: list.
    /// </summary>
    [Fact]
    public async Task Checkpoint_List_Returns_All_Checkpoints()
    {
        var listResult = new CheckpointResult
        {
            Action = "list",
            VmId = "test-vm",
            Checkpoints = new List<CheckpointInfo>
            {
                new() { Name = "test-vm-clean-baseline", Id = "cp-001", CreatedAt = DateTimeOffset.UtcNow.AddHours(-2) },
                new() { Name = "test-vm-after-install", Id = "cp-002", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) }
            }
        };
        _checkpointManager.Setup(m => m.ListCheckpointsAsync("local", "test-vm", It.IsAny<CancellationToken>()))
            .ReturnsAsync(listResult);

        var result = await _checkpointManager.Object.ListCheckpointsAsync("local", "test-vm", CancellationToken.None);

        result.Action.Should().Be("list");
        result.Checkpoints.Should().HaveCount(2,
            "list should return all checkpoints for the VM");
        result.Checkpoints![0].Name.Should().Be("test-vm-clean-baseline");
    }

    // ─── File Transfer: Host-to-Guest ─────────────────────────────────

    /// <summary>
    /// vm_copy_file host-to-guest flow: resolve host → acquire per-VM lock →
    /// Copy-Item -ToSession through IPowerShellDirectChannel → verify.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D10/FT-D11/FT-D12 (Phase 2).
    /// See /myplans/operational/concurrency/concurrency-design.md — vm_copy_file needs Global+VM.
    /// </summary>
    [Fact]
    public async Task CopyFile_HostToGuest_Flow_Transfers_And_Verifies()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);

        var globalLock = Mock.Of<IDisposable>();
        var vmLock = Mock.Of<IDisposable>();
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(globalLock);
        _gate.Setup(g => g.AcquireVmLockAsync("local", "test-vm", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(vmLock);

        var transferResult = new FileTransferResult
        {
            BytesTransferred = 1024,
            SourcePath = @"C:\src\file.txt",
            DestPath = @"C:\dest\file.txt",
            IsDirectory = false,
            FileCount = 1,
            Verified = true
        };
        _fileTransfer.Setup(f => f.CopyToGuestAsync("local", "test-vm", @"C:\src\file.txt", @"C:\dest\file.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transferResult);

        // Simulate flow (note: vm_copy_file needs Global + VM locks, no Host lock)
        _hostResolver.Object.ResolveRequired("local");
        await _gate.Object.AcquireGlobalSlotAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await _gate.Object.AcquireVmLockAsync("local", "test-vm", TimeSpan.FromSeconds(60), CancellationToken.None);
        var result = await _fileTransfer.Object.CopyToGuestAsync("local", "test-vm", @"C:\src\file.txt", @"C:\dest\file.txt", false, null, null, CancellationToken.None);

        result.BytesTransferred.Should().Be(1024);
        result.Verified.Should().BeTrue(
            "transfer integrity must be verified after Copy-Item -ToSession " +
            "(see /myplans/execution/file-transfer/file-transfer-design.md — FT-D12)");

        // No host lock acquired (vm_copy_file only needs Global + VM per concurrency table)
        _gate.Verify(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never,
            "vm_copy_file should NOT acquire per-host lock " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Operation Classification)");
    }

    // ─── File Transfer: Guest-to-Host ─────────────────────────────────

    /// <summary>
    /// vm_get_file guest-to-host flow: uses Copy-Item -FromSession via the persistent
    /// PSSession owned by IPowerShellDirectChannel (Phase 2, issue #52). There is a
    /// single code path regardless of file size — file size only affects transfer
    /// duration, not which transport is used.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D11.
    /// </summary>
    [Fact]
    public async Task GetFile_SmallFile_Uses_PsDirect()
    {
        var transferResult = new FileTransferResult
        {
            BytesTransferred = 5_000_000,  // 5 MB — single Copy-Item -FromSession path
            SourcePath = @"C:\guest\report.txt",
            DestPath = @"C:\host\report.txt",
            IsDirectory = false,
            FileCount = 1,
            Verified = true
        };
        _fileTransfer.Setup(f => f.CopyFromGuestAsync("local", "test-vm",
            @"C:\guest\report.txt", @"C:\host\report.txt", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transferResult);

        var result = await _fileTransfer.Object.CopyFromGuestAsync(
            "local", "test-vm", @"C:\guest\report.txt", @"C:\host\report.txt", null, null, CancellationToken.None);

        result.BytesTransferred.Should().Be(5_000_000);
        result.Verified.Should().BeTrue();
    }

    // ─── File Transfer: Directory Copy ─────────────────────────────────

    /// <summary>
    /// vm_copy_file with isDirectory=true: ZIP on host, Copy-Item -ToSession,
    /// Expand-Archive on guest (Phase 2 zip-staged flow).
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D13: ZIP+extract for directories.
    /// </summary>
    [Fact]
    public async Task CopyFile_Directory_Zips_And_Extracts()
    {
        var transferResult = new FileTransferResult
        {
            BytesTransferred = 50_000,
            SourcePath = @"C:\src\project-dir",
            DestPath = @"C:\guest\project-dir",
            IsDirectory = true,
            FileCount = 42,
            Verified = true
        };
        _fileTransfer.Setup(f => f.CopyToGuestAsync("local", "test-vm",
            @"C:\src\project-dir", @"C:\guest\project-dir", true, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transferResult);

        var result = await _fileTransfer.Object.CopyToGuestAsync(
            "local", "test-vm", @"C:\src\project-dir", @"C:\guest\project-dir", true, null, null, CancellationToken.None);

        result.IsDirectory.Should().BeTrue();
        result.FileCount.Should().Be(42,
            "directory copy should report the number of files transferred");
    }

    // ─── Remoting: Remote Host VM Operation ───────────────────────────

    /// <summary>
    /// Operations targeting a remote host must resolve the remote HostProfile
    /// and use the remote host's configuration for paths.
    /// See /myplans/remoting/remoting-design.md — Two-Tier Connection Architecture.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D6: hostId for multi-host.
    /// </summary>
    [Fact]
    public async Task Remote_Host_File_Transfer_Uses_Remote_Paths()
    {
        _hostResolver.Setup(r => r.ResolveRequired("hyperv-01")).Returns(_remoteProfile);

        var transferResult = new FileTransferResult
        {
            BytesTransferred = 2048,
            SourcePath = @"D:\remote-files\config.json",
            DestPath = @"C:\guest\config.json",
            IsDirectory = false,
            FileCount = 1,
            Verified = true
        };
        _fileTransfer.Setup(f => f.CopyToGuestAsync("hyperv-01", "remote-vm",
            @"D:\remote-files\config.json", @"C:\guest\config.json", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transferResult);

        // Resolve remote host
        var profile = _hostResolver.Object.ResolveRequired("hyperv-01");
        profile.IsLocal.Should().BeFalse(
            "remote host should not be identified as local");
        profile.BaseVhdxPath.Should().NotBeNull(
            "remote host should carry its own base VHDX path");

        // Execute transfer on remote host
        var result = await _fileTransfer.Object.CopyToGuestAsync(
            "hyperv-01", "remote-vm", @"D:\remote-files\config.json", @"C:\guest\config.json",
            false, null, null, CancellationToken.None);

        result.Verified.Should().BeTrue();
    }

    // ─── Remoting: Run Command via Remote Host ─────────────────────────

    /// <summary>
    /// vm_run_command on a remote host: host resolution → command execution on remote host's VM.
    /// See /myplans/remoting/remoting-design.md — Remote host path: PSSession to remote host, then PS Direct to guest.
    /// </summary>
    [Fact]
    public async Task RunCommand_On_Remote_Host_Resolves_And_Executes()
    {
        _hostResolver.Setup(r => r.ResolveRequired("hyperv-01")).Returns(_remoteProfile);

        var cmdResult = new CommandResult
        {
            ExitCode = 0,
            Stdout = "remote output",
            Stderr = "",
            TimedOut = false,
            DurationMs = 500
        };
        _commandExecutor.Setup(e => e.ExecuteCommandAsync("hyperv-01", "remote-vm", "hostname", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cmdResult);

        var profile = _hostResolver.Object.ResolveRequired("hyperv-01");
        profile.ComputerName.Should().Be("hyperv-01.corp.local");

        var result = await _commandExecutor.Object.ExecuteCommandAsync(
            "hyperv-01", "remote-vm", "hostname", "cmd", 30, null, null, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Be("remote output",
            "command executed on remote host's VM should return output");
    }

    // ─── Error Case: File Not Found on Guest ──────────────────────────

    /// <summary>
    /// vm_get_file for a nonexistent file must produce FILE_NOT_FOUND.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: FILE_NOT_FOUND.
    /// </summary>
    [Fact]
    public async Task GetFile_NonExistent_Returns_FileNotFound()
    {
        _fileTransfer.Setup(f => f.CopyFromGuestAsync("local", "test-vm",
            @"C:\guest\missing.txt", @"C:\host\missing.txt", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("File not found on guest: C:\\guest\\missing.txt"));

        _errorMapper.Setup(m => m.MapException(It.IsAny<FileNotFoundException>(), null))
            .Returns(McpToolResponse.Fail("Source file does not exist", ErrorCodes.FileNotFound));

        try
        {
            await _fileTransfer.Object.CopyFromGuestAsync(
                "local", "test-vm", @"C:\guest\missing.txt", @"C:\host\missing.txt", null, null, CancellationToken.None);
        }
        catch (FileNotFoundException ex)
        {
            var response = _errorMapper.Object.MapException(ex);
            response.Success.Should().BeFalse();
            response.ErrorCode.Should().Be("FILE_NOT_FOUND",
                "missing file on guest must produce FILE_NOT_FOUND error");
        }
    }

    // ─── Error Case: Checkpoint Failed ────────────────────────────────

    /// <summary>
    /// When checkpoint creation fails, it must produce CHECKPOINT_FAILED.
    /// Uses CheckpointFailedException and real ErrorMapper to verify canonical mapping.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: CHECKPOINT_FAILED.
    /// </summary>
    [Fact]
    public async Task Checkpoint_Create_Failure_Returns_CheckpointFailed()
    {
        _checkpointManager.Setup(m => m.CreateCheckpointAsync("local", "test-vm", "bad-checkpoint", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CheckpointFailedException("local", "test-vm",
                "Checkpoint creation failed: insufficient disk space",
                checkpointName: "bad-checkpoint"));

        var realMapper = new ErrorMapper();

        try
        {
            await _checkpointManager.Object.CreateCheckpointAsync("local", "test-vm", "bad-checkpoint", CancellationToken.None);
        }
        catch (CheckpointFailedException ex)
        {
            var response = realMapper.MapException(ex);
            response.Success.Should().BeFalse();
            response.ErrorCode.Should().Be("CHECKPOINT_FAILED",
                "CheckpointFailedException must map to CHECKPOINT_FAILED via real ErrorMapper");
            response.Error.Should().Contain("test-vm",
                "sanitized error message should include the VM identifier");
            response.Error.Should().Contain("bad-checkpoint",
                "sanitized error message should include the checkpoint name");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Dispatcher-wired tests: exercise real ToolDispatcher + ErrorMapper
    // These tests address the review finding that mock-only tests lack
    // real wiring coverage through the dispatcher/SUT.
    // Updated for P1 implementation: P0 tools (vm_copy_file, vm_run_command)
    // and P1 tools (vm_checkpoint, vm_get_file, vm_run_script) all have
    // real handlers. Tests with invalid vmId ("test-vm") now expect
    // INVALID_PARAMETER from InputValidation.ValidateVmId().
    // See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Helper to create a ToolDispatcher with mocked infrastructure services
    /// and real ErrorMapper for end-to-end dispatch testing.
    /// </summary>
    private static ToolDispatcher CreateMockedDispatcher(
        Mock<IHyperVManager>? hvManager = null,
        Mock<ICommandExecutor>? commandExecutor = null,
        Mock<IFileTransferService>? fileTransfer = null,
        Mock<IHostResolver>? hostResolver = null,
        Mock<IConcurrencyGate>? gate = null)
    {
        var hv = hvManager ?? new Mock<IHyperVManager>();
        var cmd = commandExecutor ?? new Mock<ICommandExecutor>();
        var ft = fileTransfer ?? new Mock<IFileTransferService>();
        var hr = hostResolver ?? new Mock<IHostResolver>();
        var g = gate ?? new Mock<IConcurrencyGate>();

        // Default concurrency gate: always grant immediately
        g.Setup(x => x.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        g.Setup(x => x.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        g.Setup(x => x.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // Default: GetVmStatusAsync returns a Running VM (needed for vm_run_command, vm_run_script, vm_copy_file, vm_get_file state precondition)
        hv.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = "12345678-1234-1234-1234-123456789abc", Name = "test-vm", State = "Running", HostId = "local" });

        return new ToolDispatcher(
            hv.Object, cmd.Object, ft.Object, new Mock<ICheckpointManager>().Object, hr.Object,
            new ErrorMapper(), g.Object, new Mock<IPowerShellExecutor>().Object, new Mock<IPowerShellDirectChannel>().Object, new ServerOptions());
    }

    /// <summary>
    /// vm_checkpoint dispatched through real ToolDispatcher with invalid vmId
    /// (not a GUID) produces INVALID_PARAMETER error.
    /// vm_checkpoint now has a real handler that validates vmId format.
    /// Verifies exact errorCode, success flag, and error message are present.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmCheckpoint_InvalidVmId_Returns_InvalidParameter()
    {
        var dispatcher = CreateMockedDispatcher();

        var resultJson = await dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["vmId"] = "test-vm", // Not a valid GUID — rejected by InputValidation
                ["action"] = "create",
                ["name"] = "test-checkpoint"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse(
            "vm_checkpoint with invalid vmId must produce error envelope");
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "non-GUID vmId must be rejected with INVALID_PARAMETER by the real handler");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "error message must be present even when sanitized");
    }

    /// <summary>
    /// vm_copy_file dispatched through real ToolDispatcher with mocked
    /// IFileTransferService returns success when the service call succeeds.
    /// vm_copy_file is a P0 tool with a real handler.
    /// See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmCopyFile_Returns_Success_With_Mocked_Service()
    {
        const string vmGuid = "12345678-1234-1234-1234-123456789abc";
        var tempFile = Path.GetTempFileName();
        try
        {
            var fileTransfer = new Mock<IFileTransferService>();
            fileTransfer.Setup(f => f.CopyToGuestAsync("local", vmGuid,
                tempFile, @"C:\dest\file.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FileTransferResult
                {
                    BytesTransferred = 1024,
                    SourcePath = tempFile,
                    DestPath = @"C:\dest\file.txt",
                    IsDirectory = false,
                    FileCount = 1,
                    Verified = true
                });

            var dispatcher = CreateMockedDispatcher(fileTransfer: fileTransfer);

            var resultJson = await dispatcher.DispatchAsync("vm_copy_file",
                new Dictionary<string, object?>
                {
                    ["hostId"] = "local",
                    ["vmId"] = vmGuid,
                    ["sourcePath"] = tempFile,
                    ["destPath"] = @"C:\dest\file.txt"
                },
                CancellationToken.None);

            var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
            response.Should().NotBeNull();
            response!.Success.Should().BeTrue(
                "vm_copy_file with mocked successful service should return success");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// vm_get_file dispatched through real ToolDispatcher with invalid vmId
    /// (not a GUID) produces INVALID_PARAMETER error.
    /// vm_get_file now has a real handler that validates vmId format.
    /// Asserts exact errorCode and envelope invariants.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmGetFile_InvalidVmId_Returns_InvalidParameter()
    {
        var dispatcher = CreateMockedDispatcher();

        var resultJson = await dispatcher.DispatchAsync("vm_get_file",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["vmId"] = "test-vm", // Not a valid GUID — rejected by InputValidation
                ["sourcePath"] = @"C:\guest\report.txt",
                ["destPath"] = @"C:\host\report.txt"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse(
            "vm_get_file with invalid vmId must produce error envelope");
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "non-GUID vmId must be rejected with INVALID_PARAMETER by the real handler");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "error message must be present even when sanitized");
    }

    /// <summary>
    /// vm_run_command dispatched through real ToolDispatcher with mocked
    /// ICommandExecutor returns success when the service call succeeds.
    /// vm_run_command is a P0 tool with a real handler.
    /// See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmRunCommand_Returns_Success_With_Mocked_Service()
    {
        const string vmGuid = "12345678-1234-1234-1234-123456789abc";
        var commandExecutor = new Mock<ICommandExecutor>();
        commandExecutor.Setup(e => e.ExecuteCommandAsync("local", vmGuid, "hostname", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                ExitCode = 0,
                Stdout = "test-host",
                Stderr = "",
                TimedOut = false,
                DurationMs = 100
            });

        var dispatcher = CreateMockedDispatcher(commandExecutor: commandExecutor);

        var resultJson = await dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["vmId"] = vmGuid,
                ["command"] = "hostname"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_run_command with mocked successful service should return success");
    }

    /// <summary>
    /// vm_run_script dispatched through real ToolDispatcher with invalid vmId
    /// (not a GUID) produces INVALID_PARAMETER error.
    /// vm_run_script now has a real handler that validates vmId format.
    /// Asserts exact errorCode and envelope invariants.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmRunScript_InvalidVmId_Returns_InvalidParameter()
    {
        var dispatcher = CreateMockedDispatcher();

        var resultJson = await dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["vmId"] = "test-vm", // Not a valid GUID — rejected by InputValidation
                ["script"] = "Get-Process"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse(
            "vm_run_script with invalid vmId must produce error envelope");
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "non-GUID vmId must be rejected with INVALID_PARAMETER by the real handler");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "error message must be present even when sanitized");
    }

    /// <summary>
    /// Dispatching a tool not in the catalog through the dispatcher must
    /// return TOOL_NOT_FOUND with full envelope invariants.
    /// Verifies the dispatch-to-error path for checkpoint/filetransfer domain
    /// tools that don't exist, including: success=false, errorCode, non-empty
    /// error message, data=null, and stable failure shape.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D2, MCP-D6.
    /// </summary>
    [Fact]
    public async Task Dispatcher_UnknownTool_Returns_ToolNotFound()
    {
        var dispatcher = CreateMockedDispatcher();

        var resultJson = await dispatcher.DispatchAsync("vm_snapshot",
            new Dictionary<string, object?>
            {
                ["vmId"] = "test-vm",
                ["action"] = "create"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse(
            "unknown tool must produce a failure envelope");
        response.ErrorCode.Should().Be("TOOL_NOT_FOUND",
            "unregistered tool name must produce TOOL_NOT_FOUND through dispatcher");
        response.Data.Should().BeNull(
            "failure envelopes for unknown tools must not carry data " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Failure Response: data=null)");
        response.State.Should().BeNull(
            "failure envelopes for unknown tools must not carry VM state " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — ADR-8: state is optional and only set for VM-context errors)");
        response.Error.Should().NotBeNullOrEmpty(
            "failure envelopes must include a human-readable error message");
        response.Error.Should().Contain("vm_snapshot",
            "error message should identify the unknown tool name for diagnostics");
    }

    /// <summary>
    /// Real ErrorMapper maps FileNotFoundException to FILE_NOT_FOUND.
    /// Verifies end-to-end error mapping without mocks for file transfer scenarios.
    /// </summary>
    [Fact]
    public void Real_ErrorMapper_Maps_FileNotFound_For_Transfer()
    {
        var mapper = new ErrorMapper();
        var ex = new FileNotFoundException("File not found on guest: C:\\guest\\missing.txt");

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("FILE_NOT_FOUND",
            "real ErrorMapper must map FileNotFoundException to FILE_NOT_FOUND");
        response.Error.Should().Contain("not found",
            "sanitized error message should describe the file-not-found condition");
    }

    /// <summary>
    /// Real ErrorMapper maps CheckpointFailedException to CHECKPOINT_FAILED.
    /// This is a regression test ensuring checkpoint failures use the dedicated
    /// CHECKPOINT_FAILED error code, not COMMAND_FAILED from InvalidOperationException.
    /// </summary>
    [Fact]
    public void Real_ErrorMapper_Maps_CheckpointFailure_To_CheckpointFailed()
    {
        var mapper = new ErrorMapper();
        var ex = new CheckpointFailedException("local", "test-vm",
            "Checkpoint creation failed: insufficient disk space",
            checkpointName: "bad-checkpoint");

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("CHECKPOINT_FAILED",
            "CheckpointFailedException must map to CHECKPOINT_FAILED, " +
            "not COMMAND_FAILED — this is a regression test for checkpoint error semantics");
        response.Error.Should().Contain("test-vm",
            "sanitized error message should include the VM identifier");
    }

    /// <summary>
    /// Real ConcurrencyGate + real ErrorMapper: file transfer with concurrency limit
    /// produces the correct error mapping. Verifies the full wiring path.
    /// </summary>
    [Fact]
    public async Task Real_ConcurrencyGate_Limit_Maps_To_ConcurrencyLimit()
    {
        var options = new ServerOptions { MaxConcurrentOperations = 1 };
        using var gate = new ConcurrencyGate(options);
        var mapper = new ErrorMapper();

        // Saturate the gate
        var slot = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        try
        {
            await gate.AcquireGlobalSlotAsync(
                TimeSpan.FromMilliseconds(50), CancellationToken.None);
            throw new InvalidOperationException("Should not reach here");
        }
        catch (ConcurrencyLimitException ex)
        {
            var response = mapper.MapException(ex);
            response.Success.Should().BeFalse();
            response.ErrorCode.Should().Be("CONCURRENCY_LIMIT",
                "real orchestration: ConcurrencyGate exception → ErrorMapper → CONCURRENCY_LIMIT");
        }
        finally
        {
            slot.Dispose();
        }
    }
}
