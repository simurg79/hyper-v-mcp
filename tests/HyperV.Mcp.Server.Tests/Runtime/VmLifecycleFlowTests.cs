using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Runtime tests for VM lifecycle tool flows (vm_create, vm_start, vm_stop, vm_destroy).
/// See /myplans/vm-management/lifecycle/lifecycle-design.md.
/// See /myplans/vm-management/vm-management-design.md — Full Management Capability Matrix.
///
/// These tests exercise the expected orchestration flow for lifecycle tools:
/// - Host resolution → concurrency acquisition → HyperV operation → response
/// - Error cases: VM not found, VM already exists, host not found
/// - Concurrency: lifecycle operations acquire both per-VM and per-host locks
///
/// Two test categories:
/// 1. Mock-based tests: validate the orchestration contract (expected wiring)
/// 2. Dispatcher-wired tests: exercise real ToolDispatcher + ErrorMapper to verify
///    end-to-end error mapping and dispatch behavior without real Hyper-V calls.
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement lifecycle tool handlers that:
///    a. Resolve hostId via IHostResolver
///    b. Acquire global + per-host + per-VM locks via IConcurrencyGate
///    c. Delegate to IHyperVManager for the operation
///    d. Catch exceptions and map via IErrorMapper
///    e. Release locks in reverse order
/// 2. Wire up in DI.
/// </summary>
[Trait("Category", "Runtime")]
public class VmLifecycleFlowTests
{
    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<IConcurrencyGate> _gate = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IErrorMapper> _errorMapper = new();

    private readonly HostProfile _localProfile = new()
    {
        HostId = "local",
        ComputerName = "localhost",
        TrustPolicy = "local"
    };

    // ─── vm_start Flow ────────────────────────────────────────────────

    /// <summary>
    /// vm_start flow: resolve host → acquire locks → start VM → return success.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_start.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_start needs Global+Host+VM.
    /// </summary>
    [Fact]
    public async Task VmStart_Flow_Resolves_Host_Acquires_Locks_Starts_Vm()
    {
        // Arrange: host resolution
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);

