using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Regression tests for code review feedback issues:
///
/// Round 1:
/// Issue 1: Hardcoded "local" host selection in ToolDispatcher — handlers now use
///          ServerOptions.DefaultHostId instead of hardcoded "local".
///
/// Issue 2: Hardcoded 30-second lock timeouts in ToolDispatcher — handlers now use
///          ServerOptions.QueueTimeoutSeconds and ServerOptions.VmLockTimeoutSeconds.
///
/// Issue 3: Over-broad error classification in HyperVManager — HandleError now skips
///          VmNotFoundException mapping for create operations where "not found" errors
///          refer to missing base VHDX or storage paths, not missing VMs.
///
/// Round 2:
/// Issue R2-1: Non-timeout command failures surfaced as success in vm_run_command —
///             HandleRunCommandAsync now checks ExitCode != 0 and returns COMMAND_FAILED.
///
/// Issue R2-2: Host-to-guest file verification is incomplete and unenforced —
///             BuildCopyToGuestScript now verifies file size, and CopyToGuestAsync throws
///             IOException when verification fails.
///
/// Issue R2-3: Missing VM in file-copy path not mapped to VM_NOT_FOUND —
///             FileTransferService now throws VmNotFoundException when the error output
///             indicates the VM was not found.
/// </summary>
[Trait("Category", "Runtime")]
public class ReviewFeedbackRegressionTests
{
    private const string TestVmGuid = "12345678-1234-1234-1234-123456789abc";

