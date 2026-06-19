using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Runtime tests for tool dispatch and registration behavior.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D1: Attribute-based tool registration.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
///
/// These tests exercise the REAL ToolDispatcher implementation against expected
/// runtime behavior using mocked infrastructure services.
///
/// Expected runtime flows:
/// - Known tool name routes to the correct handler and returns success
/// - Unknown tool name produces TOOL_NOT_FOUND error response (not an exception)
/// - Dispatcher reports all 19 catalog tools as registered
/// - Handler exceptions are wrapped into failure envelopes (MCP-D6)
/// </summary>
[Trait("Category", "Runtime")]
public class ToolDispatchTests
{
    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IErrorMapper> _errorMapper = new();
    private readonly Mock<IConcurrencyGate> _gate = new();
    private readonly ToolDispatcher _dispatcher;

    public ToolDispatchTests()
    {
        // Set up default concurrency gate behavior — always grant locks immediately.
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // Use real ErrorMapper for dispatch tests that exercise end-to-end error mapping.
        _errorMapper.Setup(m => m.MapException(It.IsAny<Exception>(), It.IsAny<string?>()))
            .Returns((Exception ex, string? state) => new ErrorMapper().MapException(ex, state));

        _dispatcher = new ToolDispatcher(
            _hvManager.Object,
            _commandExecutor.Object,
            _fileTransfer.Object,
            new Mock<ICheckpointManager>().Object,
            _hostResolver.Object,
            _errorMapper.Object,
            _gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            new ServerOptions());
    }

    // ─── Registration ──────────────────────────────────────────────────

    /// <summary>
    /// vm_echo (a known P0 tool) must be recognized by the dispatcher.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
    /// </summary>
    [Fact]
    public void Known_Tool_Is_Registered()
    {
        var result = _dispatcher.IsRegistered("vm_echo");

        result.Should().BeTrue(
            "vm_echo is in the tool catalog and must be registered " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog)");
    }

    /// <summary>
    /// An unknown tool name must NOT be registered.
    /// </summary>
    [Fact]
    public void Unknown_Tool_Is_Not_Registered()
    {
        var result = _dispatcher.IsRegistered("vm_nonexistent");

        result.Should().BeFalse(
            "tools not in the catalog must not be registered");
    }

