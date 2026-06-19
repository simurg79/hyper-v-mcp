using FluentAssertions;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.McpInterface;

/// <summary>
/// Tests that the MCP tool catalog contains the complete management + remoting
/// tool set as defined in /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
///
/// These tests are TEST-FIRST: they define the expected MCP behavior contract.
/// They should pass once tool registration is implemented with [McpServerToolType]
/// attribute-based discovery (MCP-D1).
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement each tool class in src/HyperV.Mcp.Server/Tools/ with [McpServerToolType].
/// 2. Register tools via WithToolsFromAssembly() in Program.cs.
/// 3. The ToolCatalog.AllTools list must match the registered tool set exactly.
/// </summary>
public class ToolDiscoveryTests
{
    /// <summary>
    /// The server must expose exactly 22 MCP tools covering the full management surface.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
    /// Includes vm_diag (P0 health tool), vm_os_install (P1), and vm_create_base_image (P2, Issue #51).
    /// </summary>
    [Fact]
    public void ToolCatalog_Contains_Exactly_22_Tools()
    {
        ToolCatalog.AllTools.Should().HaveCount(22,
            "the catalog specifies exactly 22 tools across 9 categories " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog, plus vm_diag, vm_os_install, vm_create_base_image)");
    }

    /// <summary>
    /// All 19 tool names must follow the vm_ prefix convention (MCP-D4).
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D4.
    /// </summary>
    [Fact]
    public void All_Tools_Have_VmPrefix()
    {
        foreach (var tool in ToolCatalog.AllTools)
        {
            tool.Name.Should().StartWith("vm_",
                $"tool '{tool.Name}' must follow the vm_ prefix convention (MCP-D4)");
        }
    }

    /// <summary>
    /// Verify every expected tool name is present in the catalog.
    /// This is the authoritative list from /myplans/mcp-interface/mcp-interface-design.md.
    /// </summary>
    [Theory]
    [InlineData("vm_echo")]
    [InlineData("vm_create")]
    [InlineData("vm_start")]
    [InlineData("vm_stop")]
    [InlineData("vm_restart")]
    [InlineData("vm_destroy")]
    [InlineData("vm_list")]
    [InlineData("vm_status")]
    [InlineData("vm_wait_ready")]
    [InlineData("vm_run_command")]
    [InlineData("vm_run_script")]
    [InlineData("vm_copy_file")]
    [InlineData("vm_get_file")]
    [InlineData("vm_checkpoint")]
    [InlineData("vm_list_images")]
    [InlineData("vm_cleanup_orphans")]
    [InlineData("vm_configure")]
    [InlineData("vm_pause")]
    [InlineData("vm_resume")]
    [InlineData("vm_create_base_image")]
    public void ToolCatalog_Contains_Expected_Tool(string toolName)
    {
        ToolCatalog.AllTools
            .Should().Contain(t => t.Name == toolName,
                $"tool '{toolName}' must be in the catalog " +
                "(see /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog)");
    }

    /// <summary>
    /// P0 tools (Phase 1 — Core Foundation) must contain the minimum viable tool set.
    /// See /myplans/design.md §6 — Phase 1: Core Foundation.
    /// </summary>
    [Fact]
    public void P0_Tools_Cover_Core_Foundation()
    {
        var p0Names = ToolCatalog.P0Tools.Select(t => t.Name).ToList();

        // Phase 1 P0 tools from /myplans/design.md §6
        p0Names.Should().Contain("vm_echo", "health check is P0");
        p0Names.Should().Contain("vm_create", "VM creation is P0");
        p0Names.Should().Contain("vm_start", "VM start is P0");
        p0Names.Should().Contain("vm_stop", "VM stop is P0");
        p0Names.Should().Contain("vm_destroy", "VM destroy is P0");
        p0Names.Should().Contain("vm_list", "VM listing is P0");
        p0Names.Should().Contain("vm_status", "VM status is P0");
        p0Names.Should().Contain("vm_run_command", "command execution is P0");
        p0Names.Should().Contain("vm_copy_file", "file copy is P0");
    }

