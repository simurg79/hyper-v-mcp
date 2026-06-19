using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for P0 tool handlers in ToolDispatcher.
/// Each test exercises the full dispatch pipeline: ToolDispatcher.DispatchAsync →
/// argument extraction → concurrency gate → service delegation → response wrapping.
/// See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
///
/// All tests use mocked infrastructure services to verify:
/// - Correct argument extraction and forwarding to service methods
/// - Default values for optional arguments (hostId → "local", force → false, etc.)
/// - Concurrency gate acquisition (global slot + per-VM/host locks)
/// - Error mapping when services throw domain exceptions
/// - Missing required parameter validation
/// </summary>
[Trait("Category", "Runtime")]
public class P0ToolHandlerTests
{
    /// <summary>
    /// Canonical VM GUID used across all P0 tool tests.
    /// All handlers now call InputValidation.ValidateVmId() which requires a valid GUID.
    /// </summary>
    private const string TestVmGuid = "12345678-1234-1234-1234-123456789abc";

    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
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

        // Default: GetVmStatusAsync returns a Running VM (needed for vm_run_command, vm_copy_file state precondition)
        _hvManager.Setup(m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" });

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
            serverOptions);
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_create Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_create dispatches to IHyperVManager.CreateVmAsync with correct arguments.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — VM creation.
    /// </summary>
    [Fact]
    public async Task VmCreate_Dispatches_To_HyperVManager_CreateVmAsync()
    {
        var expected = new VmInfo { VmId = "new-vm", Name = "new-vm", State = "Running", HostId = "local", CpuCount = 4, MemoryMB = 8192 };
        _hvManager.Setup(m => m.CreateVmAsync("local", "new-vm", @"C:\base.vhdx", 4, 8192, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["name"] = "new-vm",
                ["hostId"] = "local",
                ["baseVhdxPath"] = @"C:\base.vhdx",
                ["cpuCount"] = 4,
                ["memoryMB"] = 8192
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.CreateVmAsync("local", "new-vm", @"C:\base.vhdx", 4, 8192, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()), Times.Once,
            "vm_create must delegate to IHyperVManager.CreateVmAsync with exact arguments");
    }

    /// <summary>
    /// vm_create defaults hostId to "local" when not provided.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: default hostId = "local".
    /// </summary>
    [Fact]
    public async Task VmCreate_Default_HostId_Is_Local()
    {
        var expected = new VmInfo { VmId = "new-vm", Name = "new-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.CreateVmAsync("local", "new-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["name"] = "new-vm"
                // hostId omitted — should default to "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.CreateVmAsync("local", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()), Times.Once,
            "hostId must default to 'local' when not provided (MCP-D3)");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_start Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_start dispatches correctly with vmId and hostId.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_start.
    /// </summary>
    [Fact]
    public async Task VmStart_Dispatches_Correctly()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.StartVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.StartVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "vm_start must delegate to IHyperVManager.StartVmAsync with correct vmId and hostId");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_stop Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_stop with force=true passes force flag to StopVmAsync.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_stop (graceful + force).
    /// </summary>
    [Fact]
    public async Task VmStop_With_Force_True_Passes_Force_Flag()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Off", HostId = "local" };
        _hvManager.Setup(m => m.StopVmAsync("local", TestVmGuid, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["force"] = true
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.StopVmAsync("local", TestVmGuid, true, It.IsAny<CancellationToken>()), Times.Once,
            "vm_stop with force=true must pass force flag to StopVmAsync");
    }

    /// <summary>
    /// vm_stop defaults force to false when not provided.
    /// </summary>
    [Fact]
    public async Task VmStop_Default_Force_Is_False()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Off", HostId = "local" };
        _hvManager.Setup(m => m.StopVmAsync("local", TestVmGuid, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_stop",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
                // force omitted — should default to false
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.StopVmAsync("local", TestVmGuid, false, It.IsAny<CancellationToken>()), Times.Once,
            "vm_stop must default force to false when not provided");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_destroy Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_destroy dispatches correctly and returns destroy confirmation.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_destroy.
    /// </summary>
    [Fact]
    public async Task VmDestroy_Dispatches_Correctly()
    {
        _hvManager.Setup(m => m.DestroyVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_destroy",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.DestroyVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "vm_destroy must delegate to IHyperVManager.DestroyVmAsync");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_list Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_list with nameFilter passes the filter to ListVmsAsync.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_list.
    /// </summary>
    [Fact]
    public async Task VmList_With_Filter_Passes_NameFilter()
    {
        var vms = new List<VmInfo>
        {
            new() { VmId = "vm-1", Name = "test-vm-1", State = "Running", HostId = "local" }
        };
        _hvManager.Setup(m => m.ListVmsAsync("local", "test-*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(vms.AsReadOnly());

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["nameFilter"] = "test-*"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.ListVmsAsync("local", "test-*", It.IsAny<CancellationToken>()), Times.Once,
            "vm_list must pass nameFilter to ListVmsAsync");
    }

    /// <summary>
    /// vm_list without nameFilter passes null filter to ListVmsAsync.
    /// </summary>
    [Fact]
    public async Task VmList_Without_Filter_Passes_Null()
    {
        var vms = new List<VmInfo>
        {
            new() { VmId = "vm-1", Name = "vm-1", State = "Running", HostId = "local" },
            new() { VmId = "vm-2", Name = "vm-2", State = "Off", HostId = "local" }
        };
        _hvManager.Setup(m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(vms.AsReadOnly());

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local"
                // nameFilter omitted — should default to null
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()), Times.Once,
            "vm_list without nameFilter must pass null to ListVmsAsync");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_status Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_status dispatches correctly with vmId lookup.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_status.
    /// </summary>
    [Fact]
    public async Task VmStatus_Dispatches_Correctly()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local", CpuCount = 4, MemoryMB = 8192, UptimeSeconds = 3600 };
        _hvManager.Setup(m => m.GetVmStatusAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_status",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.GetVmStatusAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "vm_status must delegate to IHyperVManager.GetVmStatusAsync");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_run_command Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_run_command with all parameters passes them correctly.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1.
    /// </summary>
    [Fact]
    public async Task VmRunCommand_With_All_Parameters()
    {
        var expected = new CommandResult { ExitCode = 0, Stdout = "output", Stderr = "", TimedOut = false, DurationMs = 500 };
        _commandExecutor.Setup(e => e.ExecuteCommandAsync("remote-host", TestVmGuid, "dir C:\\", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["command"] = "dir C:\\",
                ["hostId"] = "remote-host",
                ["shell"] = "powershell",
                ["timeoutSeconds"] = 60
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _commandExecutor.Verify(e => e.ExecuteCommandAsync("remote-host", TestVmGuid, "dir C:\\", "powershell", 60, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_run_command must forward all parameters to ExecuteCommandAsync");
    }

    /// <summary>
    /// vm_run_command defaults shell to "cmd" when not provided.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1: default shell.
    /// </summary>
    [Fact]
    public async Task VmRunCommand_Default_Shell_Is_Cmd()
    {
        var expected = new CommandResult { ExitCode = 0, Stdout = "output", Stderr = "", TimedOut = false, DurationMs = 100 };
        _commandExecutor.Setup(e => e.ExecuteCommandAsync("local", TestVmGuid, "hostname", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["command"] = "hostname"
                // shell and timeoutSeconds omitted — should default to "cmd" and 30
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _commandExecutor.Verify(e => e.ExecuteCommandAsync("local", TestVmGuid, "hostname", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_run_command must default shell to 'cmd' and timeoutSeconds to 30");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_copy_file Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_copy_file dispatches correctly with paths and directory flag.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D1.
    /// </summary>
    [Fact]
    public async Task VmCopyFile_Dispatches_Correctly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var expected = new FileTransferResult
            {
                BytesTransferred = 2048,
                SourcePath = tempFile,
                DestPath = @"C:\guest\file.txt",
                IsDirectory = false,
                FileCount = 1,
                Verified = true
            };
            _fileTransfer.Setup(f => f.CopyToGuestAsync("local", TestVmGuid, tempFile, @"C:\guest\file.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expected);

            var dispatcher = CreateDispatcher();
            var resultJson = await dispatcher.DispatchAsync("vm_copy_file",
                new Dictionary<string, object?>
                {
                    ["vmId"] = TestVmGuid,
                    ["sourcePath"] = tempFile,
                    ["destPath"] = @"C:\guest\file.txt",
                    ["hostId"] = "local",
                    ["isDirectory"] = false
                },
                CancellationToken.None);

            var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
            response!.Success.Should().BeTrue();

            _fileTransfer.Verify(f => f.CopyToGuestAsync("local", TestVmGuid, tempFile, @"C:\guest\file.txt", false, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once,
                "vm_copy_file must delegate to IFileTransferService.CopyToGuestAsync with correct arguments");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_echo Regression Test
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_echo still works after ToolDispatcher refactoring.
    /// Regression test to ensure the simplest tool is not broken by DI changes.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog: vm_echo.
    /// </summary>
    [Fact]
    public async Task VmEcho_Still_Works_After_Refactoring()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = "regression-test" },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_echo must still work after ToolDispatcher DI refactoring");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Missing Required Parameter Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_create without required 'name' parameter throws ArgumentException,
    /// which is mapped to INVALID_PARAMETER error response.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task VmCreate_Missing_Name_Returns_InvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local"
                // 'name' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse(
            "missing required parameter must produce error");
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "ArgumentException for missing 'name' must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_start without required 'vmId' parameter throws ArgumentException,
    /// which is mapped to INVALID_PARAMETER error response.
    /// </summary>
    [Fact]
    public async Task VmStart_Missing_VmId_Returns_InvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local"
                // 'vmId' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "ArgumentException for missing 'vmId' must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_run_command without required 'command' parameter throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task VmRunCommand_Missing_Command_Returns_InvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid
                // 'command' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "ArgumentException for missing 'command' must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_copy_file without required 'sourcePath' parameter throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task VmCopyFile_Missing_SourcePath_Returns_InvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_copy_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["destPath"] = @"C:\guest\file.txt"
                // 'sourcePath' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "ArgumentException for missing 'sourcePath' must map to INVALID_PARAMETER");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Concurrency Gate Verification Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_start acquires global slot and per-VM lock.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification.
    /// </summary>
    [Fact]
    public async Task VmStart_Acquires_Global_Slot_And_Vm_Lock()
    {
        _hvManager.Setup(m => m.StartVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmGuid, State = "Running" });

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_start must acquire global slot");
        _gate.Verify(g => g.AcquireVmLockAsync("local", TestVmGuid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_start must acquire per-VM lock");
    }

    /// <summary>
    /// vm_create acquires global slot and per-host lock (lifecycle operation).
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification.
    /// </summary>
    [Fact]
    public async Task VmCreate_Acquires_Global_Slot_And_Host_Lock()
    {
        _hvManager.Setup(m => m.CreateVmAsync("local", "test-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = "test-vm", State = "Running" });

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["hostId"] = "local"
            },
            CancellationToken.None);

        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_create must acquire global slot");
        _gate.Verify(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_create must acquire per-host lock");
    }

    /// <summary>
    /// vm_list acquires only global slot (read-only operation, no per-VM/host lock).
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification.
    /// </summary>
    [Fact]
    public async Task VmList_Acquires_Only_Global_Slot()
    {
        _hvManager.Setup(m => m.ListVmsAsync("local", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>().AsReadOnly());

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchAsync("vm_list",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local"
            },
            CancellationToken.None);

        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_list must acquire global slot");
        _gate.Verify(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never,
            "vm_list must NOT acquire per-host lock (read-only operation)");
        _gate.Verify(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never,
            "vm_list must NOT acquire per-VM lock (read-only operation)");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Error from Service Maps to Error Response Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// VmNotFoundException from IHyperVManager.StartVmAsync is mapped to VM_NOT_FOUND
    /// error response through the dispatcher pipeline.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_NOT_FOUND.
    /// </summary>
    [Fact]
    public async Task VmStart_VmNotFound_Returns_VmNotFound_Error()
    {
        var nonExistentGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _hvManager.Setup(m => m.StartVmAsync("local", nonExistentGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", nonExistentGuid));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_start",
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

    /// <summary>
    /// VmAlreadyExistsException from IHyperVManager.CreateVmAsync is mapped to
    /// VM_ALREADY_EXISTS error response.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_ALREADY_EXISTS.
    /// </summary>
    [Fact]
    public async Task VmCreate_VmAlreadyExists_Returns_VmAlreadyExists_Error()
    {
        _hvManager.Setup(m => m.CreateVmAsync("local", "existing-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmAlreadyExistsException("local", "existing-vm"));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["name"] = "existing-vm",
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("VM_ALREADY_EXISTS",
            "VmAlreadyExistsException from service must map to VM_ALREADY_EXISTS");
    }

    /// <summary>
    /// ConcurrencyLimitException from IConcurrencyGate is mapped to CONCURRENCY_LIMIT
    /// error response through the dispatcher pipeline.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D4, CC-D5.
    /// </summary>
    [Fact]
    public async Task VmStart_ConcurrencyLimit_Returns_ConcurrencyLimit_Error()
    {
        var dispatcher = CreateDispatcher();

        // Override the default gate setup AFTER CreateDispatcher() to avoid
        // CreateDispatcher's default setup overwriting this ThrowsAsync.
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyLimitException("Global concurrency limit reached"));

        var resultJson = await dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("CONCURRENCY_LIMIT",
            "ConcurrencyLimitException must map to CONCURRENCY_LIMIT through dispatcher");
    }

    // ═══════════════════════════════════════════════════════════════════
    // GUID Normalization Regression Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression test: ToolDispatcher normalizes vmId BEFORE acquiring locks.
    /// An uppercase GUID must be normalized to lowercase canonical form so that
    /// the concurrency gate receives the same key regardless of GUID casing.
    /// This prevents two callers addressing the same VM with different casing
    /// from acquiring different locks and bypassing concurrency protection.
    /// </summary>
    [Fact]
    public async Task VmStart_UppercaseGuid_Normalizes_Before_Lock_Acquisition()
    {
        var uppercaseGuid = "12345678-1234-1234-1234-123456789ABC";
        var expectedNormalized = "12345678-1234-1234-1234-123456789abc";

        _hvManager.Setup(m => m.StartVmAsync("local", expectedNormalized, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = expectedNormalized, State = "Running" });

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?>
            {
                ["vmId"] = uppercaseGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue(
            "uppercase GUID should be normalized and accepted");

        // Verify the concurrency gate received the NORMALIZED (lowercase) vmId
        _gate.Verify(
            g => g.AcquireVmLockAsync("local", expectedNormalized, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "AcquireVmLockAsync must receive the normalized (lowercase) vmId, " +
            "not the raw uppercase input — otherwise different casing bypasses lock protection");

        // Verify the service also received the normalized vmId
        _hvManager.Verify(
            m => m.StartVmAsync("local", expectedNormalized, It.IsAny<CancellationToken>()),
            Times.Once,
            "StartVmAsync must receive the normalized vmId");
    }

    /// <summary>
    /// Regression test: non-GUID vmId is rejected with INVALID_PARAMETER at the dispatcher level.
    /// This ensures the validation happens before any lock acquisition or service delegation.
    /// </summary>
    [Fact]
    public async Task VmStart_NonGuidVmId_Returns_InvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?>
            {
                ["vmId"] = "not-a-guid",
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "non-GUID vmId must be rejected with INVALID_PARAMETER at the dispatcher level");

        // Verify NO lock was acquired (validation happens before locking)
        _gate.Verify(
            g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "No VM lock should be acquired when vmId validation fails");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_create autoStart Tests (Issue #24)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Issue #24: vm_create with autoStart=false passes false to CreateVmAsync.
    /// </summary>
    [Fact]
    public async Task VmCreate_AutoStartFalse_PassesFalseToCreateVmAsync()
    {
        var expected = new VmInfo { VmId = "new-vm", Name = "new-vm", State = "Off", HostId = "local" };
        _hvManager.Setup(m => m.CreateVmAsync("local", "new-vm", null, 2, 4096, false, It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["name"] = "new-vm",
                ["autoStart"] = false
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.CreateVmAsync("local", "new-vm", null, 2, 4096, false, It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()), Times.Once,
            "Issue #24: vm_create with autoStart=false must pass false to CreateVmAsync");
    }

    /// <summary>
    /// Issue #39: vm_create without autoStart defaults to false.
    /// </summary>
    [Fact]
    public async Task VmCreate_AutoStartOmitted_DefaultsToFalse()
    {
        var expected = new VmInfo { VmId = "new-vm", Name = "new-vm", State = "Off", HostId = "local" };
        _hvManager.Setup(m => m.CreateVmAsync("local", "new-vm", null, 2, 4096, false, It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["name"] = "new-vm"
                // autoStart omitted — should default to false
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.CreateVmAsync("local", "new-vm", null, 2, 4096, false, It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()), Times.Once,
            "Issue #39: vm_create without autoStart must default to false");
    }

    /// <summary>
    /// Issue #24: vm_create with malformed autoStart value returns INVALID_PARAMETER error.
    /// </summary>
    [Fact]
    public async Task VmCreate_AutoStartMalformed_ReturnsInvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["name"] = "new-vm",
                ["autoStart"] = "banana"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse("malformed autoStart should produce an error");
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "Issue #24: malformed boolean should map to INVALID_PARAMETER error code");
        response.Error.Should().Contain("autoStart",
            "the error should identify the malformed autoStart parameter");
    }
}