    /// <summary>
    /// The dispatcher must report all 22 catalog tools as registered.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog
    /// (22 tools, including vm_diag, vm_os_install, and vm_create_base_image per Issue #51).
    /// </summary>
    [Fact]
    public void Dispatcher_Reports_All_22_Catalog_Tools()
    {
        var registered = _dispatcher.GetRegisteredTools();

        registered.Should().HaveCount(22,
            "all 22 catalog tools must be registered in the dispatcher " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog, plus vm_diag, vm_os_install, and vm_create_base_image (Issue #51))");

        foreach (var tool in ToolCatalog.AllTools)
        {
            registered.Should().Contain(tool.Name,
                $"tool '{tool.Name}' must be registered");
        }
    }

    // ─── Dispatch Behavior ────────────────────────────────────────────

    /// <summary>
    /// Dispatching an unknown tool must return a TOOL_NOT_FOUND error response
    /// (not throw an exception). MCP-D6: Exceptions caught and wrapped.
    /// </summary>
    [Fact]
    public async Task Dispatch_Unknown_Tool_Returns_ToolNotFound_Error()
    {
        var resultJson = await _dispatcher.DispatchAsync("vm_fake",
            new Dictionary<string, object?>(), CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("TOOL_NOT_FOUND",
            "unknown tools must produce TOOL_NOT_FOUND error response " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — MCP-D6)");
    }

    /// <summary>
    /// Dispatching vm_echo with a message must return a success response.
    /// This is the simplest tool — it echoes back the input message.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog: vm_echo.
    /// </summary>
    [Fact]
    public async Task Dispatch_Echo_Returns_Success_With_Message()
    {
        var resultJson = await _dispatcher.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = "hello" },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_echo with valid args should return success");
    }

    /// <summary>
    /// vm_diag MUST return the exact <c>diagVersion</c> literal that the
    /// out-of-band smoke gates pin against. Without this assertion, a future
    /// typo or missed cohort bump (e.g. v11 → v12) would silently regress
    /// because the rest of <c>vm_diag</c>'s payload only checks "section
    /// exists", not the cohort marker itself.
    ///
    /// Addresses PR #61 review comment (copilot-pull-request-reviewer,
    /// 2026-05-03) on <c>ToolDispatcher.cs</c>:
    /// "smoke-gate routing depends on this exact value, a future typo or
    /// missed bump would silently pass the current vm_diag tests".
    ///
    /// Bump procedure: when intentionally rolling the cohort, update both
    /// the literal in <c>HandleDiagAsync</c> and <see cref="ExpectedDiagVersion"/>
    /// below in the same commit.
    /// </summary>
    [Fact]
    public async Task Dispatch_VmDiag_Returns_Pinned_DiagVersion_Literal()
    {
        const string ExpectedDiagVersion = "v12";

        var resultJson = await _dispatcher.DispatchAsync("vm_diag",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            $"vm_diag must succeed in unit-test environment; raw response: {resultJson}");

        // Data is serialized as a JsonElement once round-tripped through
        // JsonSerializer.Deserialize<McpToolResponse>(). Read the diagVersion
        // property directly off the element to pin the literal.
        response.Data.Should().BeOfType<JsonElement>(
            "vm_diag returns a dictionary payload that round-trips as a JsonElement object");
        var data = (JsonElement)response.Data!;
        data.ValueKind.Should().Be(JsonValueKind.Object);
        data.TryGetProperty("diagVersion", out var diagVersion).Should().BeTrue(
            "vm_diag payload must contain a 'diagVersion' field");
        diagVersion.GetString().Should().Be(ExpectedDiagVersion,
            $"vm_diag.diagVersion is the cohort marker that out-of-band smoke gates " +
            $"pin against; DIAG-D7 / Issue #65 originally pinned v10, and " +
            $"VC-D7 / Issue #169 bumps to v11 to advertise the additive " +
            $"`baseImageHashCache` block. VC-D12..VC-D19 / Issue #170 bumps to v12 " +
            $"for sidecar cache telemetry fields (sidecarHits/sidecarWrites/" +
            $"sidecarDiscards/lastMutationDetected). If you intentionally bumped it, update " +
            $"ExpectedDiagVersion in this test in the same commit " +
            $"(current expected: {ExpectedDiagVersion}).");
    }

    /// <summary>
    /// PR-A / PA-Q3 (code-cleanup): the <c>vm_diag</c> success payload must NOT
    /// contain a <c>fileCaptureContent</c> key. That field was a dead stub
    /// removed in PR-A (low-risk cleanup) — historically it was declared as
    /// <c>string? fileCaptureContent = null;</c> and emitted as a permanently
    /// <c>null</c> value, and no consumer (including the smoke harness) ever
    /// read it. This negative assertion prevents anyone from re-introducing
    /// the stub without an accompanying design decision.
    ///
    /// Forward-protection: also asserts <c>psTests</c> IS still present.
    /// <c>psTests</c> is a real, populated section consumed by the smoke
    /// harness and must remain in the envelope. See
    /// <c>myplans/code-cleanup/pr-a-low-risk-cleanup/pr-a-low-risk-cleanup-design.md</c>
    /// (PA-Q3, Gate 6 v2 user pre-approval).
    /// </summary>
    [Fact]
    public async Task Dispatch_VmDiag_Does_Not_Expose_FileCaptureContent_But_Keeps_PsTests()
    {
        var resultJson = await _dispatcher.DispatchAsync("vm_diag",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            $"vm_diag must succeed in unit-test environment; raw response: {resultJson}");

        response.Data.Should().BeOfType<JsonElement>();
        var data = (JsonElement)response.Data!;
        data.ValueKind.Should().Be(JsonValueKind.Object);

        data.TryGetProperty("fileCaptureContent", out _).Should().BeFalse(
            "PR-A removed the dead `fileCaptureContent` stub from the vm_diag " +
            "envelope; re-introducing it would resurrect a known-unused field " +
            "(see pr-a-low-risk-cleanup-design.md — PA-Q3).");

        data.TryGetProperty("psTests", out var psTests).Should().BeTrue(
            "vm_diag MUST continue to expose the `psTests` section — it is " +
            "consumed by the smoke harness and was explicitly preserved by " +
            "PR-A (only `fileCaptureContent` was removed).");
        psTests.ValueKind.Should().Be(JsonValueKind.Array,
            "`psTests` is an array of PowerShell health-probe results.");
    }

    /// <summary>
    /// The dispatcher must propagate CancellationToken to handlers.
    /// See /myplans/execution/commands/commands-design.md — Timeout and Cancellation.
    /// </summary>
    [Fact]
    public async Task Dispatch_Respects_CancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Dispatching with an already-cancelled token should throw or
        // return a cancellation-aware response.
        Func<Task> act = async () => await _dispatcher.DispatchAsync("vm_run_command",
            new Dictionary<string, object?> { ["vmId"] = "test", ["command"] = "dir" },
            cts.Token);

        // Acceptable: either OperationCanceledException or a structured timeout response
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancelled dispatch must throw OperationCanceledException");
    }

    // ─── Issue #52 Phase 2 Gate 3 RC-1 — pre-flight routing ──────────

    /// <summary>
    /// RC-1: when the dispatcher is constructed with an <see cref="IPowerShellHost"/>,
    /// the VM-state pre-flight in <c>EnsureVmRunningAsync</c> MUST route through
    /// <see cref="IPowerShellHost.GetVmStateAsync"/>, NOT through
    /// <see cref="IHyperVManager.GetVmStatusAsync"/> (which would drag the legacy
    /// out-of-process <c>PowerShellExecutor</c> onto every guest tool call, violating
    /// the PSD-D6 single-facade rule).
    /// </summary>
    [Fact]
    public async Task RunCommand_WithPsHostInjected_PreFlightRoutesThroughPowerShellHost()
    {
        var hvManager = new Mock<IHyperVManager>(MockBehavior.Strict);
        var commandExec = new Mock<ICommandExecutor>();
        var fileTransfer = new Mock<IFileTransferService>();
        var hostResolver = new Mock<IHostResolver>();
        var gate = new Mock<IConcurrencyGate>();
        var psHost = new Mock<IPowerShellHost>();
        const string vmId = "12345678-1234-1234-1234-123456789abc";

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        psHost.Setup(h => h.GetVmStateAsync("local", vmId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Running");

        commandExec
            .Setup(e => e.ExecuteCommandAsync(
                "local", vmId, "echo test", "cmd", 30,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult { ExitCode = 0, Stdout = "test", Stderr = "", TimedOut = false, DurationMs = 1 });

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            commandExec.Object,
            fileTransfer.Object,
            new Mock<ICheckpointManager>().Object,
            hostResolver.Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            new ServerOptions(),
            psHost.Object);

        var json = await dispatcher.DispatchAsync(
            "vm_run_command",
            new Dictionary<string, object?> { ["vmId"] = vmId, ["command"] = "echo test" },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeTrue($"vm_run_command should succeed; got: {json}");

        psHost.Verify(h => h.GetVmStateAsync("local", vmId, It.IsAny<CancellationToken>()),
            Times.Once,
            "RC-1: pre-flight must route through IPowerShellHost.GetVmStateAsync when injected.");
        hvManager.Verify(h => h.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "RC-1: pre-flight must NOT call IHyperVManager.GetVmStatusAsync when IPowerShellHost is injected " +
            "(that path drags the legacy out-of-process PowerShellExecutor onto every guest tool call).");
    }

    /// <summary>
    /// RC-1: when <see cref="IPowerShellHost.GetVmStateAsync"/> returns a non-Running
    /// state, the dispatcher must surface VM_NOT_RUNNING just as it did under the
    /// legacy routing — preserving Issue #21's clearer-error UX.
    /// </summary>
    [Fact]
    public async Task RunCommand_WithPsHostInjected_NonRunningState_ReturnsVmNotRunning()
    {
        var hvManager = new Mock<IHyperVManager>(MockBehavior.Strict);
        var commandExec = new Mock<ICommandExecutor>();
        var fileTransfer = new Mock<IFileTransferService>();
        var hostResolver = new Mock<IHostResolver>();
        var gate = new Mock<IConcurrencyGate>();
        var psHost = new Mock<IPowerShellHost>();
        const string vmId = "12345678-1234-1234-1234-123456789abc";

        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        psHost.Setup(h => h.GetVmStateAsync("local", vmId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Off");

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            commandExec.Object,
            fileTransfer.Object,
            new Mock<ICheckpointManager>().Object,
            hostResolver.Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            new ServerOptions(),
            psHost.Object);

        var json = await dispatcher.DispatchAsync(
            "vm_run_command",
            new Dictionary<string, object?> { ["vmId"] = vmId, ["command"] = "echo test" },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("VM_NOT_RUNNING",
            "RC-1: routing change must preserve Issue #21's VM_NOT_RUNNING pre-flight error.");
        commandExec.Verify(
            e => e.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
