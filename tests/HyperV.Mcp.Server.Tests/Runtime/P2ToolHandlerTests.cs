using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for P2 tool handlers (vm_pause, vm_resume) in ToolDispatcher.
/// Each test exercises the full dispatch pipeline: ToolDispatcher.DispatchAsync →
/// argument extraction → concurrency gate → service delegation → response wrapping.
/// See /myplans/execution-plan.md — Stage 3.1: Pause and Resume.
/// </summary>
[Trait("Category", "Runtime")]
public class P2ToolHandlerTests
{
    private const string TestVmGuid = "12345678-1234-1234-1234-123456789abc";

    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
    private readonly Mock<ICheckpointManager> _checkpointManager = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IConcurrencyGate> _gate = new();

    private ToolDispatcher CreateDispatcher(ServerOptions? options = null)
    {
        var serverOptions = options ?? new ServerOptions();

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
            _checkpointManager.Object,
            _hostResolver.Object,
            new ErrorMapper(),
            _gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            serverOptions);
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_pause Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VmPause_Dispatches_To_HyperVManager_PauseVmAsync()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Paused", HostId = "local" };
        _hvManager.Setup(m => m.PauseVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_pause",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.PauseVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "vm_pause must delegate to IHyperVManager.PauseVmAsync");
    }

    [Fact]
    public async Task VmPause_Default_HostId_Is_Local()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Paused", HostId = "local" };
        _hvManager.Setup(m => m.PauseVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_pause",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid
                // hostId omitted — should default to "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.PauseVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "hostId must default to 'local' when not provided");
    }

