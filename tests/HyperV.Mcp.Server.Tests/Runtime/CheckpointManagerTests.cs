using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="CheckpointManager"/> with mocked <see cref="IPowerShellExecutor"/>
/// and <see cref="ISessionStore"/>.
/// See /myplans/vm-management/checkpoints/checkpoints-design.md -- Checkpoint Workflow.
///
/// These tests verify that CheckpointManager:
/// - Composes correct PowerShell scripts for each checkpoint operation
/// - Parses JSON output from PowerShell into CheckpointResult objects
/// - Throws appropriate typed exceptions for error conditions
/// - Enforces local-only constraint for Phase 1
/// - CP-D3: Invalidates cached PSSession after checkpoint restore
///
/// All tests use Moq to mock IPowerShellExecutor and ISessionStore so no actual
/// Hyper-V installation is needed.
/// </summary>
[Trait("Category", "Runtime")]
public class CheckpointManagerTests
{
    private readonly Mock<IPowerShellExecutor> _mockExecutor;
    private readonly Mock<ISessionStore> _mockSessionStore;
    private readonly ServerOptions _options;
    private readonly IHostResolver _hostResolver;
    private readonly ILogger<CheckpointManager> _logger;
    private readonly CheckpointManager _manager;

    /// <summary>
    /// Standard test VM ID used across tests.
    /// </summary>
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    /// <summary>
    /// Standard local host ID.
    /// </summary>
    private const string LocalHostId = "local";

    public CheckpointManagerTests()
    {
        _mockExecutor = new Mock<IPowerShellExecutor>();
        _mockSessionStore = new Mock<ISessionStore>();
        _options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [LocalHostId] = new HostProfile
                {
                    HostId = LocalHostId,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        _hostResolver = new HostResolver(_options);
        _logger = NullLoggerFactory.Instance.CreateLogger<CheckpointManager>();
        _manager = new CheckpointManager(_mockExecutor.Object, _hostResolver, _mockSessionStore.Object, _logger);
    }

    // --- Helper Methods -------------------------------------------------

    /// <summary>
    /// Creates a successful <see cref="PowerShellResult"/> with the given JSON stdout.
    /// </summary>
    private static PowerShellResult SuccessResult(string stdout) => new()
    {
        ExitCode = 0,
        Stdout = stdout,
        Stderr = string.Empty,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 100,
    };

    /// <summary>
    /// Creates a failed <see cref="PowerShellResult"/> with the given stderr message.
    /// </summary>
    private static PowerShellResult FailureResult(string stderr) => new()
    {
        ExitCode = 1,
        Stdout = string.Empty,
        Stderr = stderr,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 50,
    };

    /// <summary>
    /// Sample JSON output for a checkpoint create operation.
    /// </summary>
    private static string CreateCheckpointJson(string vmId = TestVmId, string cpName = "test-cp") =>
        $$"""
        {
            "Action": "create",
            "VmId": "{{vmId}}",
            "CheckpointName": "{{cpName}}",
            "Checkpoints": [
                {
                    "Name": "{{cpName}}",
                    "Id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    "CreatedAt": "2026-01-15T10:30:00+00:00"
                }
            ]
        }
        """;

    /// <summary>
    /// Sample JSON output for a checkpoint list operation with multiple checkpoints.
    /// </summary>
    private static string ListCheckpointsJson(string vmId = TestVmId) =>
        $$"""
        {
            "Action": "list",
            "VmId": "{{vmId}}",
            "CheckpointName": "",
            "Checkpoints": [
                {
                    "Name": "cp-1",
                    "Id": "11111111-1111-1111-1111-111111111111",
                    "CreatedAt": "2026-01-10T08:00:00+00:00"
                },
                {
                    "Name": "cp-2",
                    "Id": "22222222-2222-2222-2222-222222222222",
                    "CreatedAt": "2026-01-12T14:30:00+00:00"
                }
            ]
        }
        """;

    /// <summary>
    /// Sample JSON output for a checkpoint list with no checkpoints.
    /// </summary>
    private static string EmptyCheckpointsJson(string vmId = TestVmId) =>
        $$"""
        {
            "Action": "list",
            "VmId": "{{vmId}}",
            "CheckpointName": "",
            "Checkpoints": []
        }
        """;

    /// <summary>
    /// Sample JSON output for a checkpoint restore operation.
    /// </summary>
    private static string RestoreCheckpointJson(string vmId = TestVmId, string cpName = "test-cp") =>
        $$"""
        {
            "Action": "restore",
            "VmId": "{{vmId}}",
            "CheckpointName": "{{cpName}}",
            "Checkpoints": null
        }
        """;

    /// <summary>
    /// Sample JSON output for a checkpoint delete operation.
    /// </summary>
    private static string DeleteCheckpointJson(string vmId = TestVmId, string cpName = "test-cp") =>
        $$"""
        {
            "Action": "delete",
            "VmId": "{{vmId}}",
            "CheckpointName": "{{cpName}}",
            "Checkpoints": null
        }
        """;

    // --- CreateCheckpointAsync ---------------------------------------

    /// <summary>
    /// CreateCheckpointAsync should compose a script containing the VM ID and checkpoint name.
    /// Verifies the script is built correctly with proper PowerShell commands.
    /// </summary>
    [Fact]
    public async Task CreateCheckpoint_Composes_Script_With_VmId_And_Name()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(CreateCheckpointJson()));

