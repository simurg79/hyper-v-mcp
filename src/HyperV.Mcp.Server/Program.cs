using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace HyperV.Mcp.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var startup = new StartupInitialization();
        await RunAsync(args, startup).ConfigureAwait(false);
    }

    internal static async Task RunAsync(string[] args, StartupInitialization startup,
        Action<IServiceCollection>? configureServices = null)
    {
        _ = startup.WatchAsync();
        var shutdownDeadline = new ShutdownDeadline();
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSingleton(startup);
        builder.Services.AddSingleton(shutdownDeadline);

        // Reserve stdout for MCP protocol; send all logs to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        var serverOptions = new ServerOptions();
        builder.Configuration.GetSection("HyperVMcp").Bind(serverOptions);

        if (!serverOptions.Hosts.ContainsKey("local"))
        {
            serverOptions.Hosts["local"] = new HostProfile
            {
                HostId = "local",
                ComputerName = "localhost",
                TrustPolicy = "local",
            };
        }

        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton<IHostResolver, HostResolver>();
        builder.Services.AddSingleton<IErrorMapper, ErrorMapper>();
        builder.Services.AddSingleton<IConcurrencyGate>(sp =>
            new ConcurrencyGate(sp.GetRequiredService<ServerOptions>()));
        // Environment and temp-path wrappers let tests inject fakes.
        builder.Services.AddSingleton<IEnvironment, SystemEnvironment>();
        builder.Services.AddSingleton<ITempPathProvider, SystemTempPathProvider>();
        builder.Services.AddSingleton<IFileSystemProbe, FileSystemProbe>();
        builder.Services.AddSingleton<IBaseImageHashCache, BaseImageHashCache>();
        builder.Services.AddSingleton<IImagePathResolver, ImagePathResolver>();
        builder.Services.AddSingleton<IPowerShellExecutor, PowerShellExecutor>();
        builder.Services.AddSingleton<IPowerShellHost, PowerShellHost>();
        builder.Services.AddSingleton<ISessionStore, SessionStore>();
        // Register the concrete Windows channel so the router can delegate without changing it.
        builder.Services.AddSingleton<PowerShellDirectChannel>();
        builder.Services.AddSingleton<ISshExecClientFactory, SshExecClientFactory>();
        builder.Services.AddSingleton<ISshSessionStore, SshSessionStore>();
        builder.Services.AddSingleton<SshGuestChannel>();
        // Shared singleton: the router and SshSessionStore must see the hints the installer records.
        builder.Services.AddSingleton<IGuestRoutingHintStore, GuestRoutingHintStore>();
        // Probe independently of provisioning so Linux guests installed elsewhere are classified before their first command.
        builder.Services.AddSingleton<IGuestOsProbe, KvpGuestOsProbe>();
        builder.Services.AddSingleton<IPowerShellDirectChannel, GuestChannelRouter>();
        builder.Services.AddSingleton<ICheckpointManager, CheckpointManager>();
        builder.Services.AddSingleton<IIsoInspector, IsoInspector>();
        builder.Services.AddSingleton<IGuestOsClassifier, GuestOsClassifier>();
        builder.Services.AddSingleton<IKvpCompletionReader, KvpCompletionReader>();
        builder.Services.AddSingleton<IOscdimgProbe, OscdimgProbe>();
        builder.Services.AddSingleton<ISeedMediaAuthor, SeedMediaAuthor>();
        builder.Services.AddSingleton<IUbuntuAutoinstallOrchestrator, UbuntuAutoinstallOrchestrator>();
        // Password-create collaborators share the executor so tests can record their PowerShell calls.
        builder.Services.AddSingleton<IBaseImageGeneralizationProbe, BaseImageGeneralizationProbe>();
        builder.Services.AddSingleton<IUnattendSeeder, UnattendSeeder>();
        builder.Services.AddSingleton<IGuestReadinessPoller, GuestReadinessPoller>();
        builder.Services.AddSingleton<IHyperVManager, HyperVManager>();
        builder.Services.AddSingleton<ICommandExecutor, CommandExecutor>();
        builder.Services.AddSingleton<IFileTransferService, FileTransferService>();
        builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();

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

        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        var powerShellHost = app.Services.GetRequiredService<IPowerShellHost>();

        // BackgroundService completion does not stop Generic Host; bind EOF to shutdown to avoid orphaned runspaces and rotated-pipe failures.
        // Force-exit is only a disposal-hang backstop and exits non-zero to distinguish it from clean shutdown.

        // Completed only after RunAsync has disposed the host and its DI container, which is
        // where SessionStore.Dispose and PowerShellHost.Dispose actually run.
        var hostDisposalCompleted = new TaskCompletionSource();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var lifecycleLogger = app.Services.GetRequiredService<ILogger<Program>>();
        lifetime.ApplicationStopping.Register(() =>
        {
            shutdownDeadline.Begin();
            if (powerShellHost is PowerShellHost ownedHost) ownedHost.CleanupOwnedChild();
        });
        var hostedServices = app.Services.GetServices<IHostedService>();
        var mcpHostedService = hostedServices
            .FirstOrDefault(hs => hs.GetType().FullName?.Contains("McpServerHostedService", StringComparison.Ordinal) == true)
            as Microsoft.Extensions.Hosting.BackgroundService;

        if (mcpHostedService is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // ExecuteTask is set only after StartAsync runs.
                    while (mcpHostedService.ExecuteTask is null && !lifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        await Task.Delay(10).ConfigureAwait(false);
                    }

                    var execTask = mcpHostedService.ExecuteTask;
                    if (execTask is null) return;

                    var watchdog = new StdioPeerShutdownWatchdog(lifecycleLogger);
                    await watchdog.WatchAsync(
                        execTask,
                        () => lifetime.ApplicationStopping.IsCancellationRequested,
                        () =>
                        {
                            shutdownDeadline.Begin();
                            lifetime.StopApplication();
                        },
                        _ => StdioPeerShutdownWatchdog.CreateDisposalAwareWaiter(
                            hostDisposalCompleted.Task, lifecycleLogger)(shutdownDeadline.Remaining),
                        exitCode =>
                        {
                            try
                            {
                                if (powerShellHost is PowerShellHost ownedHost) ownedHost.CleanupOwnedChild();
                            }
                            finally { Environment.Exit(exitCode); }
                        }).ConfigureAwait(false);
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
                "MCP server hosted service not located; stdio peer-lifecycle watchdog inactive. " +
                "Server may orphan on parent disconnect.");
        }

        var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
        startup.Start(app.Services.GetRequiredService<IEnvironment>(), () =>
        {
            startup.SetStage("PowerShellExecutor pwsh probe");
            _ = app.Services.GetRequiredService<IPowerShellExecutor>();
            startup.ThrowIfUnavailable();
            powerShellHost.EnsureInitializedAsync(default).GetAwaiter().GetResult();
        });

        // Warm hashes off the startup path so stdio is immediately available; stop with the host token and tolerate failure.
        // Early creates still pay first-touch cost, but the per-path semaphore prevents duplicate computation.
        var lifetimeCt = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try
            {
                await startup.Completion.ConfigureAwait(false);
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

        await RunHostAndSignalDisposalAsync(() => app.RunAsync(), hostDisposalCompleted);
    }

    /// <summary>Expose the true disposal outcome to the watchdog; extraction lets tests cover production wiring.</summary>
    internal static async Task RunHostAndSignalDisposalAsync(
        Func<Task> runHostAsync,
        TaskCompletionSource hostDisposalCompleted)
    {
        try
        {
            await runHostAsync().ConfigureAwait(false);
            hostDisposalCompleted.TrySetResult();
        }
        catch (Exception ex)
        {
            // The watchdog must see the failure, not a synthesized clean completion:
            // signaling success here would force-exit 0 on a shutdown that threw.
            hostDisposalCompleted.TrySetException(ex);
            throw;
        }
    }
}