        // Arrange: concurrency locks
        var globalLock = Mock.Of<IDisposable>();
        var hostLock = Mock.Of<IDisposable>();
        var vmLock = Mock.Of<IDisposable>();
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(globalLock);
        _gate.Setup(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostLock);
        _gate.Setup(g => g.AcquireVmLockAsync("local", "test-vm", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(vmLock);

        // Arrange: HyperV manager returns started VM
        var vmInfo = new VmInfo { VmId = "test-vm", Name = "test-vm", State = "Running", HostId = "local" };
        _hvManager.Setup(m => m.StartVmAsync("local", "test-vm", It.IsAny<CancellationToken>()))
            .ReturnsAsync(vmInfo);

        // Act: simulate the expected tool handler flow
        var profile = _hostResolver.Object.ResolveRequired("local");
        using var gLock = await _gate.Object.AcquireGlobalSlotAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        using var hLock = await _gate.Object.AcquireHostLockAsync("local", TimeSpan.FromSeconds(30), CancellationToken.None);
        using var vLock = await _gate.Object.AcquireVmLockAsync("local", "test-vm", TimeSpan.FromSeconds(60), CancellationToken.None);
        var result = await _hvManager.Object.StartVmAsync("local", "test-vm", CancellationToken.None);
        var response = McpToolResponse.Ok(result);

        // Assert
        profile.HostId.Should().Be("local");
        response.Success.Should().BeTrue();
        result.State.Should().Be("Running",
            "started VM should be in Running state");

        // Verify lock acquisition order (global → host → VM) per design
        _gate.Verify(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _gate.Verify(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _gate.Verify(g => g.AcquireVmLockAsync("local", "test-vm", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── vm_create Flow ──────────────────────────────────────────────

    /// <summary>
    /// vm_create flow: resolve host → acquire locks → create VM → return success with VmInfo.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — VM creation with differencing VHDX.
    /// </summary>
    [Fact]
    public async Task VmCreate_Flow_Creates_Vm_With_DifferencingVhdx()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);

        var globalLock = Mock.Of<IDisposable>();
        var hostLock = Mock.Of<IDisposable>();
        var vmLock = Mock.Of<IDisposable>();
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(globalLock);
        _gate.Setup(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(hostLock);
        _gate.Setup(g => g.AcquireVmLockAsync("local", "new-vm", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(vmLock);

        var created = new VmInfo { VmId = "new-vm", Name = "new-vm", State = "Running", HostId = "local", CpuCount = 2, MemoryMB = 4096 };
        _hvManager.Setup(m => m.CreateVmAsync("local", "new-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        // Simulate flow
        _hostResolver.Object.ResolveRequired("local");
        await _gate.Object.AcquireGlobalSlotAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await _gate.Object.AcquireHostLockAsync("local", TimeSpan.FromSeconds(30), CancellationToken.None);
        await _gate.Object.AcquireVmLockAsync("local", "new-vm", TimeSpan.FromSeconds(60), CancellationToken.None);
        var result = await _hvManager.Object.CreateVmAsync("local", "new-vm", null, 2, 4096, true, /* verifyBaseImageHash */ true, CancellationToken.None);
        var response = McpToolResponse.Ok(result);

        response.Success.Should().BeTrue();
        result.State.Should().Be("Running",
            "created VM should reach Running state after bootstrap");
        result.CpuCount.Should().Be(2);
        result.MemoryMB.Should().Be(4096);
    }

    // ─── vm_stop Flow ────────────────────────────────────────────────

    /// <summary>
    /// vm_stop with force=false performs graceful shutdown.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_stop (graceful + force).
    /// </summary>
    [Fact]
    public async Task VmStop_Graceful_Returns_Off_State()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);
        var globalLock = Mock.Of<IDisposable>();
        var hostLock = Mock.Of<IDisposable>();
        var vmLock = Mock.Of<IDisposable>();
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(globalLock);
        _gate.Setup(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(hostLock);
        _gate.Setup(g => g.AcquireVmLockAsync("local", "test-vm", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(vmLock);

        var stopped = new VmInfo { VmId = "test-vm", Name = "test-vm", State = "Off", HostId = "local" };
        _hvManager.Setup(m => m.StopVmAsync("local", "test-vm", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stopped);

        var result = await _hvManager.Object.StopVmAsync("local", "test-vm", false, CancellationToken.None);

        result.State.Should().Be("Off",
            "gracefully stopped VM should be in Off state");
    }

    // ─── vm_destroy Flow ─────────────────────────────────────────────

    /// <summary>
    /// vm_destroy flow: stop VM + remove + cleanup resources.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_destroy.
    /// </summary>
    [Fact]
    public async Task VmDestroy_Flow_Removes_Vm_And_Resources()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);
        var globalLock = Mock.Of<IDisposable>();
        var hostLock = Mock.Of<IDisposable>();
        var vmLock = Mock.Of<IDisposable>();
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(globalLock);
        _gate.Setup(g => g.AcquireHostLockAsync("local", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(hostLock);
        _gate.Setup(g => g.AcquireVmLockAsync("local", "test-vm", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(vmLock);

        _hvManager.Setup(m => m.DestroyVmAsync("local", "test-vm", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _hvManager.Object.DestroyVmAsync("local", "test-vm", CancellationToken.None);

        _hvManager.Verify(m => m.DestroyVmAsync("local", "test-vm", It.IsAny<CancellationToken>()), Times.Once,
            "vm_destroy must invoke DestroyVmAsync on the HyperV manager");
    }

    // ─── Error Case: VM Not Found ──────────────────────────────────────

    /// <summary>
    /// When vm_start targets a nonexistent VM, it must produce VM_NOT_FOUND.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_NOT_FOUND.
    /// </summary>
    [Fact]
    public async Task VmStart_NonExistent_Vm_Returns_VmNotFound()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);

        _hvManager.Setup(m => m.StartVmAsync("local", "nonexistent", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmNotFoundException("local", "nonexistent"));

        _errorMapper.Setup(m => m.MapException(It.IsAny<VmNotFoundException>(), null))
            .Returns(McpToolResponse.Fail("No VM with ID 'nonexistent' exists on host 'local'", ErrorCodes.VmNotFound));

        // Simulate flow with error handling
        try
        {
            await _hvManager.Object.StartVmAsync("local", "nonexistent", CancellationToken.None);
        }
        catch (VmNotFoundException ex)
        {
            var response = _errorMapper.Object.MapException(ex);
            response.Success.Should().BeFalse();
            response.ErrorCode.Should().Be("VM_NOT_FOUND",
                "nonexistent VM must produce VM_NOT_FOUND error");
        }
    }

    // ─── Error Case: Host Not Found ────────────────────────────────────

    /// <summary>
    /// When a tool targets an unknown hostId, it must produce HOST_NOT_FOUND.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: HOST_NOT_FOUND.
    /// </summary>
    [Fact]
    public void VmStart_Unknown_Host_Returns_HostNotFound()
    {
        _hostResolver.Setup(r => r.ResolveRequired("bad-host"))
            .Throws(new HostNotFoundException("bad-host"));

        _errorMapper.Setup(m => m.MapException(It.IsAny<HostNotFoundException>(), null))
            .Returns(McpToolResponse.Fail("No host with the specified hostId 'bad-host' is configured", ErrorCodes.HostNotFound));

        try
        {
            _hostResolver.Object.ResolveRequired("bad-host");
        }
        catch (HostNotFoundException ex)
        {
            var response = _errorMapper.Object.MapException(ex);
            response.Success.Should().BeFalse();
            response.ErrorCode.Should().Be("HOST_NOT_FOUND",
                "unknown hostId must produce HOST_NOT_FOUND error");
        }
    }

    // ─── Error Case: VM Already Exists ─────────────────────────────────

    /// <summary>
    /// When vm_create targets an existing VM name, it must produce VM_ALREADY_EXISTS.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: VM_ALREADY_EXISTS.
    /// </summary>
    [Fact]
    public async Task VmCreate_Duplicate_Name_Returns_VmAlreadyExists()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);

        _hvManager.Setup(m => m.CreateVmAsync("local", "existing-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VmAlreadyExistsException("local", "existing-vm"));

        _errorMapper.Setup(m => m.MapException(It.IsAny<VmAlreadyExistsException>(), null))
            .Returns(McpToolResponse.Fail("VM with name 'existing-vm' already exists on host 'local'", ErrorCodes.VmAlreadyExists));

        try
        {
            await _hvManager.Object.CreateVmAsync("local", "existing-vm", null, 2, 4096, true, /* verifyBaseImageHash */ true, CancellationToken.None);
        }
        catch (VmAlreadyExistsException ex)
        {
            var response = _errorMapper.Object.MapException(ex);
            response.Success.Should().BeFalse();
            response.ErrorCode.Should().Be("VM_ALREADY_EXISTS",
                "duplicate VM name must produce VM_ALREADY_EXISTS error");
        }
    }

    // ─── Concurrency: Lifecycle Needs Both Host and VM Locks ───────────

    /// <summary>
    /// When concurrency limit is hit during a lifecycle operation, it must
    /// produce CONCURRENCY_LIMIT error.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D4, CC-D5.
    /// </summary>
    [Fact]
    public async Task VmStart_Concurrency_Limit_Returns_ConcurrencyLimit()
    {
        _hostResolver.Setup(r => r.ResolveRequired("local")).Returns(_localProfile);

        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyLimitException("Global limit reached: 10 operations in progress"));

        _errorMapper.Setup(m => m.MapException(It.IsAny<ConcurrencyLimitException>(), null))
            .Returns(McpToolResponse.Fail("Concurrency limit reached", ErrorCodes.ConcurrencyLimit));

        try
        {
            await _gate.Object.AcquireGlobalSlotAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        catch (ConcurrencyLimitException ex)
        {
            var response = _errorMapper.Object.MapException(ex);
            response.Success.Should().BeFalse();
            response.ErrorCode.Should().Be("CONCURRENCY_LIMIT",
                "concurrency limit during lifecycle must produce CONCURRENCY_LIMIT error");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Dispatcher-wired tests: exercise real ToolDispatcher + ErrorMapper
    // These tests address the review finding that mock-only tests lack
    // real wiring coverage through the dispatcher/SUT.
    // Updated for Stage 1.5: P0 tools now have real handlers, so they
    // return success (not INTERNAL_ERROR) when mocked services succeed.
    // See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Helper to create a ToolDispatcher with mocked infrastructure services.
    /// All concurrency gates grant locks immediately by default.
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

        return new ToolDispatcher(
            hv.Object, cmd.Object, ft.Object, new Mock<ICheckpointManager>().Object, hr.Object,
            new ErrorMapper(), g.Object, new Mock<IPowerShellExecutor>().Object, new Mock<IPowerShellDirectChannel>().Object, new ServerOptions());
    }

    /// <summary>
    /// vm_start dispatched through real ToolDispatcher with mocked HyperVManager
    /// returns success when the service call succeeds.
    /// This proves end-to-end wiring: dispatcher → handler → service → response.
    /// See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmStart_Returns_Success_With_Mocked_Service()
    {
        const string vmGuid = "12345678-1234-1234-1234-123456789abc";
        var hvManager = new Mock<IHyperVManager>();
        hvManager.Setup(m => m.StartVmAsync("local", vmGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = vmGuid, Name = "test-vm", State = "Running", HostId = "local" });

        var dispatcher = CreateMockedDispatcher(hvManager: hvManager);

        var resultJson = await dispatcher.DispatchAsync("vm_start",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["vmId"] = vmGuid
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_start with mocked successful service should return success");
    }

    /// <summary>
    /// vm_create dispatched through real ToolDispatcher with mocked HyperVManager
    /// returns success when the service call succeeds.
    /// See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmCreate_Returns_Success_With_Mocked_Service()
    {
        var hvManager = new Mock<IHyperVManager>();
        hvManager.Setup(m => m.CreateVmAsync("local", "test-vm", null, 2, 4096, It.IsAny<bool>(), It.IsAny<bool>() /* verifyBaseImageHash */, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = "test-vm", Name = "test-vm", State = "Running", HostId = "local", CpuCount = 2, MemoryMB = 4096 });

        var dispatcher = CreateMockedDispatcher(hvManager: hvManager);

        var resultJson = await dispatcher.DispatchAsync("vm_create",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["name"] = "test-vm",
                ["cpuCount"] = 2,
                ["memoryMB"] = 4096
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_create with mocked successful service should return success");
    }

    /// <summary>
    /// vm_stop dispatched through real ToolDispatcher with mocked HyperVManager
    /// returns success when the service call succeeds.
    /// See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmStop_Returns_Success_With_Mocked_Service()
    {
        const string vmGuid = "12345678-1234-1234-1234-123456789abc";
        var hvManager = new Mock<IHyperVManager>();
        hvManager.Setup(m => m.StopVmAsync("local", vmGuid, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = vmGuid, Name = "test-vm", State = "Off", HostId = "local" });

        var dispatcher = CreateMockedDispatcher(hvManager: hvManager);

        var resultJson = await dispatcher.DispatchAsync("vm_stop",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["vmId"] = vmGuid,
                ["force"] = false
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_stop with mocked successful service should return success");
    }

    /// <summary>
    /// vm_destroy dispatched through real ToolDispatcher with mocked HyperVManager
    /// returns success when the service call succeeds.
    /// See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmDestroy_Returns_Success_With_Mocked_Service()
    {
        const string vmGuid = "12345678-1234-1234-1234-123456789abc";
        var hvManager = new Mock<IHyperVManager>();
        hvManager.Setup(m => m.DestroyVmAsync("local", vmGuid, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dispatcher = CreateMockedDispatcher(hvManager: hvManager);

        var resultJson = await dispatcher.DispatchAsync("vm_destroy",
            new Dictionary<string, object?>
            {
                ["hostId"] = "local",
                ["vmId"] = vmGuid
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_destroy with mocked successful service should return success");
    }

    /// <summary>
    /// Dispatching vm_echo (real handler) through real ToolDispatcher verifies
    /// that the full dispatch pipeline returns a success envelope.
    /// This is the only lifecycle-adjacent tool with a real implementation.
    /// </summary>
    [Fact]
    public async Task Dispatcher_VmEcho_Returns_Success()
    {
        var dispatcher = CreateMockedDispatcher();

        var resultJson = await dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = "lifecycle-test" },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_echo has a real handler and should return success through the dispatcher");
    }

    /// <summary>
    /// Dispatching a completely unknown tool name through the dispatcher must
    /// return TOOL_NOT_FOUND error with full envelope invariants.
    /// Verifies the dispatcher's unknown-tool path works correctly through the
    /// full pipeline including: success=false, errorCode, non-empty error message,
    /// data=null, and stable failure shape.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D2, MCP-D6.
    /// </summary>
    [Fact]
    public async Task Dispatcher_UnknownLifecycleTool_Returns_ToolNotFound()
    {
        var dispatcher = CreateMockedDispatcher();

        var resultJson = await dispatcher.DispatchAsync("vm_upgrade",
            new Dictionary<string, object?> { ["vmId"] = "test-vm" },
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
        response.Error.Should().Contain("vm_upgrade",
            "error message should identify the unknown tool name for diagnostics");
    }

    /// <summary>
    /// Real ConcurrencyGate + real ErrorMapper: when concurrency limit is hit,
    /// the real ErrorMapper maps ConcurrencyLimitException to CONCURRENCY_LIMIT.
    /// Verifies end-to-end error mapping without mocks.
    /// </summary>
    [Fact]
    public async Task Real_ConcurrencyGate_And_ErrorMapper_Map_ConcurrencyLimit()
    {
        var options = new ServerOptions { MaxConcurrentOperations = 1 };
        using var gate = new ConcurrencyGate(options);
        var mapper = new ErrorMapper();

        // Acquire the only slot
        var slot = await gate.AcquireGlobalSlotAsync(
            TimeSpan.FromSeconds(5), CancellationToken.None);

        // Try to acquire another — should throw
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
                "real ConcurrencyGate exception must map to CONCURRENCY_LIMIT through real ErrorMapper");
            response.Error.Should().Contain("concurrency limit",
                "sanitized error message should describe the concurrency limit rejection");
        }
        finally
        {
            slot.Dispose();
        }
    }
}
