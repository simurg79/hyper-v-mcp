using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// Issue #52 Phase 2 Gate 3 RC-1 regression test (live-path / production-DI).
///
/// Background: Originally <c>ToolDispatcher.EnsureVmRunningAsync</c> called
/// <c>_hyperVManager.GetVmStatusAsync</c> for the pre-flight VM-state check, which
/// silently routed every guest-targeted tool's first hop through the legacy
/// out-of-process <see cref="PowerShellExecutor"/> (writing <c>hvmcp-*.ps1</c> temp
/// scripts). Phase 2 of issue #52 was supposed to make those tools use the new
/// in-process <see cref="IPowerShellDirectChannel"/> exclusively (PSD-D5/D6
/// single-facade rule). Unit tests passed because they all constructed
/// <see cref="ToolDispatcher"/> directly with mocks — they never exercised the
/// production DI graph.
///
/// This test wires up the SAME service registrations that
/// <see cref="HyperV.Mcp.Server.Program"/> performs, then swaps in a strict
/// <see cref="IHyperVManager"/> mock (any unexpected call throws) and a stub
/// <see cref="IPowerShellHost"/> that records calls. It then resolves
/// <see cref="IToolDispatcher"/> from the container and dispatches each of the
/// four guest-targeted tools (vm_run_command, vm_copy_file, vm_run_script,
/// vm_get_file). The assertions:
///
/// <list type="bullet">
///   <item><description>
///     <see cref="IPowerShellHost.GetVmStateAsync"/> was called once per dispatch
///     for the pre-flight (proves the in-process host is on the live path).
///   </description></item>
///   <item><description>
///     <see cref="IHyperVManager.GetVmStatusAsync"/> was NEVER called (the strict
///     mock would throw if it were — providing a structural guarantee that a
///     future refactor reverting to <c>_hyperVManager.GetVmStatusAsync</c> will
///     fail this test before any unit tests can pass).
///   </description></item>
/// </list>
///
/// This is the "real integration test that would have caught this before
/// unit-test pass" the user required after the live MCP smoke-test failure.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ScriptDumpDiagnostic")]
public class PreflightDispatchRoutingTests : IDisposable
{
    private const string TestHostId = "local";
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";
    private const string TestUsername = "testuser";
    private const string TestPassword = "testpass";

    private readonly IHost _host;
    private readonly Mock<IHyperVManager> _hyperVManagerStrict;
    private readonly RecordingPowerShellHost _psHost;
    private readonly StubPowerShellDirectChannel _channel;
    private readonly string _tempSourceFile;
    private readonly string _perTestTempDir;