    [Fact]
    public async Task VmPause_VmNotFound_Returns_Error()
    {
        var nonExistentGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _hvManager.Setup(m => m.PauseVmAsync("local", nonExistentGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", nonExistentGuid));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_pause",
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
            "VmNotFoundException must map to VM_NOT_FOUND");
    }

    [Fact]
    public async Task VmPause_WrongState_Returns_Error()
    {
        _hvManager.Setup(m => m.PauseVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cannot pause VM in state 'Off'. VM must be Running to pause."));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_pause",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task VmPause_Acquires_Global_Host_VM_Locks()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Paused", HostId = "local" };
        _hvManager.Setup(m => m.PauseVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchAsync("vm_pause",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_pause must acquire global slot");
        _gate.Verify(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_pause must acquire per-host lock (lifecycle operation)");
        _gate.Verify(g => g.AcquireVmLockAsync("local", TestVmGuid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_pause must acquire per-VM lock");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_resume Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VmResume_Dispatches_To_HyperVManager_ResumeVmAsync()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.ResumeVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_resume",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.ResumeVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "vm_resume must delegate to IHyperVManager.ResumeVmAsync");
    }

    [Fact]
    public async Task VmResume_Default_HostId_Is_Local()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.ResumeVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_resume",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid
                // hostId omitted — should default to "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.ResumeVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()), Times.Once,
            "hostId must default to 'local' when not provided");
    }

    [Fact]
    public async Task VmResume_VmNotFound_Returns_Error()
    {
        var nonExistentGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _hvManager.Setup(m => m.ResumeVmAsync("local", nonExistentGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", nonExistentGuid));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_resume",
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
            "VmNotFoundException must map to VM_NOT_FOUND");
    }

    [Fact]
    public async Task VmResume_WrongState_Returns_Error()
    {
        _hvManager.Setup(m => m.ResumeVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cannot resume VM in state 'Running'. VM must be Paused to resume."));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_resume",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task VmResume_Acquires_Global_Host_VM_Locks()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.ResumeVmAsync("local", TestVmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchAsync("vm_resume",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
            },
            CancellationToken.None);

        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_resume must acquire global slot");
        _gate.Verify(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_resume must acquire per-host lock (lifecycle operation)");
        _gate.Verify(g => g.AcquireVmLockAsync("local", TestVmGuid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once,
            "vm_resume must acquire per-VM lock");
    }

    // ═══════════════════════════════════════════════════════════════════
    // vm_configure Tests — GitHub Issue #56
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_configure with neither cpuCount nor memoryMB must return INVALID_PARAMETER
    /// and the error message must contain the precondition text — validates the
    /// widened SafeArgumentMessage that now forwards ex.Message for ArgumentException
    /// with a known parameter name.
    /// See GitHub Issue #56.
    /// </summary>
    [Fact]
    public async Task VmConfigure_AllNullSettings_ReturnsInvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local"
                // neither cpuCount nor memoryMB
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "missing both optional configuration parameters must map to INVALID_PARAMETER");
        response.Error.Should().Contain("At least one of 'cpuCount' or 'memoryMB'",
            "the widened SafeArgumentMessage must surface the precondition text from " +
            "the thrown ArgumentException (Issue #56)");

        _hvManager.Verify(
            m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ConfigureVmAsync must NOT be called when the precondition fails");
    }

    /// <summary>
    /// vm_configure with only cpuCount must call ConfigureVmAsync with cpuCount=4 and memoryMB=null,
    /// verifying that the implementation forwards null for the omitted parameter (so it can
    /// invoke Set-VMProcessor only).
    /// See GitHub Issue #56.
    /// </summary>
    [Fact]
    public async Task VmConfigure_OnlyCpu_InvokesSetVMProcessorOnly()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Off", HostId = "local" };
        _hvManager.Setup(m => m.ConfigureVmAsync("local", TestVmGuid, 4, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["cpuCount"] = 4
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.ConfigureVmAsync("local", TestVmGuid, 4, null, It.IsAny<CancellationToken>()),
            Times.Once,
            "vm_configure must delegate to ConfigureVmAsync with cpuCount=4, memoryMB=null");
    }

    /// <summary>
    /// vm_configure with only memoryMB must call ConfigureVmAsync with cpuCount=null and memoryMB=2048,
    /// verifying symmetric behavior to the cpu-only case.
    /// See GitHub Issue #56.
    /// </summary>
    [Fact]
    public async Task VmConfigure_OnlyMemory_InvokesSetVMMemoryOnly()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "test-vm", State = "Off", HostId = "local" };
        _hvManager.Setup(m => m.ConfigureVmAsync("local", TestVmGuid, null, 2048L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["memoryMB"] = 2048
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();

        _hvManager.Verify(m => m.ConfigureVmAsync("local", TestVmGuid, null, 2048L, It.IsAny<CancellationToken>()),
            Times.Once,
            "vm_configure must delegate to ConfigureVmAsync with cpuCount=null, memoryMB=2048");
    }

    /// <summary>
    /// vm_configure happy path: both cpuCount and memoryMB provided, mock returns updated VmInfo,
    /// asserts Success=true and the returned data flows through the response envelope.
    /// See GitHub Issue #56.
    /// </summary>
    [Fact]
    public async Task VmConfigure_BothProvided_ReturnsUpdatedVmInfo()
    {
        var expected = new VmInfo { VmId = TestVmGuid, Name = "configured-vm", State = "Off", HostId = "local" };
        _hvManager.Setup(m => m.ConfigureVmAsync("local", TestVmGuid, 8, 16384L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["cpuCount"] = 8,
                ["memoryMB"] = 16384
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();
        response.ErrorCode.Should().BeNullOrEmpty();
        response.Data.Should().NotBeNull("happy-path response must include the updated VmInfo");

        _hvManager.Verify(m => m.ConfigureVmAsync("local", TestVmGuid, 8, 16384L, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// vm_configure missing the required vmId argument must return INVALID_PARAMETER,
    /// confirming the required-string contract before optional-arg handling runs.
    /// See GitHub Issue #56.
    /// </summary>
    [Fact]
    public async Task VmConfigure_MissingVmId_ReturnsInvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["cpuCount"] = 4
                // vmId omitted
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "missing required vmId must map to INVALID_PARAMETER");

        _hvManager.Verify(
            m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// vm_configure when the underlying manager throws VmNotFoundException must map
    /// to VM_NOT_FOUND (same code as other VM-not-found tests in this file).
    /// See GitHub Issue #56.
    /// </summary>
    [Fact]
    public async Task VmConfigure_VmNotFound_MapsToVmNotFound()
    {
        var nonExistentGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _hvManager.Setup(m => m.ConfigureVmAsync("local", nonExistentGuid, 4, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", nonExistentGuid));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = nonExistentGuid,
                ["hostId"] = "local",
                ["cpuCount"] = 4
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.VmNotFound,
            "VmNotFoundException from ConfigureVmAsync must map to VM_NOT_FOUND");
    }

    /// <summary>
    /// vm_configure with cpuCount=0 must return INVALID_PARAMETER up-front
    /// (Gate 3 loopback range validation, Issue #56).
    /// </summary>
    [Fact]
    public async Task VmConfigure_ZeroCpuCount_ReturnsInvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["cpuCount"] = 0
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "cpuCount=0 must be rejected up-front with INVALID_PARAMETER");
        response.Error.Should().Contain("'cpuCount' must be a positive integer.");

        _hvManager.Verify(
            m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ConfigureVmAsync must NOT be called when cpuCount range validation fails");
    }

    /// <summary>
    /// vm_configure with negative cpuCount must return INVALID_PARAMETER up-front
    /// (Gate 3 loopback range validation, Issue #56).
    /// </summary>
    [Fact]
    public async Task VmConfigure_NegativeCpuCount_ReturnsInvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["cpuCount"] = -2
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "negative cpuCount must be rejected up-front with INVALID_PARAMETER");
        response.Error.Should().Contain("'cpuCount' must be a positive integer.");

        _hvManager.Verify(
            m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ConfigureVmAsync must NOT be called when cpuCount range validation fails");
    }

    /// <summary>
    /// vm_configure with memoryMB=0 must return INVALID_PARAMETER up-front
    /// (Gate 3 loopback range validation, Issue #56).
    /// </summary>
    [Fact]
    public async Task VmConfigure_ZeroMemoryMB_ReturnsInvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["memoryMB"] = 0
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "memoryMB=0 must be rejected up-front with INVALID_PARAMETER");
        response.Error.Should().Contain("'memoryMB' must be a positive integer (MB).");

        _hvManager.Verify(
            m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ConfigureVmAsync must NOT be called when memoryMB range validation fails");
    }

    /// <summary>
    /// vm_configure with negative memoryMB must return INVALID_PARAMETER up-front
    /// (Gate 3 loopback range validation, Issue #56).
    /// </summary>
    [Fact]
    public async Task VmConfigure_NegativeMemoryMB_ReturnsInvalidParameter()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmGuid,
                ["hostId"] = "local",
                ["memoryMB"] = -1024
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "negative memoryMB must be rejected up-front with INVALID_PARAMETER");
        response.Error.Should().Contain("'memoryMB' must be a positive integer (MB).");

        _hvManager.Verify(
            m => m.ConfigureVmAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ConfigureVmAsync must NOT be called when memoryMB range validation fails");
    }
}