    // ═══════════════════════════════════════════════════════════════════
    // Issue 1: Default hostId uses ServerOptions.DefaultHostId
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression test for Issue 1: When hostId is omitted, ToolDispatcher must use
    /// ServerOptions.DefaultHostId (not hardcoded "local"). This test configures
    /// DefaultHostId="remote-default" and verifies the handler forwards that value.
    /// </summary>
    [Fact]
    public async Task Issue1_OmittedHostId_Uses_ServerOptions_DefaultHostId_Not_Hardcoded_Local()
    {
        // Arrange — configure DefaultHostId to something other than "local"
        var options = new ServerOptions { DefaultHostId = "remote-default" };
        var hvManager = new Mock<IHyperVManager>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // vm_list only acquires global slot, so this is sufficient
        hvManager.Setup(m => m.ListVmsAsync("remote-default", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>().AsReadOnly());

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);

        // Act — dispatch vm_list without specifying hostId
        var resultJson = await dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        // Assert — hostId "remote-default" must have been forwarded, not "local"
        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        hvManager.Verify(
            m => m.ListVmsAsync("remote-default", null, It.IsAny<CancellationToken>()),
            Times.Once,
            "When hostId is omitted, ToolDispatcher must use ServerOptions.DefaultHostId ('remote-default'), " +
            "not hardcoded 'local'. This was the bug reported in Issue 1.");

        // Confirm "local" was never used
        hvManager.Verify(
            m => m.ListVmsAsync("local", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Hardcoded 'local' must NOT be used when ServerOptions.DefaultHostId is configured differently.");
    }

    /// <summary>
    /// Regression test for Issue 1: vm_create must also use DefaultHostId, not "local".
    /// </summary>
    [Fact]
    public async Task Issue1_VmCreate_OmittedHostId_Uses_DefaultHostId()
    {
        var options = new ServerOptions { DefaultHostId = "custom-host" };
        var hvManager = new Mock<IHyperVManager>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        hvManager.Setup(m => m.CreateVmAsync("custom-host", "new-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = "new-vm", Name = "new-vm", State = "Running", HostId = "custom-host" });

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);

        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?> { ["name"] = "new-vm" },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        hvManager.Verify(
            m => m.CreateVmAsync("custom-host", "new-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()),
            Times.Once,
            "vm_create must use ServerOptions.DefaultHostId when hostId is omitted (Issue 1).");
    }

    /// <summary>
    /// Regression test for Issue 1: vm_run_command must use DefaultHostId, not "local".
    /// </summary>
    [Fact]
    public async Task Issue1_VmRunCommand_OmittedHostId_Uses_DefaultHostId()
    {
        var options = new ServerOptions { DefaultHostId = "my-host" };
        var commandExecutor = new Mock<ICommandExecutor>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        commandExecutor.Setup(e => e.ExecuteCommandAsync("my-host", TestVmGuid, "hostname", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult { ExitCode = 0, Stdout = "host1", Stderr = "", TimedOut = false, DurationMs = 50 });

        var hvManager = new Mock<IHyperVManager>();
        hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            commandExecutor.Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);

        var resultJson = await dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["command"] = "hostname"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        commandExecutor.Verify(
            e => e.ExecuteCommandAsync("my-host", TestVmGuid, "hostname", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "vm_run_command must use ServerOptions.DefaultHostId when hostId is omitted (Issue 1).");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Issue 2: Lock timeouts use ServerOptions configuration
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression test for Issue 2: Global slot acquisition must use
    /// ServerOptions.QueueTimeoutSeconds, not hardcoded 30 seconds.
    /// </summary>
    [Fact]
    public async Task Issue2_GlobalSlot_Uses_QueueTimeoutSeconds_Not_Hardcoded_30()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            QueueTimeoutSeconds = 120,  // Custom value to distinguish from hardcoded 30
        };
        var hvManager = new Mock<IHyperVManager>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        hvManager.Setup(m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>().AsReadOnly());

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);

        await dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?> { ["hostId"] = "local" },
            CancellationToken.None);

        // Verify the global slot was acquired with 120 seconds, not 30
        gate.Verify(
            g => g.AcquireGlobalSlotAsync(TimeSpan.FromSeconds(120), It.IsAny<CancellationToken>()),
            Times.Once,
            "Global slot timeout must use ServerOptions.QueueTimeoutSeconds (120s), " +
            "not hardcoded 30s. This was the bug reported in Issue 2.");
    }

    /// <summary>
    /// Regression test for Issue 2: VM lock acquisition must use
    /// ServerOptions.VmLockTimeoutSeconds, not hardcoded 30 seconds.
    /// </summary>
    [Fact]
    public async Task Issue2_VmLock_Uses_VmLockTimeoutSeconds_Not_Hardcoded_30()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            QueueTimeoutSeconds = 45,
            VmLockTimeoutSeconds = 90,  // Custom value to distinguish from hardcoded 30
        };
        var hvManager = new Mock<IHyperVManager>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        hvManager.Setup(m => m.StartVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, State = "Running" });

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);

        await dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?> { ["vmId"] = TestVmGuid, ["hostId"] = "local" },
            CancellationToken.None);

        // Verify queue timeout is used for global and host locks
        gate.Verify(
            g => g.AcquireGlobalSlotAsync(TimeSpan.FromSeconds(45), It.IsAny<CancellationToken>()),
            Times.Once,
            "Global slot must use QueueTimeoutSeconds (45s) for vm_start.");

        gate.Verify(
            g => g.AcquireHostLockAsync("local", TimeSpan.FromSeconds(45), It.IsAny<CancellationToken>()),
            Times.Once,
            "Host lock must use QueueTimeoutSeconds (45s) for vm_start.");

        // Verify VM lock uses VmLockTimeoutSeconds
        gate.Verify(
            g => g.AcquireVmLockAsync("local", TestVmGuid, TimeSpan.FromSeconds(90), It.IsAny<CancellationToken>()),
            Times.Once,
            "VM lock timeout must use ServerOptions.VmLockTimeoutSeconds (90s), " +
            "not hardcoded 30s. This was the bug reported in Issue 2.");
    }

    /// <summary>
    /// Regression test for Issue 2: vm_copy_file must use configured timeouts.
    /// </summary>
    [Fact]
    public async Task Issue2_VmCopyFile_Uses_Configured_Timeouts()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var options = new ServerOptions
            {
                DefaultHostId = "local",
                QueueTimeoutSeconds = 60,
                VmLockTimeoutSeconds = 120,
            };
            var fileTransfer = new Mock<IFileTransferService>();
            var gate = new Mock<IConcurrencyGate>();

            gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IDisposable>());
            gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IDisposable>());

            fileTransfer.Setup(f => f.CopyToGuestAsync("local", TestVmGuid, tempFile, @"C:\dst\f.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FileTransferResult { BytesTransferred = 100, Verified = true, FileCount = 1 });

            var hvManager = new Mock<IHyperVManager>();
            hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

            var dispatcher = new ToolDispatcher(
                hvManager.Object,
                new Mock<ICommandExecutor>().Object,
                fileTransfer.Object,
                new Mock<ICheckpointManager>().Object,
                new Mock<IHostResolver>().Object,
                new ErrorMapper(),
                gate.Object,
                new Mock<IPowerShellExecutor>().Object,
                new Mock<IPowerShellDirectChannel>().Object,
                options);

            await dispatcher.DispatchAsync("vm_copy_file",
                new Dictionary<string, object?>
                {
                    ["vmId"] = TestVmGuid,
                    ["sourcePath"] = tempFile,
                    ["destPath"] = @"C:\dst\f.txt",
                    ["hostId"] = "local",
                },
                CancellationToken.None);

            gate.Verify(
                g => g.AcquireGlobalSlotAsync(TimeSpan.FromSeconds(60), It.IsAny<CancellationToken>()),
                Times.Once,
                "vm_copy_file global slot must use QueueTimeoutSeconds (60s).");

            gate.Verify(
                g => g.AcquireVmLockAsync("local", TestVmGuid, TimeSpan.FromSeconds(120), It.IsAny<CancellationToken>()),
                Times.Once,
                "vm_copy_file VM lock must use VmLockTimeoutSeconds (120s), not hardcoded 30s (Issue 2).");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Issue 3: Context-aware error classification in HyperVManager
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression test for Issue 3: During CreateVmAsync, a "not found" error in stderr
    /// (e.g., missing base VHDX) must NOT be misclassified as VmNotFoundException.
    /// It should throw InvalidOperationException instead.
    /// </summary>
    [Fact]
    public async Task Issue3_CreateVm_NotFoundError_Does_Not_Throw_VmNotFoundException()
    {
        var mockExecutor = new Mock<IPowerShellExecutor>();
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["local"] = new HostProfile
                {
                    HostId = "local",
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var logger = NullLoggerFactory.Instance.CreateLogger<HyperVManager>();
        var manager = new HyperVManager(mockExecutor.Object, hostResolver, options, logger, new TestIsoInspector());

        // Simulate PowerShell failure: base VHDX "not found"
        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 1,
                Stdout = string.Empty,
                Stderr = "The file 'C:\\Base\\base.vhdx' was not found",
                TimedOut = false,
                Cancelled = false,
                DurationMs = 50,
            });

        // Act
        Func<Task> act = async () => await manager.CreateVmAsync("local", "new-vm");

        // Assert — per LF-D17, CreateVmAsync now wraps all primary-pipeline failures
        // in VmCreateRollbackException after running the detached-CTS rollback.
        // The Issue 3 invariant is preserved by checking that the rollback exception
        // does NOT wrap a VmNotFoundException (a "not found" error against the base
        // VHDX must not be misclassified as a missing VM).
        var ex = await act.Should().ThrowAsync<VmCreateRollbackException>(
            "LF-D17: CreateVmAsync wraps primary failures in VmCreateRollbackException.");

        ex.Which.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "non-cancel / non-timeout primary failure ⇒ COMMAND_FAILED");
        ex.Which.InnerException.Should().NotBeOfType<VmNotFoundException>(
            "Issue 3: during create, 'not found' refers to base VHDX, not a VM. " +
            "It must NOT be wrapped as a VmNotFoundException inner.");
        ex.Which.Message.Should().Contain("not found",
            "The original error message should be preserved in the wrapping exception.");
    }

    /// <summary>
    /// Regression test for Issue 3: During CreateVmAsync, a "does not exist" error
    /// (e.g., invalid storage path) must NOT be misclassified as VmNotFoundException.
    /// </summary>
    [Fact]
    public async Task Issue3_CreateVm_DoesNotExistError_Does_Not_Throw_VmNotFoundException()
    {
        var mockExecutor = new Mock<IPowerShellExecutor>();
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["local"] = new HostProfile
                {
                    HostId = "local",
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var logger = NullLoggerFactory.Instance.CreateLogger<HyperVManager>();
        var manager = new HyperVManager(mockExecutor.Object, hostResolver, options, logger, new TestIsoInspector());

        // Simulate PowerShell failure: storage path "does not exist"
        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 1,
                Stdout = string.Empty,
                Stderr = "The path 'C:\\HyperVMCP\\VMs\\new-vm' does not exist",
                TimedOut = false,
                Cancelled = false,
                DurationMs = 50,
            });

        Func<Task> act = async () => await manager.CreateVmAsync("local", "new-vm");

        // LF-D17: CreateVmAsync wraps primary-pipeline failures in VmCreateRollbackException.
        // Issue 3 invariant preserved by checking the inner is NOT a VmNotFoundException.
        var ex = await act.Should().ThrowAsync<VmCreateRollbackException>(
            "LF-D17: CreateVmAsync wraps primary failures in VmCreateRollbackException.");

        ex.Which.ErrorCode.Should().Be(ErrorCodes.CommandFailed);
        ex.Which.InnerException.Should().NotBeOfType<VmNotFoundException>(
            "Issue 3: during create, 'does not exist' refers to a storage path, not a VM.");
    }

    /// <summary>
    /// Regression test for Issue 3: Non-create operations (e.g., StartVmAsync) must
    /// still correctly map "not found" errors to VmNotFoundException.
    /// This ensures the fix for Issue 3 didn't break existing behavior.
    /// </summary>
    [Fact]
    public async Task Issue3_StartVm_NotFoundError_Still_Throws_VmNotFoundException()
    {
        var mockExecutor = new Mock<IPowerShellExecutor>();
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["local"] = new HostProfile
                {
                    HostId = "local",
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var logger = NullLoggerFactory.Instance.CreateLogger<HyperVManager>();
        var manager = new HyperVManager(mockExecutor.Object, hostResolver, options, logger, new TestIsoInspector());

        // Simulate PowerShell failure: VM "not found"
        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 1,
                Stdout = string.Empty,
                Stderr = "VM not found: " + TestVmGuid,
                TimedOut = false,
                Cancelled = false,
                DurationMs = 50,
            });

        Func<Task> act = async () => await manager.StartVmAsync("local", TestVmGuid);

        await act.Should().ThrowAsync<VmNotFoundException>(
            "For non-create operations like StartVmAsync, 'not found' errors must still " +
            "correctly map to VmNotFoundException. The Issue 3 fix must not break this.");
    }

    /// <summary>
    /// Regression test for Issue 3: GetVmStatusAsync must still correctly map
    /// "does not exist" errors to VmNotFoundException.
    /// </summary>
    [Fact]
    public async Task Issue3_GetVmStatus_DoesNotExistError_Still_Throws_VmNotFoundException()
    {
        var mockExecutor = new Mock<IPowerShellExecutor>();
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["local"] = new HostProfile
                {
                    HostId = "local",
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var logger = NullLoggerFactory.Instance.CreateLogger<HyperVManager>();
        var manager = new HyperVManager(mockExecutor.Object, hostResolver, options, logger, new TestIsoInspector());

        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 1,
                Stdout = string.Empty,
                Stderr = "The virtual machine does not exist",
                TimedOut = false,
                Cancelled = false,
                DurationMs = 50,
            });

        Func<Task> act = async () => await manager.GetVmStatusAsync("local", TestVmGuid);

        await act.Should().ThrowAsync<VmNotFoundException>(
            "For GetVmStatusAsync, 'does not exist' errors must still map to VmNotFoundException. " +
            "The Issue 3 fix must not break this existing behavior.");
    }

    /// <summary>
    /// Regression test for Issue 3: CreateVmAsync "already exists" errors must still
    /// correctly map to VmAlreadyExistsException (unaffected by the isCreateOperation flag).
    /// </summary>
    [Fact]
    public async Task Issue3_CreateVm_AlreadyExistsError_Still_Throws_VmAlreadyExistsException()
    {
        var mockExecutor = new Mock<IPowerShellExecutor>();
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["local"] = new HostProfile
                {
                    HostId = "local",
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var logger = NullLoggerFactory.Instance.CreateLogger<HyperVManager>();
        var manager = new HyperVManager(mockExecutor.Object, hostResolver, options, logger, new TestIsoInspector());

        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 1,
                Stdout = string.Empty,
                Stderr = "VM with name 'existing-vm' already exists",
                TimedOut = false,
                Cancelled = false,
                DurationMs = 50,
            });

        Func<Task> act = async () => await manager.CreateVmAsync("local", "existing-vm");

        // Issue #203 / VC-DUP-D5 (supersedes the earlier LF-D17 wrapping for
        // this case): CreateVmAsync now throws VmAlreadyExistsException directly
        // for name-collision failures (no VM was created on this path, so there
        // is nothing to roll back). The Issue 3 invariant ("already exists"
        // must not be misclassified as 'not found') is preserved by the typed
        // exception, which maps to ErrorCode=VM_ALREADY_EXISTS at the envelope
        // layer.
        var ex = await act.Should().ThrowAsync<VmAlreadyExistsException>(
            "VC-DUP-D5: name-collision throws VmAlreadyExistsException directly, not a rollback wrapper.");

        ex.Which.VmName.Should().Be("existing-vm");
        ex.Which.HostId.Should().Be("local");
    }
    // ═══════════════════════════════════════════════════════════════════
    // Round 2, Issue R2-1: Non-zero exit code returns COMMAND_FAILED
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression test for R2-1: A command that exits with non-zero exit code must
    /// return success: false with COMMAND_FAILED error code. Previously, only timeout
    /// and cancellation were treated as failures; non-zero exit codes were returned
    /// as successful MCP responses.
    /// </summary>
    [Fact]
    public async Task R2_Issue1_NonZeroExitCode_Returns_CommandFailed()
    {
        var options = new ServerOptions { DefaultHostId = "local" };
        var commandExecutor = new Mock<ICommandExecutor>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // Command exits with non-zero exit code (e.g., command not found on guest)
        commandExecutor.Setup(e => e.ExecuteCommandAsync("local", TestVmGuid, "bad-command", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                ExitCode = 1,
                Stdout = "",
                Stderr = "'bad-command' is not recognized",
                TimedOut = false,
                Cancelled = false,
                DurationMs = 50
            });

        var hvManager = new Mock<IHyperVManager>();
        hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            commandExecutor.Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);

        var resultJson = await dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["command"] = "bad-command"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse(
            "A command with non-zero exit code must return success: false (R2-Issue 1).");
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "Non-zero exit code must map to COMMAND_FAILED error code per the MCP error taxonomy.");
        response.Error.Should().Contain("exit code 1");
    }

    /// <summary>
    /// Regression test for R2-1: A command with exit code 0 must still return success: true.
    /// Ensures the fix doesn't break successful commands.
    /// </summary>
    [Fact]
    public async Task R2_Issue1_ZeroExitCode_Returns_Success()
    {
        var options = new ServerOptions { DefaultHostId = "local" };
        var commandExecutor = new Mock<ICommandExecutor>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        commandExecutor.Setup(e => e.ExecuteCommandAsync("local", TestVmGuid, "hostname", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                ExitCode = 0,
                Stdout = "myhost",
                Stderr = "",
                TimedOut = false,
                Cancelled = false,
                DurationMs = 50
            });

        var hvManager = new Mock<IHyperVManager>();
        hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            commandExecutor.Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);

        var resultJson = await dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["command"] = "hostname"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue(
            "A command with exit code 0 must return success: true. " +
            "The R2-Issue 1 fix must not break successful commands.");
    }

    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task R2_Issue2_CopyToGuest_UnverifiedTransfer_MapsToTransferFailed_InDispatcher()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var options = new ServerOptions { DefaultHostId = "local" };
            var fileTransfer = new Mock<IFileTransferService>();
            var gate = new Mock<IConcurrencyGate>();

            gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IDisposable>());
            gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IDisposable>());

            // Simulate IOException from unverified transfer
            fileTransfer.Setup(f => f.CopyToGuestAsync("local", TestVmGuid, tempFile, @"C:\dst\f.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("File transfer verification failed"));

            var hvManager = new Mock<IHyperVManager>();
            hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

            var dispatcher = new ToolDispatcher(
                hvManager.Object,
                new Mock<ICommandExecutor>().Object,
                fileTransfer.Object,
                new Mock<ICheckpointManager>().Object,
                new Mock<IHostResolver>().Object,
                new ErrorMapper(),
                gate.Object,
                new Mock<IPowerShellExecutor>().Object,
                new Mock<IPowerShellDirectChannel>().Object,
                options);

            var resultJson = await dispatcher.DispatchAsync("vm_copy_file",
                new Dictionary<string, object?>
                {
                    ["vmId"] = TestVmGuid,
                    ["sourcePath"] = tempFile,
                    ["destPath"] = @"C:\dst\f.txt",
                    ["hostId"] = "local",
                },
                CancellationToken.None);

            var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
            response!.Success.Should().BeFalse();
            response.ErrorCode.Should().Be(ErrorCodes.TransferFailed,
                "IOException from unverified transfer must map to TRANSFER_FAILED (R2-Issue 2).");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Round 2, Issue R2-3: Missing VM in file-copy maps to VM_NOT_FOUND
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    [Fact]
    public async Task R2_Issue3_CopyToGuest_VmNotFound_MapsToVmNotFound_InDispatcher()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var options = new ServerOptions { DefaultHostId = "local" };
            var fileTransfer = new Mock<IFileTransferService>();
            var gate = new Mock<IConcurrencyGate>();

            gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IDisposable>());
            gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IDisposable>());

            fileTransfer.Setup(f => f.CopyToGuestAsync("local", TestVmGuid, tempFile, @"C:\dst\f.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new VmNotFoundException("local", TestVmGuid));

            var hvManager = new Mock<IHyperVManager>();
            hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

            var dispatcher = new ToolDispatcher(
                hvManager.Object,
                new Mock<ICommandExecutor>().Object,
                fileTransfer.Object,
                new Mock<ICheckpointManager>().Object,
                new Mock<IHostResolver>().Object,
                new ErrorMapper(),
                gate.Object,
                new Mock<IPowerShellExecutor>().Object,
                new Mock<IPowerShellDirectChannel>().Object,
                options);

            var resultJson = await dispatcher.DispatchAsync("vm_copy_file",
                new Dictionary<string, object?>
                {
                    ["vmId"] = TestVmGuid,
                    ["sourcePath"] = tempFile,
                    ["destPath"] = @"C:\dst\f.txt",
                    ["hostId"] = "local",
                },
                CancellationToken.None);

            var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
            response!.Success.Should().BeFalse();
            response.ErrorCode.Should().Be(ErrorCodes.VmNotFound,
                "VmNotFoundException from file transfer must map to VM_NOT_FOUND (R2-Issue 3).");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Round 3, Issue R3-1: Shell-launch failure in inner catch must
    // produce ExitCode != 0 in CommandExecutor generated scripts
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression test for R3-1: When a guest shell cannot be launched (inner catch fires),
    /// the generated script must produce ExitCode != 0. Previously, $LASTEXITCODE could be
    /// $null (no native process ran), and the fallback "else { 0 }" produced ExitCode = 0,
    /// causing the dispatcher to return success despite the failure.
    ///
    /// This test simulates the scenario by returning JSON with ExitCode=1 (as the fixed
    /// script now does) and verifies ToolDispatcher returns COMMAND_FAILED.
    /// </summary>
    [Fact]
    public async Task R3_Issue1_ShellLaunchFailure_InnerCatch_Returns_CommandFailed()
    {
        var options = new ServerOptions { DefaultHostId = "local" };
        var commandExecutor = new Mock<ICommandExecutor>();
        var gate = new Mock<IConcurrencyGate>();

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // Simulate the result from a shell-launch failure: the inner catch fires,
        // stderr contains the error, and ExitCode is 1 (the R3 fix ensures this).
        commandExecutor.Setup(e => e.ExecuteCommandAsync("local", TestVmGuid, "whoami", "pwsh", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                ExitCode = 1,
                Stdout = "",
                Stderr = "The term 'pwsh.exe' is not recognized as the name of a cmdlet",
                TimedOut = false,
                Cancelled = false,
                DurationMs = 50
            });

        var hvManager = new Mock<IHyperVManager>();
        hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            commandExecutor.Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);

        var resultJson = await dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["command"] = "whoami",
                ["shell"] = "pwsh"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse(
            "When the guest shell cannot be launched and the inner catch fires, " +
            "the result must have ExitCode != 0 and the dispatcher must return failure (R3-Issue 1).");
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "Shell-launch failure must map to COMMAND_FAILED error code.");
        response.Error.Should().Contain("exit code 1");
    }
}
