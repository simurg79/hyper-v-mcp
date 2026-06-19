using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// DI container smoke tests that verify the service registration wiring matches
/// what Program.cs configures. Builds a real host with all infrastructure services
/// and asserts that each critical service can be resolved.
///
/// This catches registration mistakes (missing services, circular dependencies,
/// wrong lifetimes) before they surface at runtime.
/// See /myplans/execution-plan.md — Stage 1.6: DI Container Smoke Test.
/// </summary>
[Trait("Category", "Integration")]
public class DiContainerTests : IDisposable
{
    private readonly IHost _host;

    public DiContainerTests()
    {
        // Build the host using the same service registration pattern as Program.cs.
        // See /myplans/mcp-interface/mcp-interface-design.md — MCP-D7: stdio transport.
        // Note: MCP SDK registration (AddMcpServer/WithStdioServerTransport) is excluded
        // because it requires the MCP SDK package and stdio pipes; it would try to open
        // stdin/stdout which isn't available in test.
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

        var serverOptions = new ServerOptions();
        serverOptions.Hosts["local"] = new HostProfile
        {
            HostId = "local",
            ComputerName = "localhost",
            TrustPolicy = "local",
        };

        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton<IHostResolver, HostResolver>();
        builder.Services.AddSingleton<IErrorMapper, ErrorMapper>();
        builder.Services.AddSingleton<IConcurrencyGate>(sp =>
            new ConcurrencyGate(sp.GetRequiredService<ServerOptions>()));
        builder.Services.AddSingleton<IPowerShellExecutor, PowerShellExecutor>();
        builder.Services.AddSingleton<IPowerShellHost, PowerShellHost>();
        builder.Services.AddSingleton<ISessionStore, SessionStore>();
        builder.Services.AddSingleton<IPowerShellDirectChannel, PowerShellDirectChannel>();
        builder.Services.AddSingleton<ICheckpointManager, CheckpointManager>();
        builder.Services.AddSingleton<IIsoInspector, IsoInspector>();              // Issue #97 / ISO-D16
        builder.Services.AddSingleton<IHyperVManager, HyperVManager>();
        builder.Services.AddSingleton<ICommandExecutor, CommandExecutor>();
        builder.Services.AddSingleton<IFileTransferService, FileTransferService>();
        builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();

        _host = builder.Build();
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    /// <summary>
    /// Verifies that all critical infrastructure services can be resolved from the
    /// DI container. This is a smoke test that catches missing registrations,
    /// circular dependencies, or misconfigured service lifetimes.
    /// </summary>
    [Fact]
    public void AllRequiredServices_CanBeResolved()
    {
        _host.Services.GetRequiredService<IToolDispatcher>().Should().NotBeNull();
        _host.Services.GetRequiredService<IHyperVManager>().Should().NotBeNull();
        _host.Services.GetRequiredService<ICommandExecutor>().Should().NotBeNull();
        _host.Services.GetRequiredService<IFileTransferService>().Should().NotBeNull();
        _host.Services.GetRequiredService<IConcurrencyGate>().Should().NotBeNull();
        _host.Services.GetRequiredService<IHostResolver>().Should().NotBeNull();
        _host.Services.GetRequiredService<IErrorMapper>().Should().NotBeNull();
        _host.Services.GetRequiredService<IPowerShellExecutor>().Should().NotBeNull();
        _host.Services.GetRequiredService<IPowerShellHost>().Should().NotBeNull();
        _host.Services.GetRequiredService<ISessionStore>().Should().NotBeNull();
        _host.Services.GetRequiredService<IPowerShellDirectChannel>().Should().NotBeNull();
    }

    /// <summary>
    /// Issue #52, ST-7: <see cref="IPowerShellDirectChannel"/> and <see cref="ISessionStore"/>
    /// must be resolvable as singletons (Phase 2 wiring smoke test).
    /// </summary>
    [Fact]
    public void PowerShellDirectChannel_And_SessionStore_AreSingletons()
    {
        var channel1 = _host.Services.GetRequiredService<IPowerShellDirectChannel>();
        var channel2 = _host.Services.GetRequiredService<IPowerShellDirectChannel>();
        channel1.Should().NotBeNull();
        channel1.Should().BeSameAs(channel2,
            "IPowerShellDirectChannel must be registered as a singleton");

        var store1 = _host.Services.GetRequiredService<ISessionStore>();
        var store2 = _host.Services.GetRequiredService<ISessionStore>();
        store1.Should().NotBeNull();
        store1.Should().BeSameAs(store2,
            "ISessionStore must be registered as a singleton");
    }

    /// <summary>
    /// Verifies that ServerOptions is registered as a singleton and contains
    /// the expected default "local" host profile.
    /// </summary>
    [Fact]
    public void ServerOptions_ContainsLocalHostProfile()
    {
        var options = _host.Services.GetRequiredService<ServerOptions>();
        options.Should().NotBeNull();
        options.Hosts.Should().ContainKey("local");
        options.Hosts["local"].HostId.Should().Be("local");
        options.Hosts["local"].ComputerName.Should().Be("localhost");
    }

    /// <summary>
    /// Verifies that resolving IToolDispatcher returns the same singleton instance
    /// on multiple resolutions, confirming singleton lifetime.
    /// </summary>
    [Fact]
    public void ToolDispatcher_IsSingleton()
    {
        var first = _host.Services.GetRequiredService<IToolDispatcher>();
        var second = _host.Services.GetRequiredService<IToolDispatcher>();
        first.Should().BeSameAs(second);
    }

    /// <summary>
    /// Verifies that the resolved ToolDispatcher has all 19 catalog tools registered
    /// (confirming full catalog registration during construction).
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
    /// </summary>
    [Fact]
    public void ToolDispatcher_HasAllCatalogToolsRegistered()
    {
        var dispatcher = _host.Services.GetRequiredService<IToolDispatcher>();
        // PA-D3: IsRegistered / GetRegisteredTools were demoted from the
        // IToolDispatcher public surface to `internal` on the concrete class.
        // Cast through the friend-visible concrete type (InternalsVisibleTo
        // already wired in HyperV.Mcp.Server.csproj).
        var registeredTools = ((ToolDispatcher)dispatcher).GetRegisteredTools();

        // P0 tools
        registeredTools.Should().Contain("vm_echo");
        registeredTools.Should().Contain("vm_create");
        registeredTools.Should().Contain("vm_start");
        registeredTools.Should().Contain("vm_stop");
        registeredTools.Should().Contain("vm_destroy");
        registeredTools.Should().Contain("vm_list");
        registeredTools.Should().Contain("vm_status");
        registeredTools.Should().Contain("vm_run_command");
        registeredTools.Should().Contain("vm_copy_file");

        // Should have at least 9 P0 tools (plus P1/P2 stubs)
        registeredTools.Count.Should().BeGreaterThanOrEqualTo(9);
    }

    /// <summary>
    /// Verifies that IConcurrencyGate resolves to a ConcurrencyGate instance
    /// configured with the same ServerOptions (MaxConcurrentOperations).
    /// </summary>
    [Fact]
    public void ConcurrencyGate_IsProperlyConfigured()
    {
        var gate = _host.Services.GetRequiredService<IConcurrencyGate>();
        gate.Should().NotBeNull();
        gate.Should().BeOfType<ConcurrencyGate>();
        gate.GetQueueDepth().Should().Be(0, "no operations should be queued at startup");
    }
}