    /// <summary>
    /// Verify tool categories are assigned correctly per the design.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
    /// </summary>
    [Theory]
    [InlineData("vm_echo", ToolCategory.Health)]
    [InlineData("vm_create", ToolCategory.Lifecycle)]
    [InlineData("vm_start", ToolCategory.Lifecycle)]
    [InlineData("vm_stop", ToolCategory.Lifecycle)]
    [InlineData("vm_restart", ToolCategory.Lifecycle)]
    [InlineData("vm_destroy", ToolCategory.Lifecycle)]
    [InlineData("vm_pause", ToolCategory.Lifecycle)]
    [InlineData("vm_resume", ToolCategory.Lifecycle)]
    [InlineData("vm_list", ToolCategory.Discovery)]
    [InlineData("vm_status", ToolCategory.Discovery)]
    [InlineData("vm_wait_ready", ToolCategory.Discovery)]
    [InlineData("vm_run_command", ToolCategory.Execution)]
    [InlineData("vm_run_script", ToolCategory.Execution)]
    [InlineData("vm_copy_file", ToolCategory.FileTransfer)]
    [InlineData("vm_get_file", ToolCategory.FileTransfer)]
    [InlineData("vm_checkpoint", ToolCategory.Checkpoints)]
    [InlineData("vm_list_images", ToolCategory.Storage)]
    [InlineData("vm_create_base_image", ToolCategory.Storage)]
    [InlineData("vm_cleanup_orphans", ToolCategory.Cleanup)]
    [InlineData("vm_configure", ToolCategory.Configuration)]
    public void Tool_Has_Correct_Category(string toolName, ToolCategory expectedCategory)
    {
        var tool = ToolCatalog.AllTools.First(t => t.Name == toolName);
        tool.Category.Should().Be(expectedCategory,
            $"tool '{toolName}' must be in category '{expectedCategory}' " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog)");
    }

    /// <summary>
    /// Verify tool priorities match the phasing plan.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog and /myplans/design.md §6.
    /// </summary>
    [Theory]
    [InlineData("vm_echo", ToolPriority.P0)]
    [InlineData("vm_create", ToolPriority.P0)]
    [InlineData("vm_start", ToolPriority.P0)]
    [InlineData("vm_stop", ToolPriority.P0)]
    [InlineData("vm_destroy", ToolPriority.P0)]
    [InlineData("vm_list", ToolPriority.P0)]
    [InlineData("vm_status", ToolPriority.P0)]
    [InlineData("vm_run_command", ToolPriority.P0)]
    [InlineData("vm_copy_file", ToolPriority.P0)]
    [InlineData("vm_restart", ToolPriority.P1)]
    [InlineData("vm_wait_ready", ToolPriority.P1)]
    [InlineData("vm_run_script", ToolPriority.P1)]
    [InlineData("vm_get_file", ToolPriority.P1)]
    [InlineData("vm_checkpoint", ToolPriority.P1)]
    [InlineData("vm_list_images", ToolPriority.P1)]
    [InlineData("vm_cleanup_orphans", ToolPriority.P1)]
    [InlineData("vm_configure", ToolPriority.P2)]
    [InlineData("vm_pause", ToolPriority.P2)]
    [InlineData("vm_resume", ToolPriority.P2)]
    [InlineData("vm_create_base_image", ToolPriority.P2)]
    public void Tool_Has_Correct_Priority(string toolName, ToolPriority expectedPriority)
    {
        var tool = ToolCatalog.AllTools.First(t => t.Name == toolName);
        tool.Priority.Should().Be(expectedPriority,
            $"tool '{toolName}' must have priority '{expectedPriority}' " +
            "(see /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog)");
    }

