using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #65 / DIAG-D7 — <see cref="ILogger{TCategoryName}"/> for
/// <see cref="ToolDispatcher"/> is wired through DI and used at the
/// <c>vm_cleanup_orphans</c> catch site instead of the previous raw
/// <c>Console.Error.WriteLine</c> fallback.
/// See /myplans/diagnostics/diagnostics-design.md — DIAG-D7.
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/65.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue65ToolDispatcherLoggerTests
{
    private const string LocalHostId = "local";

    /// <summary>
    /// DI smoke test: the Generic Host's default <c>AddLogging</c> registers the
    /// open-generic <c>ILogger&lt;T&gt;</c> binding, so resolving
    /// <see cref="ToolDispatcher"/> through the DI container must produce an
    /// instance whose <c>ILogger&lt;ToolDispatcher&gt;</c> is NOT
    /// <see cref="NullLogger{T}"/>. The fallback in <c>ToolDispatcher</c>'s
    /// constructor (NullLogger) only fires for legacy fixture wiring that
    /// passes <c>logger: null</c> directly.
    /// </summary>
    [Fact]
    public void DI_Resolved_ToolDispatcher_Has_NonNullLogger_From_Container()
    {
        // Build a minimal Generic Host that mirrors Program.cs registration order
        // (DiContainerTests already covers full wiring; here we just need
        // ILogger<ToolDispatcher> to be resolvable from the container).
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

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<IToolDispatcher>();
        dispatcher.Should().NotBeNull();

        // The injected logger must be the container-provided one, not NullLogger.
        var loggerField = typeof(ToolDispatcher)
            .GetField("_logger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        loggerField.Should().NotBeNull("DIAG-D7 introduced an _logger field on ToolDispatcher.");
        var loggerValue = loggerField!.GetValue(dispatcher);
        loggerValue.Should().NotBeNull();
        loggerValue.Should().NotBeOfType<NullLogger<ToolDispatcher>>(
            "DIAG-D7 (#65): the DI container must inject a real ILogger<ToolDispatcher>; " +
            "the NullLogger fallback is only for legacy direct-construction tests.");
    }

    /// <summary>
    /// DIAG-D7 (#65): when the manager throws on the <c>vm_cleanup_orphans</c>
    /// catch path, the dispatcher must call <c>ILogger.LogError</c> at
    /// <see cref="LogLevel.Error"/> with the original exception attached
    /// instead of writing the failure to <c>Console.Error</c>.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_CatchPath_Logs_LogError_With_Exception()
    {
        var loggerMock = new Mock<ILogger<ToolDispatcher>>();
        loggerMock
            .Setup(l => l.IsEnabled(It.IsAny<LogLevel>()))
            .Returns(true);

        var hvManager = new Mock<IHyperVManager>();
        hvManager
            .Setup(m => m.CleanupOrphansAsync(LocalHostId, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom-from-manager-65"));

        var gate = new Mock<IConcurrencyGate>();
        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        var dispatcher = new ToolDispatcher(
            hvManager.Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            new ServerOptions { DefaultHostId = LocalHostId },
            psHost: null,
            logger: loggerMock.Object);

        var json = await dispatcher.DispatchAsync(
            "vm_cleanup_orphans",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        // Assert: structured INTERNAL_ERROR envelope (orchestrator-side guarantee unchanged).
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(RuntimeErrorCodes.InternalError);

        // Verify the underlying ILogger.Log(LogLevel.Error, ...) extension was called
        // exactly once with the originating Exception attached. This is the canonical
        // Moq pattern for asserting on ILogger extension methods (Log → underlying call).
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(ex => ex != null && ex.Message == "boom-from-manager-65"),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "DIAG-D7 (#65): the cleanup-orphans catch path must route through ILogger.LogError " +
            "with the original exception attached, not Console.Error.WriteLine.");
    }

    /// <summary>
    /// DIAG-D7 (#65) build-identity gate: <c>vm_diag</c> reports
    /// <c>diagVersion = "v12"</c> so smoke probes can confirm the post-warm-up
    /// diagnostic shape is the one shipped with the VC-D12..VC-D19 / Issue #170
    /// cohort (which added sidecar persistence telemetry in
    /// <c>baseImageHashCache</c>).
    /// </summary>
    [Fact]
    public async Task VmDiag_Reports_DiagVersion_v12()
    {
        var dispatcher = new ToolDispatcher(
            new Mock<IHyperVManager>().Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            new Mock<IConcurrencyGate>().Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            new ServerOptions());

        var json = await dispatcher.DispatchAsync(
            "vm_diag",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        var data = root.GetProperty("data");
        data.GetProperty("diagVersion").GetString().Should().Be("v12",
            "DIAG-D7 / Issue #65: vm_diag originally reported v10 to advertise the post-#59 spill-aware build. " +
            "VC-D7 / Issue #169 bumped to v11 to advertise the additive `baseImageHashCache` block. " +
            "VC-D15 / Issue #170 bumps to v12 to advertise the sidecar persistence cohort.");
    }
}