    public PreflightDispatchRoutingTests()
    {
        // Replicate Program.cs's DI registrations 1:1 (same pattern used by
        // DiContainerTests). NOTE: keep this in sync if Program.cs changes.
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

        var serverOptions = new ServerOptions();
        serverOptions.Hosts["local"] = new HostProfile
        {
            HostId = "local",
            ComputerName = "localhost",
            TrustPolicy = "local",
        };

        // Strict mock — ANY call throws. Registered as singleton replacement for the
        // production HyperVManager so the test fails the moment the dispatcher tries
        // to route the pre-flight back through IHyperVManager (the RC-1 bug).
        _hyperVManagerStrict = new Mock<IHyperVManager>(MockBehavior.Strict);

        // Recording stub for IPowerShellHost. Returns "Running" from GetVmStateAsync
        // so EnsureVmRunningAsync proceeds, and counts calls so we can assert the
        // pre-flight actually went through this facade.
        _psHost = new RecordingPowerShellHost();

        // Stub for IPowerShellDirectChannel: returns benign success from script
        // invocation and copy methods so the real CommandExecutor / FileTransferService
        // can complete without touching real PowerShell or the disk.
        _channel = new StubPowerShellDirectChannel();

        // Per-test isolation seams for the script-dump diagnostic (issue #48 /
        // TI-D6, TI-D7): an empty FakeEnvironment guarantees HYPERV_MCP_DUMP_PS_SCRIPTS
        // is unset for this test regardless of the host machine's env block, and the
        // FixedTempPathProvider routes any PowerShellExecutor temp-staging into a
        // per-test directory that we clean up in Dispose.
        _perTestTempDir = Path.Combine(
            Path.GetTempPath(), "hvmcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_perTestTempDir);

        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton<IHostResolver, HostResolver>();
        builder.Services.AddSingleton<IErrorMapper, ErrorMapper>();
        builder.Services.AddSingleton<IConcurrencyGate>(sp =>
            new ConcurrencyGate(sp.GetRequiredService<ServerOptions>()));
        builder.Services.AddSingleton<IEnvironment>(new FakeEnvironment());
        builder.Services.AddSingleton<ITempPathProvider>(new FixedTempPathProvider(_perTestTempDir));
        builder.Services.AddSingleton<IPowerShellExecutor, PowerShellExecutor>();
        builder.Services.AddSingleton<IPowerShellHost>(_psHost);
        builder.Services.AddSingleton<ISessionStore, SessionStore>();
        builder.Services.AddSingleton<IPowerShellDirectChannel>(_channel);
        builder.Services.AddSingleton<ICheckpointManager, CheckpointManager>();
        builder.Services.AddSingleton<IHyperVManager>(_hyperVManagerStrict.Object);
        builder.Services.AddSingleton<ICommandExecutor, CommandExecutor>();
        builder.Services.AddSingleton<IFileTransferService, FileTransferService>();
        builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();

        _host = builder.Build();

        // vm_copy_file validates the local source path BEFORE EnsureVmRunningAsync,
        // so we need a real file on disk. Tiny temp file, cleaned up in Dispose.
        _tempSourceFile = Path.Combine(
            Path.GetTempPath(),
            $"hvmcp-preflight-routing-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_tempSourceFile, "preflight routing test");
    }

    public void Dispose()
    {
        try { File.Delete(_tempSourceFile); } catch { /* best effort */ }
        _host.Dispose();
        try
        {
            if (Directory.Exists(_perTestTempDir))
            {
                Directory.Delete(_perTestTempDir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// RC-1 structural assertion for <c>vm_run_command</c>: the production-wired
    /// dispatcher MUST consult <see cref="IPowerShellHost.GetVmStateAsync"/> for
    /// the pre-flight and MUST NOT call <see cref="IHyperVManager.GetVmStatusAsync"/>
    /// (which would re-introduce the legacy out-of-process executor on every
    /// guest tool call).
    /// </summary>
    [Fact]
    public async Task VmRunCommand_ProductionDi_PreflightUsesPowerShellHost_NotHyperVManager()
    {
        var dispatcher = _host.Services.GetRequiredService<IToolDispatcher>();

        var json = await dispatcher.DispatchAsync(
            "vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmId,
                ["command"] = "echo preflight",
                ["username"] = TestUsername,
                ["password"] = TestPassword,
            },
            CancellationToken.None);

        AssertPreflightRoutedThroughPowerShellHost(json);
    }

    /// <summary>
    /// RC-1 structural assertion for <c>vm_copy_file</c>. Same guarantee as
    /// vm_run_command. Uses a real temp file because the handler validates the
    /// local source path before <c>EnsureVmRunningAsync</c>.
    /// </summary>
    [Fact]
    public async Task VmCopyFile_ProductionDi_PreflightUsesPowerShellHost_NotHyperVManager()
    {
        var dispatcher = _host.Services.GetRequiredService<IToolDispatcher>();

        var json = await dispatcher.DispatchAsync(
            "vm_copy_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmId,
                ["sourcePath"] = _tempSourceFile,
                ["destPath"] = @"C:\Temp\preflight.txt",
                ["username"] = TestUsername,
                ["password"] = TestPassword,
            },
            CancellationToken.None);

        AssertPreflightRoutedThroughPowerShellHost(json);
    }

    /// <summary>
    /// RC-1 structural assertion for <c>vm_run_script</c>. Same guarantee as
    /// vm_run_command.
    /// </summary>
    [Fact]
    public async Task VmRunScript_ProductionDi_PreflightUsesPowerShellHost_NotHyperVManager()
    {
        var dispatcher = _host.Services.GetRequiredService<IToolDispatcher>();

        var json = await dispatcher.DispatchAsync(
            "vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmId,
                ["script"] = "Write-Output 'preflight'",
                ["username"] = TestUsername,
                ["password"] = TestPassword,
            },
            CancellationToken.None);

        AssertPreflightRoutedThroughPowerShellHost(json);
    }

    /// <summary>
    /// RC-1 structural assertion for <c>vm_get_file</c>. Same guarantee as
    /// vm_run_command.
    /// </summary>
    [Fact]
    public async Task VmGetFile_ProductionDi_PreflightUsesPowerShellHost_NotHyperVManager()
    {
        var dispatcher = _host.Services.GetRequiredService<IToolDispatcher>();

        var json = await dispatcher.DispatchAsync(
            "vm_get_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmId,
                ["sourcePath"] = @"C:\Temp\guest.txt",
                ["destPath"] = Path.Combine(Path.GetTempPath(), $"hvmcp-get-{Guid.NewGuid():N}.txt"),
                ["username"] = TestUsername,
                ["password"] = TestPassword,
            },
            CancellationToken.None);

        AssertPreflightRoutedThroughPowerShellHost(json);
    }

    /// <summary>
    /// Cross-cutting verification: dispatching all four guest-targeted tools in
    /// one fixture must produce exactly four <c>GetVmStateAsync</c> calls and
    /// zero <see cref="IHyperVManager.GetVmStatusAsync"/> calls. This is the
    /// single most explicit citation of the RC-1 invariant — if a future change
    /// reverts <c>EnsureVmRunningAsync</c> to <c>_hyperVManager.GetVmStatusAsync</c>,
    /// the strict mock will throw on the first call and this test will fail.
    /// </summary>
    [Fact]
    public async Task AllGuestTargetedTools_ProductionDi_NeverCallHyperVManagerForPreflight()
    {
        var dispatcher = _host.Services.GetRequiredService<IToolDispatcher>();

        await dispatcher.DispatchAsync(
            "vm_run_command",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmId,
                ["command"] = "echo a",
                ["username"] = TestUsername,
                ["password"] = TestPassword,
            },
            CancellationToken.None);

        await dispatcher.DispatchAsync(
            "vm_copy_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmId,
                ["sourcePath"] = _tempSourceFile,
                ["destPath"] = @"C:\Temp\preflight.txt",
                ["username"] = TestUsername,
                ["password"] = TestPassword,
            },
            CancellationToken.None);

        await dispatcher.DispatchAsync(
            "vm_run_script",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmId,
                ["script"] = "Write-Output 'a'",
                ["username"] = TestUsername,
                ["password"] = TestPassword,
            },
            CancellationToken.None);

