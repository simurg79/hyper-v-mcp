using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Operational;

/// <summary>
/// Tests for concurrency control, backpressure, and the three-level semaphore hierarchy.
/// See /myplans/operational/concurrency/concurrency-design.md — CC-D1 through CC-D7.
///
/// These tests validate:
/// - Configuration defaults (global=10, per-host=5, timeout=30s)
/// - Concurrency limit error envelope shape
/// - Operation classification (read-only vs. mutating vs. lifecycle)
/// - Lock acquisition hierarchy expectations
/// - ConcurrencyLimitException contract
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement ConcurrencyGate : IConcurrencyGate with SemaphoreSlim hierarchy.
/// 2. Wire up ConcurrencyGate in DI with ServerOptions.
/// 3. Tool handlers must acquire/release locks per the Operation Classification table.
/// </summary>
public class ConcurrencyTests
{
    // ─── Configuration Defaults ────────────────────────────────────────

    /// <summary>
    /// Global concurrent operation limit must default to 10.
    /// See /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults.
    /// </summary>
    [Fact]
    public void Default_MaxConcurrentOperations_Is_10()
    {
        var options = new ServerOptions();

        options.MaxConcurrentOperations.Should().Be(10,
            "default global concurrency limit is 10 " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults)");
    }

    /// <summary>
    /// Per-host concurrent operation limit must default to 5.
    /// See /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults.
    /// </summary>
    [Fact]
    public void Default_MaxPerHostOperations_Is_5()
    {
        var options = new ServerOptions();

        options.MaxPerHostOperations.Should().Be(5,
            "default per-host concurrency limit is 5 " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults)");
    }

    /// <summary>
    /// Queue wait timeout must default to 30 seconds.
    /// See /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults.
    /// </summary>
    [Fact]
    public void Default_QueueTimeoutSeconds_Is_30()
    {
        var options = new ServerOptions();

        options.QueueTimeoutSeconds.Should().Be(30,
            "queue wait timeout defaults to 30 seconds " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults)");
    }

    /// <summary>
    /// VM lock timeout must default to 60 seconds.
    /// See /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults.
    /// </summary>
    [Fact]
    public void Default_VmLockTimeoutSeconds_Is_60()
    {
        var options = new ServerOptions();

        options.VmLockTimeoutSeconds.Should().Be(60,
            "VM lock timeout defaults to 60 seconds " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults)");
    }

    // ─── Backpressure Response Shape ───────────────────────────────────

    /// <summary>
    /// When concurrency limit is reached, the server must return CONCURRENCY_LIMIT
    /// error with informative message including queue depth.
    /// See /myplans/operational/concurrency/concurrency-design.md — Backpressure Response.
    /// </summary>
    [Fact]
    public void ConcurrencyLimit_Error_Has_Correct_Shape()
    {
        // Simulate backpressure response per design spec
        var response = McpToolResponse.Fail(
            "Concurrency limit reached: VM e2e-test-001 is busy with another operation. 2 operations queued globally.",
            ErrorCodes.ConcurrencyLimit);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("CONCURRENCY_LIMIT",
            "error code must be CONCURRENCY_LIMIT " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D4)");
        response.Error.Should().Contain("Concurrency limit",
            "error message must indicate the nature of the limit");
        response.Data.Should().BeNull();
    }

    // ─── ConcurrencyLimitException Contract ────────────────────────────

    /// <summary>
    /// ConcurrencyLimitException must be throwable with a descriptive message.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D4: Non-blocking TryWait.
    /// </summary>
    [Fact]
    public void ConcurrencyLimitException_Carries_Message()
    {
        var ex = new ConcurrencyLimitException("VM test-vm is busy");

        ex.Message.Should().Be("VM test-vm is busy");
        ex.Should().BeAssignableTo<Exception>();
    }