        await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "before-update");

        capturedScript.Should().NotBeNull("the executor should have been called");
        capturedScript.Should().Contain(TestVmId,
            "the script should include the VM ID for checkpoint creation");
        capturedScript.Should().Contain("before-update",
            "the script should include the checkpoint name");
        capturedScript.Should().Contain("Checkpoint-VM",
            "the script should use Checkpoint-VM cmdlet");
        capturedScript.Should().Contain("-ComputerName localhost",
            "the script should use '-ComputerName localhost' for WMI workaround (LF-D7)");
    }

    /// <summary>
    /// CreateCheckpointAsync should parse JSON output and return a valid CheckpointResult.
    /// </summary>
    [Fact]
    public async Task CreateCheckpoint_Returns_Success_On_Valid_Result()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(CreateCheckpointJson(cpName: "my-checkpoint")));

        var result = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "my-checkpoint");

        result.Should().NotBeNull();
        result.Action.Should().Be("create");
        result.VmId.Should().Be(TestVmId);
        result.CheckpointName.Should().Be("my-checkpoint");
        result.Checkpoints.Should().NotBeNull();
        result.Checkpoints.Should().HaveCount(1);
        result.Checkpoints![0].Name.Should().Be("my-checkpoint");
        result.Checkpoints[0].Id.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// CreateCheckpointAsync should throw CheckpointFailedException on PowerShell failure.
    /// Issue 3 fix: HandleError now throws CheckpointFailedException instead of InvalidOperationException,
    /// so ErrorMapper maps to CHECKPOINT_FAILED instead of COMMAND_FAILED.
    /// </summary>
    [Fact]
    public async Task CreateCheckpoint_Throws_On_Failure()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Insufficient disk space for checkpoint"));

        Func<Task> act = async () =>
            await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "will-fail");

        await act.Should().ThrowAsync<CheckpointFailedException>()
            .WithMessage("*Insufficient disk space*");
    }

    // --- RestoreCheckpointAsync --------------------------------------

    /// <summary>
    /// CP-D3: RestoreCheckpointAsync should invalidate the cached PSSession after restore.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md -- CP-D3.
    /// </summary>
    [Fact]
    public async Task RestoreCheckpoint_Invalidates_Session()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(RestoreCheckpointJson(cpName: "snap-1")));

        _mockSessionStore
            .Setup(s => s.EvictAsync(LocalHostId, TestVmId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _manager.RestoreCheckpointAsync(LocalHostId, TestVmId, "snap-1");

        _mockSessionStore.Verify(
            s => s.EvictAsync(LocalHostId, TestVmId, It.IsAny<CancellationToken>()),
            Times.Once,
            "RestoreCheckpointAsync must invalidate cached PSSession after restore (CP-D3)");
    }

    /// <summary>
    /// RestoreCheckpointAsync should parse JSON output and return a valid CheckpointResult.
    /// </summary>
    [Fact]
    public async Task RestoreCheckpoint_Returns_Success_On_Valid_Result()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(RestoreCheckpointJson(cpName: "snap-restore")));

        _mockSessionStore
            .Setup(s => s.EvictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _manager.RestoreCheckpointAsync(LocalHostId, TestVmId, "snap-restore");

        result.Should().NotBeNull();
        result.Action.Should().Be("restore");
        result.VmId.Should().Be(TestVmId);
        result.CheckpointName.Should().Be("snap-restore");
    }

    // --- ListCheckpointsAsync ----------------------------------------

    /// <summary>
    /// ListCheckpointsAsync should return empty checkpoints list when no checkpoints exist.
    /// </summary>
    [Fact]
    public async Task ListCheckpoints_Returns_Empty_Array()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(EmptyCheckpointsJson()));

        var result = await _manager.ListCheckpointsAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.Action.Should().Be("list");
        result.Checkpoints.Should().NotBeNull();
        result.Checkpoints.Should().BeEmpty();
    }

    /// <summary>
    /// ListCheckpointsAsync should return multiple checkpoints when they exist.
    /// </summary>
    [Fact]
    public async Task ListCheckpoints_Returns_Multiple_Checkpoints()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(ListCheckpointsJson()));

        var result = await _manager.ListCheckpointsAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.Action.Should().Be("list");
        result.Checkpoints.Should().NotBeNull();
        result.Checkpoints.Should().HaveCount(2);
        result.Checkpoints![0].Name.Should().Be("cp-1");
        result.Checkpoints[1].Name.Should().Be("cp-2");
        result.Checkpoints[0].Id.Should().Be("11111111-1111-1111-1111-111111111111");
        result.Checkpoints[1].Id.Should().Be("22222222-2222-2222-2222-222222222222");
    }

    // --- DeleteCheckpointAsync ---------------------------------------

    /// <summary>
    /// DeleteCheckpointAsync should parse JSON output and return confirmation.
    /// </summary>
    [Fact]
    public async Task DeleteCheckpoint_Returns_Confirmation()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(DeleteCheckpointJson(cpName: "old-cp")));

        var result = await _manager.DeleteCheckpointAsync(LocalHostId, TestVmId, "old-cp");

        result.Should().NotBeNull();
        result.Action.Should().Be("delete");
        result.VmId.Should().Be(TestVmId);
        result.CheckpointName.Should().Be("old-cp");
    }

    // --- Remote Host Rejection --------------------------------------

    /// <summary>
    /// All CheckpointManager methods should throw NotSupportedException for remote hosts in Phase 1.
    /// Remote host support (WinRM) will be added in a future phase.
    /// </summary>
    [Fact]
    public async Task RejectsRemoteHost()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "remote1",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["remote1"] = new HostProfile
                {
                    HostId = "remote1",
                    ComputerName = "hyperv-server.contoso.com", // Not local ? IsLocal = false
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var manager = new CheckpointManager(_mockExecutor.Object, hostResolver, _mockSessionStore.Object, _logger);

        // Verify each method throws NotSupportedException for remote hosts.
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.CreateCheckpointAsync("remote1", TestVmId, "test-cp"));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.RestoreCheckpointAsync("remote1", TestVmId, "test-cp"));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.ListCheckpointsAsync("remote1", TestVmId));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.DeleteCheckpointAsync("remote1", TestVmId, "test-cp"));
    }

    // --- VmNotFound Error Handling ----------------------------------

    /// <summary>
    /// CreateCheckpointAsync should throw VmNotFoundException when PowerShell stderr contains "VM not found".
    /// </summary>
    [Fact]
    public async Task CreateCheckpoint_VmNotFound_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("VM not found: " + TestVmId));

        Func<Task> act = async () =>
            await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "test-cp");

        await act.Should().ThrowAsync<VmNotFoundException>();
    }

    // --- Issue 3: CheckpointFailedException Tests --------------------

    /// <summary>
    /// CreateCheckpointAsync should throw CheckpointFailedException (not InvalidOperationException)
    /// on generic PowerShell errors, so ErrorMapper maps to CHECKPOINT_FAILED.
    /// </summary>
    [Fact]
    public async Task CreateCheckpoint_Throws_CheckpointFailedException_On_Error()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("An unexpected error occurred during checkpoint creation"));

        Func<Task> act = async () =>
            await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "fail-cp");

        var ex = await act.Should().ThrowAsync<CheckpointFailedException>();
        ex.Which.HostId.Should().Be(LocalHostId);
        ex.Which.VmId.Should().Be(TestVmId);
    }

    // --- Issue 4: Post-Restore Recovery Tests ------------------------

    /// <summary>
    /// RestoreCheckpointAsync should execute post-restore recovery (VM state wait + clock resync)
    /// after the restore script + session invalidation.
    /// Issue 4 fix: verify that a second PowerShell call is made for post-restore recovery.
    /// </summary>
    [Fact]
    public async Task RestoreCheckpoint_PerformsPostRestoreRecovery()
    {
        var callCount = 0;
        var capturedScripts = new List<string>();

        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) =>
            {
                callCount++;
                capturedScripts.Add(script);
            })
            .ReturnsAsync(SuccessResult(RestoreCheckpointJson(cpName: "recovery-test")));

        _mockSessionStore
            .Setup(s => s.EvictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _manager.RestoreCheckpointAsync(LocalHostId, TestVmId, "recovery-test");

        // Two PowerShell calls: restore script + post-restore recovery script
        callCount.Should().Be(2,
            "RestoreCheckpointAsync should make 2 PowerShell calls: restore + post-restore recovery");

        // Verify the second script contains post-restore recovery commands
        capturedScripts[1].Should().Contain("w32tm",
            "post-restore recovery script should include clock resync via w32tm");
        capturedScripts[1].Should().Contain("Running",
            "post-restore recovery script should poll for Running state");
    }

    // --- Issue 5: Duplicate Checkpoint Name Tests --------------------

    /// <summary>
    /// DeleteCheckpointAsync should throw CheckpointFailedException when the PowerShell
    /// script reports multiple checkpoints with the same name.
    /// Issue 5 fix: duplicate name detection for delete operations.
    /// </summary>
    [Fact]
    public async Task DeleteCheckpoint_ThrowsOnDuplicateNames()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Multiple checkpoints named 'dup-cp' exist for VM 'test-vm'. Use vm_checkpoint list to find the specific checkpoint."));

        Func<Task> act = async () =>
            await _manager.DeleteCheckpointAsync(LocalHostId, TestVmId, "dup-cp");

        var ex = await act.Should().ThrowAsync<CheckpointFailedException>();
        ex.Which.Message.Should().Contain("Multiple checkpoints named",
            "the error message should mention duplicate checkpoint names");
    }

    /// <summary>
    /// RestoreCheckpointAsync should throw CheckpointFailedException when the PowerShell
    /// script reports multiple checkpoints with the same name.
    /// Issue 5 fix: duplicate name detection for restore operations.
    /// </summary>
    [Fact]
    public async Task RestoreCheckpoint_ThrowsOnDuplicateNames()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Multiple checkpoints named 'dup-cp' exist for VM 'test-vm'. Use vm_checkpoint list to find the specific checkpoint."));

        _mockSessionStore
            .Setup(s => s.EvictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Func<Task> act = async () =>
            await _manager.RestoreCheckpointAsync(LocalHostId, TestVmId, "dup-cp");

        var ex = await act.Should().ThrowAsync<CheckpointFailedException>();
        ex.Which.Message.Should().Contain("Multiple checkpoints named",
            "the error message should mention duplicate checkpoint names");
    }

    // --- Issue #51 / CP-D6: MergeAllAsync tests --------------------------

    /// <summary>
    /// MergeAllAsync on a linear chain returns Success=true with the merged count
    /// parsed from the script's JSON output.
    /// </summary>
    [Fact]
    public async Task MergeAll_LinearChain_ReturnsSuccessWithCount()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("{\"MergedCount\":3}"));

        var result = await _manager.MergeAllAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.MergedCount.Should().Be(3);
        result.FailureReason.Should().BeNull();
    }

    /// <summary>
    /// MergeAllAsync on a VM with no checkpoints returns Success=true with MergedCount=0
    /// (the empty-tree success path).
    /// </summary>
    [Fact]
    public async Task MergeAll_NoCheckpoints_ReturnsZeroCount()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("{\"MergedCount\":0}"));

        var result = await _manager.MergeAllAsync(LocalHostId, TestVmId);

        result.Success.Should().BeTrue();
        result.MergedCount.Should().Be(0);
    }

    /// <summary>
    /// MergeAllAsync rejects a branched checkpoint tree with MergeNotSupportedException
    /// (→ MERGE_NOT_SUPPORTED). The script emits the sentinel "MERGE_NOT_SUPPORTED:BRANCHED"
    /// on stdout which the C# code recognises.
    /// </summary>
    [Fact]
    public async Task MergeAll_BranchedTree_ThrowsMergeNotSupported()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("MERGE_NOT_SUPPORTED:BRANCHED"));

        Func<Task> act = async () => await _manager.MergeAllAsync(LocalHostId, TestVmId);

        var ex = await act.Should().ThrowAsync<MergeNotSupportedException>();
        ex.Which.HostId.Should().Be(LocalHostId);
        ex.Which.VmId.Should().Be(TestVmId);
        ex.Which.Message.Should().Contain("linear chain");
    }

    /// <summary>
    /// MergeAllAsync surfaces underlying Hyper-V merge-job failures (non-zero exit /
    /// stderr) as CheckpointMergeFailedException → CHECKPOINT_MERGE_FAILED.
    /// </summary>
    [Fact]
    public async Task MergeAll_RuntimeFailure_ThrowsCheckpointMergeFailed()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Remove-VMSnapshot : The VHDX is currently locked by another process."));

        Func<Task> act = async () => await _manager.MergeAllAsync(LocalHostId, TestVmId);

        var ex = await act.Should().ThrowAsync<CheckpointMergeFailedException>();
        ex.Which.HostId.Should().Be(LocalHostId);
        ex.Which.VmId.Should().Be(TestVmId);
        ex.Which.Message.Should().Contain("Remove-VMSnapshot");
    }

    /// <summary>
    /// MergeAllAsync passes the VM ID into the composed script and uses
    /// the WMI-workaround '-ComputerName localhost' (LF-D7).
    /// </summary>
    [Fact]
    public async Task MergeAll_Composes_Script_With_VmId_And_Localhost()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("{\"MergedCount\":0}"));

        await _manager.MergeAllAsync(LocalHostId, TestVmId);

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain(TestVmId);
        capturedScript.Should().Contain("-ComputerName localhost", "LF-D7 WMI workaround");
        capturedScript.Should().Contain("Remove-VMSnapshot", "linear merge uses Remove-VMSnapshot");
        capturedScript.Should().Contain("Sort-Object CreationTime",
            "oldest-first ordering per CP-D6");
    }
}