    /// <summary>
    /// Every tool must have a non-empty description for AI agent tool selection.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Constraint #2: Tool descriptions must be concise.
    /// </summary>
    [Fact]
    public void All_Tools_Have_NonEmpty_Description()
    {
        foreach (var tool in ToolCatalog.AllTools)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace(
                $"tool '{tool.Name}' must have a description for AI agent tool selection");
        }
    }

    /// <summary>
    /// Tool names must be unique across the catalog.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Assumption #2.
    /// </summary>
    [Fact]
    public void Tool_Names_Are_Unique()
    {
        var names = ToolCatalog.AllTools.Select(t => t.Name);
        names.Should().OnlyHaveUniqueItems(
            "tool names must be globally unique (see /myplans/mcp-interface/mcp-interface-design.md — Assumption #2)");
    }

    /// <summary>
    /// All host-scoped tools (everything except vm_echo) must accept hostId parameter.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3.
    /// This test validates the catalog correctly identifies host-scoped tools.
    /// </summary>
    [Fact]
    public void HostScoped_Tools_Exclude_Only_Echo()
    {
        var hostScoped = ToolCatalog.HostScopedTools.Select(t => t.Name).ToList();

        hostScoped.Should().NotContain("vm_echo",
            "vm_echo is a pure local health check and does not need hostId");
        hostScoped.Should().HaveCount(20,
            "20 of 22 tools are host-scoped (all except vm_echo and vm_diag)");
    }

    /// <summary>
    /// Exactly 9 distinct categories must be represented in the catalog:
    /// Health, Lifecycle, Discovery, Execution, FileTransfer, Checkpoints, Storage, Cleanup, Configuration.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog headers.
    /// </summary>
    [Fact]
    public void Catalog_Covers_All_9_Categories()
    {
        var categories = ToolCatalog.AllTools
            .Select(t => t.Category)
            .Distinct()
            .ToList();

        categories.Should().HaveCount(9,
            "9 categories are represented: Health, Lifecycle, Discovery, Execution, " +
            "FileTransfer, Checkpoints, Storage, Cleanup, Configuration");
    }

    // ─── Immutability Regression Tests ──────────────────────────────────

    /// <summary>
    /// Regression: AllTools must not be castable to List&lt;T&gt; to prevent mutation.
    /// Consumers should not be able to add, remove, or replace tools at runtime.
    /// </summary>
    [Fact]
    public void AllTools_Cannot_Be_Cast_To_Mutable_List()
    {
        var asList = ToolCatalog.AllTools as System.Collections.Generic.List<ToolDefinition>;
        asList.Should().BeNull(
            "AllTools must not be backed by a mutable List<T> — " +
            "consumers must not be able to cast and mutate the catalog");
    }

    /// <summary>
    /// Regression: ReadOnlyCollection&lt;T&gt; IS castable to IList&lt;T&gt; (it implements it
    /// explicitly), so we must verify that mutation methods throw NotSupportedException
    /// rather than relying on cast failure.
    /// </summary>
    [Fact]
    public void AllTools_Cannot_Be_Cast_To_Mutable_IList()
    {
        // ReadOnlyCollection<T> implements IList<T> explicitly — the cast succeeds.
        // The real protection is that mutation methods throw NotSupportedException.
        var asIList = (System.Collections.Generic.IList<ToolDefinition>)ToolCatalog.AllTools;

        asIList.Should().NotBeNull(
            "ReadOnlyCollection<T> implements IList<T> — cast should succeed");

        var dummy = new ToolDefinition("dummy", "dummy", ToolCategory.Health, ToolPriority.P0);

        // Add must throw
        var addAction = () => asIList.Add(dummy);
        addAction.Should().Throw<NotSupportedException>(
            "AllTools must reject Add — the catalog is immutable");

        // RemoveAt must throw
        var removeAtAction = () => asIList.RemoveAt(0);
        removeAtAction.Should().Throw<NotSupportedException>(
            "AllTools must reject RemoveAt — the catalog is immutable");

        // Indexer set must throw
        var indexerSetAction = () => asIList[0] = dummy;
        indexerSetAction.Should().Throw<NotSupportedException>(
            "AllTools must reject indexer set — the catalog is immutable");
    }

    // ─── vm_configure handler-registration regression (Issue #56) ───────

    /// <summary>
    /// Regression for GitHub Issue #56: vm_configure was in the catalog but had no
    /// dispatcher handler, so invocations fell through to a NotImplementedException
    /// stub and the catch-all mapped that to INTERNAL_ERROR.
    ///
    /// This test ensures vm_configure is BOTH in the catalog AND has a real handler.
    /// Invoking it with empty args must return an INVALID_PARAMETER envelope (from
    /// the missing-vmId precondition) — NOT an INTERNAL_ERROR envelope (which would
    /// indicate the NotImplementedException stub is back).
    ///
    /// This catches future regressions where catalog tools lack handlers.
    /// </summary>
    [Fact]
    public async Task VmConfigure_IsRegisteredWithRealHandler()
    {
        // 1. Catalog membership.
        ToolCatalog.AllTools.Should().Contain(t => t.Name == "vm_configure",
            "vm_configure must be in the catalog (Issue #56)");

        // 2. Real handler — invoke and assert the envelope is NOT the stub-thrown
        //    INTERNAL_ERROR. We construct a minimal ToolDispatcher with mocks; we
        //    only care about the dispatch path, not the underlying Hyper-V calls.
        var serverOptions = new HyperV.Mcp.Server.Configuration.ServerOptions
        {
            DefaultHostId = "local",
            MaxConcurrentOperations = 4,
            MaxPerHostOperations = 2,
        };
        serverOptions.Hosts["local"] = new HyperV.Mcp.Server.Configuration.HostProfile
        {
            HostId = "local",
            ComputerName = "localhost",
            TrustPolicy = "local",
        };

        var hvManager = new Moq.Mock<HyperV.Mcp.Server.Infrastructure.IHyperVManager>();
        var commandExecutor = new Moq.Mock<HyperV.Mcp.Server.Infrastructure.ICommandExecutor>();
        var fileTransfer = new Moq.Mock<HyperV.Mcp.Server.Infrastructure.IFileTransferService>();
        var checkpointManager = new Moq.Mock<HyperV.Mcp.Server.Infrastructure.ICheckpointManager>();
        var psExecutor = new Moq.Mock<HyperV.Mcp.Server.Infrastructure.IPowerShellExecutor>();
        var psDirect = new Moq.Mock<HyperV.Mcp.Server.Infrastructure.IPowerShellDirectChannel>();
        var hostResolver = new HyperV.Mcp.Server.Infrastructure.HostResolver(serverOptions);
        using var concurrencyGate = new HyperV.Mcp.Server.Infrastructure.ConcurrencyGate(serverOptions);

        var dispatcher = new HyperV.Mcp.Server.Infrastructure.ToolDispatcher(
            hvManager.Object,
            commandExecutor.Object,
            fileTransfer.Object,
            checkpointManager.Object,
            hostResolver,
            new HyperV.Mcp.Server.Infrastructure.ErrorMapper(),
            concurrencyGate,
            psExecutor.Object,
            psDirect.Object,
            serverOptions);

        // Invoke with EMPTY args: no vmId, no cpuCount, no memoryMB.
        var json = await dispatcher.DispatchAsync("vm_configure",
            new Dictionary<string, object?>(),
            System.Threading.CancellationToken.None);

        var response = System.Text.Json.JsonSerializer.Deserialize<McpToolResponse>(json);
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().NotBe(RuntimeErrorCodes.InternalError,
            "vm_configure must NOT fall through to the NotImplementedException stub — " +
            "this is the Issue #56 regression guard. Got error: {0}", response.Error);
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "vm_configure with empty args must return INVALID_PARAMETER from the " +
            "missing-vmId precondition (Issue #56)");
    }
}
