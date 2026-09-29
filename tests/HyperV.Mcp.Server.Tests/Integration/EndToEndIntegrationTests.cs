using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>Covers dispatch gaps beyond EndToEndPipelineTests: argument extraction, concurrency, service calls and response envelopes. Uses
/// real dispatcher, error mapper, host resolver and concurrency gate with mocked services; no Hyper-V infrastructure.</summary>
[Trait("Category", "Integration")]
public class EndToEndIntegrationTests : IDisposable
{
    /// <summary>Handlers validate VM IDs as GUIDs.</summary>
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

        // A Running VM satisfies command, script and file-transfer state preconditions.
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


    private static JsonDocument ParseResponse(string json)
    {
        return JsonDocument.Parse(json);
    }

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

    private static void AssertErrorResponse(string json, string expectedErrorCode)
    {
        using var doc = ParseResponse(json);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse(
            $"Expected error response but got success: {json}");
        root.GetProperty("errorCode").GetString().Should().Be(expectedErrorCode);
        root.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }


    [Fact]
    public async Task VmListImages_FullPipeline_ReturnsImagesWithCount()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_list_images",
            new Dictionary<string, object?>());

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


    [Fact]
    public async Task VmStop_ForceTrue_PassesForceFlagToService()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["force"] = true,
            });

        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("data").GetProperty("state").GetString().Should().Be("Off");

        _mockHyperVManager.Verify(
            m => m.StopVmAsync("local", VmGuid1, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "Should have called StopVmAsync with force=true");
    }


    /// <summary>Non-zero exits return COMMAND_FAILED but must retain the full CommandResult in data.</summary>
    [Fact]
    public async Task VmRunCommand_NonZeroExitCode_ReturnsCommandFailed()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["command"] = "cat /nonexistent",
                ["shell"] = "cmd",
                ["timeoutSeconds"] = 30,
            });

        AssertErrorResponse(result, ErrorCodes.CommandFailed);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("exitCode").GetInt32().Should().Be(1);
        data.GetProperty("stdout").GetString().Should().Be("partial output");
        data.GetProperty("stderr").GetString().Should().Be("error: file not found");
        data.GetProperty("durationMs").GetInt64().Should().Be(200);
    }


    /// <summary>Tests inline timeout handling in HandleRunCommandAsync, not the exception path through ErrorMapper.</summary>
    [Fact]
    public async Task VmRunCommand_TimedOut_ReturnsCommandTimeout()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["command"] = "long-running-cmd",
                ["shell"] = "cmd",
                ["timeoutSeconds"] = 30,
            });

        AssertErrorResponse(result, ErrorCodes.CommandTimeout);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("stdout").GetString().Should().Be("partial stdout before timeout");
        data.GetProperty("timedOut").GetBoolean().Should().BeTrue();
        data.GetProperty("durationMs").GetInt64().Should().Be(30000);
    }


    /// <summary>Cancelled commands use COMMAND_FAILED, not COMMAND_TIMEOUT.</summary>
    [Fact]
    public async Task VmRunCommand_Cancelled_ReturnsCommandFailed()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["command"] = "cancelled-cmd",
                ["shell"] = "cmd",
                ["timeoutSeconds"] = 30,
            });

        AssertErrorResponse(result, ErrorCodes.CommandFailed);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("cancelled").GetBoolean().Should().BeTrue();
        data.GetProperty("stdout").GetString().Should().Be("output before cancel");
    }


    [Fact]
    public async Task InputValidation_InvalidVmIdFormat_ReturnsInvalidParameter()
    {
        var result = await _dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?> { ["vmId"] = "not-a-guid" });

        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }


    [Fact]
    public async Task InputValidation_VmNamePathTraversal_ReturnsInvalidParameter()
    {
        var result = await _dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?> { ["name"] = "../../evil" });

        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }


    [Fact]
    public async Task InputValidation_VmNameWithNullByte_ReturnsInvalidParameter()
    {
        var result = await _dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?> { ["name"] = "vm\x00test" });

        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }


    /// <summary>Readiness dispatch must carry this call's credentials through the full pipeline.</summary>
    [Fact]
    public async Task P1Tool_VmWaitReady_ReturnsSuccess()
    {
        _mockHyperVManager
            .Setup(m => m.WaitForReadyAsync("local", VmGuid1, It.IsAny<ReadinessBudget>(),
                "readiness-user", "readiness-pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = VmGuid1, Name = "vm-1", State = "Running", HostId = "local" });

        var result = await _dispatcher.DispatchAsync("vm_wait_ready",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["timeoutSeconds"] = 300,
                ["username"] = "readiness-user",
                ["password"] = "readiness-pass",
            });

        AssertSuccessResponse(result);
    }

    /// <summary>A heartbeat cannot establish guest login when no credentials can be resolved.</summary>
    [Fact]
    public async Task P1Tool_VmWaitReady_WithoutCredentials_RefusesInsteadOfClaimingReadiness()
    {
        using var _ = new ClearedGuestCredentialEnvironment();

        var result = await _dispatcher.DispatchAsync("vm_wait_ready",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["timeoutSeconds"] = 300,
            });

        AssertErrorResponse(result, ErrorCodes.MissingCredentials);
    }


    [Fact]
    public async Task P1Tool_VmGetFile_ReturnsSuccess()
    {
        var result = await _dispatcher.DispatchAsync("vm_get_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["sourcePath"] = @"C:\guest\file.txt",
                ["destPath"] = @"C:\host\file.txt",
            });

        AssertSuccessResponse(result);
    }


    [Fact]
    public async Task VmRunScript_FullPipeline_ReturnsCommandResult()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["script"] = "Get-Process | Select-Object -First 5",
                ["shell"] = "powershell",
                ["timeoutSeconds"] = 60,
            });

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

    [Fact]
    public async Task VmRunScript_FullPipeline_NonZeroExitCode_ReturnsCommandFailed()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["script"] = "Get-RestrictedData",
                ["shell"] = "powershell",
                ["timeoutSeconds"] = 60,
            });

        AssertErrorResponse(result, ErrorCodes.CommandFailed);
        using var doc = ParseResponse(result);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("exitCode").GetInt32().Should().Be(1);
        data.GetProperty("stdout").GetString().Should().Be("partial script output");
        data.GetProperty("stderr").GetString().Should().Be("error: access denied");
    }


    [Fact]
    public async Task VmRestart_FullPipeline_ReturnsVmInfo()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_restart",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
            });

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

    [Fact]
    public async Task VmRestart_FullPipeline_VmNotFound_ReturnsError()
    {
        _mockHyperVManager
            .Setup(m => m.RestartVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", VmGuid1));

        var result = await _dispatcher.DispatchAsync("vm_restart",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
            });

        AssertErrorResponse(result, ErrorCodes.VmNotFound);
    }


    [Fact]
    public async Task VmCheckpoint_Create_FullPipeline_ReturnsCheckpointResult()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["action"] = "create",
                ["name"] = "test-checkpoint",
            });

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

    [Fact]
    public async Task VmCheckpoint_List_FullPipeline_ReturnsCheckpointList()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["action"] = "list",
            });

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

    [Fact]
    public async Task VmCheckpoint_InvalidAction_FullPipeline_ReturnsError()
    {
        var result = await _dispatcher.DispatchAsync("vm_checkpoint",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
                ["action"] = "invalid",
            });

        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
    }


    [Fact]
    public async Task VmCleanupOrphans_DryRun_FullPipeline_ReturnsOrphanList()
    {
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

        var result = await _dispatcher.DispatchAsync("vm_cleanup_orphans",
            new Dictionary<string, object?>
            {
                ["dryRun"] = true,
            });

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

    [Fact]
    public async Task VmCleanupOrphans_Execute_FullPipeline_ReturnsDestroyedList()
    {
        // Non-dry-run destruction requires an orphan-candidate row, matching the PowerShell pipeline. needs-attention rows are report-only
        // and yield action="detected".
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
                Reason = "orphan-candidate",
            },
        };

        _mockHyperVManager
            .Setup(m => m.CleanupOrphansAsync("local", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(destroyed);

        var result = await _dispatcher.DispatchAsync("vm_cleanup_orphans",
            new Dictionary<string, object?>
            {
                ["dryRun"] = false,
            });

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


    /// <summary>The real pause handler wraps the mock manager's default null result as success.</summary>
    [Fact]
    public async Task VmPause_DispatchesToHandler()
    {
        var expected = new VmInfo { VmId = VmGuid1, Name = "test-vm", State = "Paused", HostId = "local" };
        _mockHyperVManager.Setup(m => m.PauseVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _dispatcher.DispatchAsync("vm_pause",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });

        var root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        _mockHyperVManager.Verify(
            m => m.PauseVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task VmResume_DispatchesToHandler()
    {
        var expected = new VmInfo { VmId = VmGuid1, Name = "test-vm", State = "Running", HostId = "local" };
        _mockHyperVManager.Setup(m => m.ResumeVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _dispatcher.DispatchAsync("vm_resume",
            new Dictionary<string, object?> { ["vmId"] = VmGuid1 });

        var root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        _mockHyperVManager.Verify(
            m => m.ResumeVmAsync("local", VmGuid1, It.IsAny<CancellationToken>()),
            Times.Once);
    }


    [Fact]
    public async Task VmConfigure_MissingBothSettings_ReturnsInvalidParameter()
    {
        // A happy-path mock detects unexpected delegation.
        _mockHyperVManager
            .Setup(m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = VmGuid1, Name = "x", State = "Off", HostId = "local" });

        var result = await _dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = VmGuid1,
            });

        AssertErrorResponse(result, ErrorCodes.InvalidParameter);
        _mockHyperVManager.Verify(
            m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ConfigureVmAsync must NOT be called when both optional settings are null");
    }


    /// <summary>Use the list response's VM ID for status dispatch to validate serialization as part of the chain. Concurrency release is
    /// covered separately by EndToEndPipelineTests.ConcurrencyLimit_MaxOne_SecondOperationWaitsForFirst().</summary>
    [Fact]
    public async Task MultiToolSequence_ListThenStatus_BothSucceed()
    {
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

        var listResult = await _dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?>());

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

        var statusResult = await _dispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?> { ["vmId"] = extractedVmId });

        AssertSuccessResponse(statusResult);
        using (var doc = ParseResponse(statusResult))
        {
            var data = doc.RootElement.GetProperty("data");
            data.GetProperty("vmId").GetString().Should().Be(extractedVmId);
            data.GetProperty("name").GetString().Should().Be("vm-alpha");
            data.GetProperty("uptimeSeconds").GetInt64().Should().Be(3600);
        }

        _mockHyperVManager.Verify(
            m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()), Times.Once);
        _mockHyperVManager.Verify(
            m => m.GetVmStatusAsync("local", extractedVmId, It.IsAny<CancellationToken>()), Times.Once);
    }


    /// <summary>Covers explicit empty input; VmEcho_MissingMessageKey covers a missing key.</summary>
    [Fact]
    public async Task VmEcho_EmptyMessage_ReturnsSuccessWithEmptyMessage()
    {
        var result = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = "" });

        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("data").GetProperty("message").GetString()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task VmEcho_MissingMessageKey_ReturnsSuccessWithEmptyMessage()
    {
        var result = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?>());

        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("data").GetProperty("message").GetString()
            .Should().BeEmpty();
    }


    [Fact]
    public async Task VmEcho_SpecialCharacters_ReturnsCorrectMessage()
    {
        const string specialMessage = "{\"key\": \"value\"}";

        var result = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = specialMessage });

        AssertSuccessResponse(result);
        using var doc = ParseResponse(result);
        doc.RootElement.GetProperty("data").GetProperty("message").GetString()
            .Should().Be(specialMessage);
    }
}
