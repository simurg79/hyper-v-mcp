using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace HyperV.Mcp.Server;

/// <summary>
/// Entry point for the Hyper-V MCP Server.
/// Uses .NET Generic Host with MCP SDK stdio transport.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D7: stdio transport.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D8: stderr for logging.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Configure logging to stderr only (stdout reserved for MCP protocol).
        // See /myplans/mcp-interface/mcp-interface-design.md — MCP-D8.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // Bind configuration.
        var serverOptions = new ServerOptions();
        builder.Configuration.GetSection("HyperVMcp").Bind(serverOptions);

        // Ensure a default "local" host profile exists.
        // See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3: default hostId = "local".
        if (!serverOptions.Hosts.ContainsKey("local"))
        {
            serverOptions.Hosts["local"] = new HostProfile
            {
                HostId = "local",
                ComputerName = "localhost",
                TrustPolicy = "local",
            };
        }

        // Register infrastructure services.
        // Note: IToolDispatcher is registered last because its constructor requires
        // all other infrastructure services via DI.
        // See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton<IHostResolver, HostResolver>();
        builder.Services.AddSingleton<IErrorMapper, ErrorMapper>();
        builder.Services.AddSingleton<IConcurrencyGate>(sp =>
            new ConcurrencyGate(sp.GetRequiredService<ServerOptions>()));
        // Script-dump diagnostic testability seams (issue #48 / TI-D1, TI-D2, TI-D3):
        // production-default pass-throughs to System.Environment and
        // System.IO.Path.GetTempPath. Tests inject fakes via constructor.
        builder.Services.AddSingleton<IEnvironment, SystemEnvironment>();
        builder.Services.AddSingleton<ITempPathProvider, SystemTempPathProvider>();
        builder.Services.AddSingleton<IFileSystemProbe, FileSystemProbe>(); // Issue #73 — probe-layer test seam for ListImagesAsync
        builder.Services.AddSingleton<IBaseImageHashCache, BaseImageHashCache>(); // Issue #164 / ST-D6a + Issue #169 / VC-D5 — pre-hash cache with Shape B detached compute
        builder.Services.AddSingleton<IImagePathResolver, ImagePathResolver>();   // Issue #169 / VC-D4 — warm-up path resolver (singleton; reuses IFileSystemProbe)
        builder.Services.AddSingleton<IPowerShellExecutor, PowerShellExecutor>();   // KEEP — used by HyperVManager for host-level cmdlets
        builder.Services.AddSingleton<IPowerShellHost, PowerShellHost>();           // NEW (issue #52, ST-6) — singleton runspace host
        builder.Services.AddSingleton<ISessionStore, SessionStore>();               // depends on IPowerShellHost
        builder.Services.AddSingleton<IPowerShellDirectChannel, PowerShellDirectChannel>(); // NEW (issue #52, ST-6) — depends on IPowerShellHost + ISessionStore
        builder.Services.AddSingleton<ICheckpointManager, CheckpointManager>();
        builder.Services.AddSingleton<IIsoInspector, IsoInspector>();              // Issue #97 / ISO-D16 — OS-family check helper
        builder.Services.AddSingleton<IHyperVManager, HyperVManager>();
        builder.Services.AddSingleton<ICommandExecutor, CommandExecutor>();
        builder.Services.AddSingleton<IFileTransferService, FileTransferService>();
        builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();

        // Register MCP server with stdio transport and attribute-based tool discovery.
        // See /myplans/mcp-interface/mcp-interface-design.md — MCP-D7: stdio transport.
        // See /myplans/mcp-interface/mcp-interface-design.md — MCP-D1: attribute-based tool discovery.
        // WithToolsFromAssembly() discovers [McpServerToolType] classes (e.g., VmTools)
        // and registers their [McpServerTool]-attributed methods as MCP tools.
        // These tool methods delegate to IToolDispatcher, which is resolved from DI.
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "hyper-v-mcp-server",
                    Version = "1.0.0-preview",
                };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();

        // Issue #87 / MCP-D12: bind server lifecycle to stdio peer lifecycle.
        // The MCP SDK's stdio transport runs as a BackgroundService whose ExecuteAsync
        // returns when stdin reaches EOF (the parent MCP client closed our stdin, e.g.
        // on IDE reload). However, a BackgroundService completing normally does NOT stop
        // the Generic Host — so without this wiring the process orphans, holding the
        // PowerShell runspace and SessionStore state, and the next-spawned server can't
        // reach across the rotated stdio pipe pair ("Not connected" until kill+reload).
        //
        // Strategy: after Build(), locate the SDK's MCP hosted service (a BackgroundService)
        // via the resolved IHostedService collection, observe its ExecuteTask, and on
        // completion (whether EOF, fault, or graceful client shutdown) request a clean
        // host shutdown via IHostApplicationLifetime.StopApplication(). DI then disposes
        // IPowerShellHost (releases runspace) and SessionStore (evicts entries).
        //
        // A grace-period force-exit (~5s) protects against disposal hangs (e.g. a
        // pipeline-in-flight blocking PowerShellHost.Dispose). Stale runspaces must not
        // pin the process indefinitely — a fresh peer requires a fresh process.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var lifecycleLogger = app.Services.GetRequiredService<ILogger<Program>>();
        var hostedServices = app.Services.GetServices<IHostedService>();
        var mcpHostedService = hostedServices
            .FirstOrDefault(hs => hs.GetType().FullName?.Contains("McpServerHostedService", StringComparison.Ordinal) == true)
            as Microsoft.Extensions.Hosting.BackgroundService;

        if (mcpHostedService is not null)
        {
            // Fire-and-forget watcher: when the SDK's transport read loop completes,
            // request application shutdown and arm a grace-period force-exit.
            _ = Task.Run(async () =>
            {
                try
                {
                    // ExecuteTask is set after StartAsync runs; wait briefly for it.
                    for (int i = 0; i < 50 && mcpHostedService.ExecuteTask is null; i++)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                    }

                    var execTask = mcpHostedService.ExecuteTask;
                    if (execTask is null)
                    {
                        lifecycleLogger.LogWarning("MCP transport ExecuteTask never observed; stdio EOF watchdog disabled.");
                        return;
                    }

                    try { await execTask.ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        lifecycleLogger.LogWarning(ex, "MCP transport completed with exception; initiating shutdown.");
                    }

                    if (!lifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        lifecycleLogger.LogInformation(
                            "MCP stdio transport completed (peer disconnect / EOF). Stopping application (MCP-D12).");
                        lifetime.StopApplication();
                    }

                    // Grace-period force-exit safety net (MCP-D12 §4): if disposal hangs
                    // (e.g. PowerShell pipeline blocking runspace teardown), kill the
                    // process so we never outlive our parent.
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    lifecycleLogger.LogWarning(
                        "Graceful shutdown exceeded 5s grace period after stdio EOF; force-exiting process.");
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    lifecycleLogger.LogError(ex, "stdio EOF watchdog faulted unexpectedly.");
                }
            });
        }
        else
        {
            lifecycleLogger.LogWarning(
                "MCP server hosted service not located; stdio peer-lifecycle watchdog (MCP-D12) inactive. " +
                "Server may orphan on parent disconnect.");
        }

        // Issue #52 Phase 2 Gate 3 RC-5: eager startup initialization of the in-process
        // PowerShell host. Without this, init failures hide behind the first guest tool
        // call and the operator only learns about them when something tries to actually
        // run. Combined with RC-4 (which now caches init failures permanently), an early
        // critical log gives operators an immediate actionable signal at startup while
        // still letting host-level tools (vm_list, vm_status, vm_diag — they go through
        // the legacy executor) keep working.
        var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
        try
        {
            await app.Services.GetRequiredService<IPowerShellHost>()
                .EnsureInitializedAsync(default);
            startupLogger.LogInformation("PowerShell host initialized successfully at startup.");
        }
        catch (Exception ex)
        {
            // Log critical but DO NOT hard-fail: the legacy out-of-process executor still
            // serves vm_list / vm_status / vm_diag, and RC-4 will surface the cached init
            // error promptly when a guest-targeted tool is invoked. This preserves
            // observability without killing the server outright.
            startupLogger.LogCritical(
                ex,
                "PowerShell host failed to initialize at startup. Guest-targeted tools " +
                "(vm_run_command, vm_run_script, vm_copy_file, vm_get_file) will fail " +
                "until the server is restarted with the underlying issue fixed.");
        }

        // Issue #169 / VC-D3: detached warm-on-init for the base-image SHA-256 cache.
        // Runs on Task.Run with the host's ApplicationStopping token — does NOT block
        // app.RunAsync() (the stdio transport must come up immediately). A vm_create
        // issued before warm-up completes still pays the first-touch cost but races
        // the existing per-path SemaphoreSlim coalescing gate, so concurrent requests
        // do not double-compute. Failures are non-fatal (Constraint #2).
        var lifetimeCt = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try
            {
                var cache = app.Services.GetRequiredService<IBaseImageHashCache>();
                var imageResolver = app.Services.GetRequiredService<IImagePathResolver>();
                var paths = await imageResolver.ResolveWarmUpPathsAsync(lifetimeCt).ConfigureAwait(false);
                startupLogger.LogInformation(
                    "BaseImageHashCache warm-up starting for {Count} path(s).", paths.Count);
                var report = await cache.WarmAsync(paths, lifetimeCt).ConfigureAwait(false);
                var ok = report.Paths.Count(p =>
                    p.Status == WarmUpPathStatus.Succeeded ||
                    p.Status == WarmUpPathStatus.AlreadyWarm ||
                    p.Status == WarmUpPathStatus.WarmedFresh);
                startupLogger.LogInformation(
                    "BaseImageHashCache warm-up completed: status={Status}, succeeded={Ok}/{Total}.",
                    report.Status, ok, report.Paths.Count);
            }
            catch (OperationCanceledException) when (lifetimeCt.IsCancellationRequested)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                startupLogger.LogWarning(
                    ex,
                    "BaseImageHashCache warm-up failed. vm_create will fall back to lazy compute (pre-VC-D2 behavior).");
            }
        }, lifetimeCt);

        await app.RunAsync();
    }
}