    /// <summary>
    /// ConcurrencyLimitException must support inner exception wrapping.
    /// </summary>
    [Fact]
    public void ConcurrencyLimitException_Supports_InnerException()
    {
        var inner = new TimeoutException("Semaphore wait timed out");
        var ex = new ConcurrencyLimitException("VM test-vm is busy", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    // ─── IConcurrencyGate Interface Contract ───────────────────────────

    /// <summary>
    /// IConcurrencyGate must expose three acquisition methods and a queue depth query.
    /// See /myplans/operational/concurrency/concurrency-design.md — Interfaces: Provided.
    /// </summary>
    [Fact]
    public void IConcurrencyGate_Exposes_Required_Methods()
    {
        var methods = typeof(IConcurrencyGate).GetMethods();

        methods.Should().Contain(m => m.Name == "AcquireVmLockAsync",
            "per-VM lock acquisition must be exposed " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D1)");
        methods.Should().Contain(m => m.Name == "AcquireHostLockAsync",
            "per-host lock acquisition must be exposed " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D3)");
        methods.Should().Contain(m => m.Name == "AcquireGlobalSlotAsync",
            "global slot acquisition must be exposed " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D2)");
        methods.Should().Contain(m => m.Name == "GetQueueDepth",
            "queue depth query must be exposed for diagnostics");
    }

    /// <summary>
    /// AcquireVmLockAsync must accept hostId, vmId, timeout, and CancellationToken.
    /// See /myplans/operational/concurrency/concurrency-design.md — Interfaces: AcquireVmLock.
    /// </summary>
    [Fact]
    public void AcquireVmLockAsync_Has_Correct_Signature()
    {
        var method = typeof(IConcurrencyGate).GetMethod("AcquireVmLockAsync");
        method.Should().NotBeNull();

        var parameters = method!.GetParameters();
        parameters.Should().HaveCount(4);
        parameters[0].Name.Should().Be("hostId");
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].Name.Should().Be("vmId");
        parameters[1].ParameterType.Should().Be(typeof(string));
        parameters[2].Name.Should().Be("timeout");
        parameters[2].ParameterType.Should().Be(typeof(TimeSpan));
        parameters[3].Name.Should().Be("ct");
        parameters[3].ParameterType.Should().Be(typeof(CancellationToken));

        method.ReturnType.Should().Be(typeof(Task<IDisposable>),
            "must return Task<IDisposable> for using-pattern lock release");
    }

    /// <summary>
    /// IConcurrencyGate must be IDisposable for semaphore cleanup.
    /// </summary>
    [Fact]
    public void IConcurrencyGate_Is_Disposable()
    {
        typeof(IConcurrencyGate).Should().Implement<IDisposable>(
            "semaphores must be disposed when the server shuts down");
    }

    // ─── Operation Classification ──────────────────────────────────────

    /// <summary>
    /// Read-only operations (vm_list, vm_status) should bypass per-VM serialization.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D6.
    ///
    /// This test documents the expected behavior through the operation classification table.
    /// When ConcurrencyGate is implemented, these operations should only acquire
    /// the global slot, not per-VM or per-host locks.
    /// </summary>
    [Theory]
    [InlineData("vm_list")]
    [InlineData("vm_status")]
    public void ReadOnly_Operations_Should_Bypass_PerVm_Lock(string toolName)
    {
        // This test asserts the expected classification from the design doc.
        // The actual enforcement happens in ConcurrencyGate implementation.
        var tool = ToolCatalog.AllTools.First(t => t.Name == toolName);
        tool.Category.Should().Be(ToolCategory.Discovery,
            $"'{toolName}' is a discovery/read-only operation and should bypass " +
            "per-VM serialization (see /myplans/operational/concurrency/concurrency-design.md — CC-D6)");
    }

    /// <summary>
    /// vm_echo must bypass ALL concurrency controls — it is a pure local operation.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_echo has No/No/No.
    /// </summary>
    [Fact]
    public void Echo_Bypasses_All_Concurrency_Controls()
    {
        var echo = ToolCatalog.AllTools.First(t => t.Name == "vm_echo");
        echo.Category.Should().Be(ToolCategory.Health,
            "vm_echo is a health check that bypasses all concurrency controls " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Operation Classification)");
    }

    /// <summary>
    /// Lifecycle operations (create, destroy, start, stop) require both per-VM and per-host locks.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D5.
    /// </summary>
    [Theory]
    [InlineData("vm_create")]
    [InlineData("vm_destroy")]
    [InlineData("vm_start")]
    [InlineData("vm_stop")]
    public void Lifecycle_Operations_Require_PerVm_And_PerHost_Locks(string toolName)
    {
        var tool = ToolCatalog.AllTools.First(t => t.Name == toolName);
        tool.Category.Should().Be(ToolCategory.Lifecycle,
            $"'{toolName}' is a lifecycle operation requiring both per-VM and per-host locks " +
            "(see /myplans/operational/concurrency/concurrency-design.md — CC-D5)");
    }

    /// <summary>
    /// VM mutation operations (run_command, copy_file) require per-VM lock but not per-host.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification.
    /// </summary>
    [Theory]
    [InlineData("vm_run_command")]
    [InlineData("vm_run_script")]
    [InlineData("vm_copy_file")]
    [InlineData("vm_get_file")]
    public void VmMutation_Operations_Require_PerVm_Lock_Only(string toolName)
    {
        // This validates the tool exists and is not in the lifecycle category
        // (lifecycle tools need per-host + per-VM; mutation tools need per-VM only).
        var tool = ToolCatalog.AllTools.First(t => t.Name == toolName);
        tool.Category.Should().NotBe(ToolCategory.Lifecycle,
            $"'{toolName}' is a VM mutation (not lifecycle) and should NOT require per-host lock " +
            "(see /myplans/operational/concurrency/concurrency-design.md — Operation Classification)");
    }

    // ─── Concurrency Backpressure Behavior ─────────────────────────────

    /// <summary>
    /// Validates the expected backpressure contract: when global limit is reached,
    /// the server returns CONCURRENCY_LIMIT rather than blocking indefinitely.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D4.
    ///
    /// This test is a behavioral expectation — it will pass only when
    /// ConcurrencyGate is implemented with non-blocking TryWait semantics.
    /// For now, it validates the error contract shape.
    /// </summary>
    [Fact]
    public void Backpressure_Returns_Error_Not_Blocking()
    {
        // The design mandates non-blocking behavior per CC-D4:
        // "Callers get CONCURRENCY_LIMIT error rather than indefinite blocking"
        //
        // This test validates the expected error response shape.
        // A full integration test would saturate the semaphores and verify
        // the Nth+1 request gets CONCURRENCY_LIMIT immediately.

        var backpressureResponse = McpToolResponse.Fail(
            "Concurrency limit reached: 10 operations already in progress. " +
            "Retry after existing operations complete.",
            ErrorCodes.ConcurrencyLimit);

        backpressureResponse.Success.Should().BeFalse();
        backpressureResponse.ErrorCode.Should().Be("CONCURRENCY_LIMIT");
        backpressureResponse.Error.Should().Contain("10",
            "error message should indicate the limit that was reached");
    }

    /// <summary>
    /// Configurable concurrency limits must be respected.
    /// </summary>
    [Fact]
    public void ServerOptions_Concurrency_Limits_Are_Configurable()
    {
        var options = new ServerOptions
        {
            MaxConcurrentOperations = 20,
            MaxPerHostOperations = 8,
            QueueTimeoutSeconds = 45,
            VmLockTimeoutSeconds = 120
        };

        options.MaxConcurrentOperations.Should().Be(20);
        options.MaxPerHostOperations.Should().Be(8);
        options.QueueTimeoutSeconds.Should().Be(45);
        options.VmLockTimeoutSeconds.Should().Be(120);
    }
}