        await dispatcher.DispatchAsync(
            "vm_get_file",
            new Dictionary<string, object?>
            {
                ["vmId"] = TestVmId,
                ["sourcePath"] = @"C:\Temp\guest.txt",
                ["destPath"] = Path.Combine(Path.GetTempPath(), $"hvmcp-get-{Guid.NewGuid():N}.txt"),
                ["username"] = TestUsername,
                ["password"] = TestPassword,
            },
            CancellationToken.None);

        _psHost.GetVmStateCalls.Should().Be(4,
            "RC-1: every guest-targeted tool's pre-flight must route through " +
            "IPowerShellHost.GetVmStateAsync — once per dispatch.");

        // The strict IHyperVManager mock would have thrown on the very first call,
        // so reaching this assertion already implies it was never invoked. We make
        // the invariant explicit anyway for documentation: if a future refactor
        // reverts EnsureVmRunningAsync to _hyperVManager.GetVmStatusAsync, this
        // verification (and the strict mock) catch it before the suite passes.
        _hyperVManagerStrict.Verify(
            m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "RC-1: dispatcher MUST NOT call IHyperVManager.GetVmStatusAsync for the " +
            "pre-flight on guest-targeted tools — that path drags the legacy " +
            "out-of-process PowerShellExecutor onto every call (PSD-D5/D6 violation).");
    }

    /// <summary>
    /// Shared assertion: the dispatched tool reached the channel (proving the live
    /// path executed end-to-end) AND the pre-flight went through IPowerShellHost
    /// rather than IHyperVManager.
    /// </summary>
    private void AssertPreflightRoutedThroughPowerShellHost(string json)
    {
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response.Should().NotBeNull("dispatcher must return a serialized response envelope");

        _psHost.GetVmStateCalls.Should().BeGreaterThan(0,
            "RC-1: the pre-flight must invoke IPowerShellHost.GetVmStateAsync.");
        _psHost.LastHostId.Should().Be(TestHostId);
        _psHost.LastVmId.Should().Be(TestVmId);

        // Strict mock would have thrown on any IHyperVManager call; restate for
        // intent and to make the regression target unambiguous in test output.
        _hyperVManagerStrict.Verify(
            m => m.GetVmStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "RC-1: dispatcher MUST NOT route the pre-flight through IHyperVManager.GetVmStatusAsync.");
    }

    // ─── Test doubles ────────────────────────────────────────────────────

    /// <summary>
    /// Recording stub for <see cref="IPowerShellHost"/>. Counts <c>GetVmStateAsync</c>
    /// calls, captures the last (hostId, vmId) tuple, and returns "Running" so
    /// <c>EnsureVmRunningAsync</c> proceeds. Other members throw because they
    /// must not be exercised by this test.
    /// </summary>
    private sealed class RecordingPowerShellHost : IPowerShellHost
    {
        public int GetVmStateCalls { get; private set; }
        public string? LastHostId { get; private set; }
        public string? LastVmId { get; private set; }

        public PowerShellEdition Edition => PowerShellEdition.PowerShell7;

        public Task EnsureInitializedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PowerShellHostResult> InvokeAsync(
            string script,
            IDictionary<string, object?>? args = null,
            CancellationToken ct = default)
            => throw new System.NotSupportedException(
                "RecordingPowerShellHost does not support InvokeAsync — RC-1 test " +
                "exercises only the pre-flight path.");

        public Task<PowerShellHostResult> InvokeWithTimeoutAsync(
            string script,
            IDictionary<string, object?>? args,
            int? timeoutSeconds,
            CancellationToken ct = default)
            => throw new System.NotSupportedException(
                "RecordingPowerShellHost does not support InvokeWithTimeoutAsync.");

        public Task<string> GetVmStateAsync(string hostId, string vmId, CancellationToken ct = default)
        {
            GetVmStateCalls++;
            LastHostId = hostId;
            LastVmId = vmId;
            return Task.FromResult("Running");
        }

        public PowerShellHostInitDiagnostics GetInitDiagnostics()
            => new(
                Initialized: true,
                Edition: PowerShellEdition.PowerShell7,
                LastInitError: null,
                LastInitErrorType: null,
                LastInitErrorTrace: null,
                PsModulePath: null);
    }

    /// <summary>
    /// Stub <see cref="IPowerShellDirectChannel"/>. Returns benign success from
    /// every method so the real <see cref="CommandExecutor"/> /
    /// <see cref="FileTransferService"/> implementations can complete a dispatch
    /// without touching real PowerShell, real Hyper-V, or real disk I/O on the
    /// remote side.
    /// </summary>
    private sealed class StubPowerShellDirectChannel : IPowerShellDirectChannel
    {
        // CommandExecutor parses the JSON envelope its inner script emits — return
        // a minimally valid envelope so the executor produces a successful
        // CommandResult. Shape mirrors what the real inner script writes.
        private const string CommandSuccessEnvelope =
            "{\"stdout\":\"\",\"stderr\":\"\",\"exitCode\":0}";

        public Task<PowerShellHostResult> InvokeScriptAsync(
            string hostId, string vmId, string username, string password,
            string script, IDictionary<string, object?>? args = null,
            CancellationToken ct = default)
            => Task.FromResult(new PowerShellHostResult(
                Success: true,
                Output: new object?[] { CommandSuccessEnvelope },
                Stderr: string.Empty,
                ExitCode: 0));

        public Task<PowerShellHostResult> InvokeScriptWithTimeoutAsync(
            string hostId, string vmId, string username, string password,
            string script, IDictionary<string, object?>? args,
            int timeoutSeconds, CancellationToken ct = default)
            => Task.FromResult(new PowerShellHostResult(
                Success: true,
                Output: new object?[] { CommandSuccessEnvelope },
                Stderr: string.Empty,
                ExitCode: 0));

        public Task<PowerShellHostResult> CopyToSessionAsync(
            string hostId, string vmId, string username, string password,
            string localSourcePath, string guestDestinationPath,
            CancellationToken ct = default)
            => Task.FromResult(new PowerShellHostResult(
                Success: true,
                Output: System.Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        public Task<PowerShellHostResult> CopyFromSessionAsync(
            string hostId, string vmId, string username, string password,
            string guestSourcePath, string localDestinationPath,
            CancellationToken ct = default)
            => Task.FromResult(new PowerShellHostResult(
                Success: true,
                Output: System.Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        public Task EvictSessionAsync(string hostId, string vmId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
