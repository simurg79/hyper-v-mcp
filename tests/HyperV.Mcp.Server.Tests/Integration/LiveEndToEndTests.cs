using System.Security.Principal;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// Live end-to-end integration tests that exercise real Hyper-V operations through
/// the full ToolDispatcher pipeline. Unlike the mock-based <see cref="EndToEndIntegrationTests"/>,
/// these tests create actual VMs, run commands inside them, and destroy them.
///
/// Prerequisites:
/// - Windows with Hyper-V role enabled
/// - Process running with admin (elevated) privileges
/// - <c>HYPERV_MCP_BASE_VHDX</c> environment variable set to a valid base VHDX image path
///
/// Run these tests explicitly via: <c>dotnet test --filter "Category=LiveE2E"</c>.
///
/// <para><b>Important:</b> Because xUnit v2 does not support <c>Assert.Skip</c> or
/// <c>Skip.If</c>, tests that cannot run due to missing prerequisites will
/// <b>report as "passed"</b> (not "skipped"). Check the test output log for
/// lines starting with "SKIP:" to identify tests that did not actually execute.</para>
///
/// Uses real <see cref="ToolDispatcher"/>, <see cref="PowerShellExecutor"/>,
/// <see cref="HyperVManager"/>, <see cref="CommandExecutor"/>, <see cref="FileTransferService"/>,
/// <see cref="HostResolver"/>, <see cref="ErrorMapper"/>, <see cref="ConcurrencyGate"/>,
/// <see cref="SessionStore"/>. No mocks.
/// </summary>
[Trait("Category", "LiveE2E")]
[Collection("LiveE2E")]
public class LiveEndToEndTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    // Infrastructure services — built in InitializeAsync
    private ServerOptions? _serverOptions;
    private HostResolver? _hostResolver;
    private ConcurrencyGate? _concurrencyGate;
    private SessionStore? _sessionStore;
    private ToolDispatcher? _dispatcher;

    // Track VM IDs created during tests for best-effort cleanup
    private readonly List<string> _createdVmIds = new();

    /// <summary>
    /// Checks whether the machine meets all prerequisites for live Hyper-V testing.
    /// Returns <c>true</c> only if admin, Hyper-V available, and base VHDX configured.
    /// </summary>
    private static bool CanRunLiveTests()
    {
        return CanRunLiveTests(out _, out _, out _);
    }

    /// <summary>
    /// Checks prerequisites and provides reasons when they are not met.
    /// </summary>
    private static bool CanRunLiveTests(out bool isAdmin, out bool hyperVAvailable, out string? baseVhdxPath)
    {
        // Check 1: Is process elevated (admin)?
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

        // Check 2: Is Hyper-V available? Check that the vmms service exists AND is running.
        hyperVAvailable = false;
        try
        {
            var vmmsService = System.ServiceProcess.ServiceController
                .GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, "vmms", StringComparison.OrdinalIgnoreCase));
            hyperVAvailable = vmmsService?.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch
        {
            // If we can't query services, Hyper-V is not available
        }

        // Check 3: Is HYPERV_MCP_BASE_VHDX set and does the file exist?
        baseVhdxPath = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX");
        var baseVhdxExists = !string.IsNullOrWhiteSpace(baseVhdxPath) && File.Exists(baseVhdxPath);

        return isAdmin && hyperVAvailable && baseVhdxExists;
    }

    public LiveEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Builds the full DI stack with real services if prerequisites are met.
    /// If prerequisites are not met, the services remain null and each test
    /// will early-return via the <see cref="CanRunLiveTests"/> guard.
    /// </summary>
    public Task InitializeAsync()
    {
        if (!CanRunLiveTests(out var isAdmin, out var hyperVAvailable, out var baseVhdxPath))
        {
            _output.WriteLine("Skipping LiveE2E setup — prerequisites not met:");
            _output.WriteLine($"  Admin: {isAdmin}");
            _output.WriteLine($"  Hyper-V: {hyperVAvailable}");
            _output.WriteLine($"  BaseVHDX: {baseVhdxPath ?? "(not set)"}");
            return Task.CompletedTask;
        }

        _output.WriteLine($"LiveE2E prerequisites met. BaseVHDX: {baseVhdxPath}");

        // Build real services with NullLoggerFactory
        var loggerFactory = NullLoggerFactory.Instance;

        var psExecutor = new PowerShellExecutor(
            loggerFactory.CreateLogger<PowerShellExecutor>());

        _serverOptions = new ServerOptions
        {
            MaxConcurrentOperations = 10,
            MaxPerHostOperations = 5,
            DefaultHostId = "local",
        };
        _serverOptions.Hosts["local"] = new HostProfile
        {
            HostId = "local",
            ComputerName = "localhost",
            TrustPolicy = "local",
            BaseVhdxPath = baseVhdxPath,
        };

        _hostResolver = new HostResolver(_serverOptions);
        _concurrencyGate = new ConcurrencyGate(_serverOptions);
        var errorMapper = new ErrorMapper();

        // Phase 2 (issue #52): in-process PowerShell host + direct channel.
        var psHost = new PowerShellHost(loggerFactory.CreateLogger<PowerShellHost>());

        _sessionStore = new SessionStore(
            psHost,
            loggerFactory.CreateLogger<SessionStore>());

        var directChannel = new PowerShellDirectChannel(
            psHost,
            _sessionStore,
            loggerFactory.CreateLogger<PowerShellDirectChannel>());

        var hyperVManager = new HyperVManager(
            psExecutor,
            _hostResolver,
            _serverOptions,
            loggerFactory.CreateLogger<HyperVManager>(),
            new HyperV.Mcp.Server.Tests.Runtime.TestIsoInspector());

        var commandExecutor = new CommandExecutor(
            directChannel,
            _hostResolver,
            loggerFactory.CreateLogger<CommandExecutor>());

        var fileTransferService = new FileTransferService(
            directChannel,
            _hostResolver,
            loggerFactory.CreateLogger<FileTransferService>());

        var checkpointManager = new CheckpointManager(
            psExecutor,
            _hostResolver,
            _sessionStore,
            loggerFactory.CreateLogger<CheckpointManager>());

        _dispatcher = new ToolDispatcher(
            hyperVManager,
            commandExecutor,
            fileTransferService,
            checkpointManager,
            _hostResolver,
            errorMapper,
            _concurrencyGate,
            psExecutor,
            directChannel,
            _serverOptions);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Best-effort cleanup: destroys any VMs that were created during tests.
    /// Disposes <see cref="ConcurrencyGate"/> and <see cref="SessionStore"/>.
    /// </summary>
    public async Task DisposeAsync()
    {
        // Best-effort destroy any VMs created during the test run
        if (_dispatcher != null)
        {
            foreach (var vmId in _createdVmIds)
            {
                try
                {
                    _output.WriteLine($"Cleanup: destroying VM {vmId}");
                    await _dispatcher.DispatchAsync("vm_destroy",
                        new Dictionary<string, object?> { ["vmId"] = vmId });
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Cleanup warning: failed to destroy VM {vmId}: {ex.Message}");
                }
            }
        }

        _sessionStore?.Dispose();
        _concurrencyGate?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helper methods
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates a unique VM name for each test run to avoid collisions.
    /// Includes a timestamp and a random suffix to prevent name conflicts
    /// even when multiple test runs start in the same second.
    /// </summary>
    private static string GenerateVmName(string testSuffix = "")
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var random = Guid.NewGuid().ToString("N")[..6];
        return string.IsNullOrEmpty(testSuffix)
            ? $"e2e-test-{timestamp}-{random}"
            : $"e2e-test-{testSuffix}-{timestamp}-{random}";
    }

    /// <summary>
    /// Parses a JSON response and extracts the vmId from the data envelope.
    /// Tracks the vmId for cleanup in <see cref="DisposeAsync"/>.
    /// </summary>
    private string ExtractAndTrackVmId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var vmId = doc.RootElement.GetProperty("data").GetProperty("vmId").GetString()!;
        _createdVmIds.Add(vmId);
        return vmId;
    }

    /// <summary>
    /// Asserts that the response JSON represents a success envelope.
    /// </summary>
    private static void AssertSuccessResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue(
            $"Expected success response but got: {json}");
    }

    /// <summary>
    /// Logs test skip reason and returns. Used by each test when prerequisites are not met.
    /// <para><b>Note:</b> Because xUnit v2 does not support <c>Assert.Skip</c> or
    /// <c>Skip.If</c>, the calling test will report as "passed" (not "skipped")
    /// when this method is used. Check the test output for "SKIP:" lines.</para>
    /// </summary>
    private void SkipTest(string testName)
    {
        _output.WriteLine($"SKIP: {testName} — prerequisites not met (no admin, no Hyper-V, or no base VHDX).");
    }

    /// <summary>
    /// Waits for a VM to become fully ready by polling in two phases:
    /// <list type="number">
    ///   <item>Phase 1: Poll <c>vm_status</c> until the VM reaches the "Running" state.</item>
    ///   <item>Phase 2: Poll <c>vm_run_command</c> with a simple <c>echo ready</c> until
    ///   guest services are responsive.</item>
    /// </list>
    /// Throws <see cref="TimeoutException"/> if the VM does not become ready within the timeout.
    /// </summary>
    private async Task WaitForVmReadyAsync(string vmId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        // Phase 1: Wait for "Running" state
        _output.WriteLine("Phase 1: Waiting for VM to reach 'Running' state...");
        while (DateTime.UtcNow < deadline)
        {
            var statusResult = await _dispatcher!.DispatchAsync("vm_status",
                new Dictionary<string, object?> { ["vmId"] = vmId });
            using var doc = JsonDocument.Parse(statusResult);
            var root = doc.RootElement;
            if (root.GetProperty("success").GetBoolean())
            {
                var state = root.GetProperty("data").GetProperty("state").GetString();
                _output.WriteLine($"  VM state: {state}");
                if (state == "Running") break;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException($"VM {vmId} did not reach 'Running' state within {timeout}");
        }

        // Phase 2: Wait for guest services (command execution) to respond
        _output.WriteLine("Phase 2: Waiting for guest services to respond...");
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var cmdResult = await _dispatcher!.DispatchAsync("vm_run_command",
                    new Dictionary<string, object?>
                    {
                        ["vmId"] = vmId,
                        ["command"] = "echo ready",
                        ["shell"] = "cmd",
                        ["timeoutSeconds"] = 10,
                    });
                using var doc = JsonDocument.Parse(cmdResult);
                if (doc.RootElement.GetProperty("success").GetBoolean())
                {
                    _output.WriteLine("  Guest services are responsive.");
                    return;
                }
            }
            catch
            {
                // Guest not ready yet — swallow and retry
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        throw new TimeoutException($"VM {vmId} did not become ready within {timeout}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 1. Full VM Lifecycle: Create → Status → RunCommand → Stop → Destroy
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Exercises the complete VM lifecycle through the real ToolDispatcher pipeline:
    /// vm_create → vm_status → vm_run_command → vm_stop → vm_destroy.
    ///
    /// Prerequisites: Admin, Hyper-V enabled, HYPERV_MCP_BASE_VHDX env var set.
    /// Timeout: 10 minutes (VM creation + readiness probe + command execution + teardown).
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task FullVmLifecycle_CreateRunCommandStopDestroy()
    {
        if (!CanRunLiveTests())
        {
            SkipTest(nameof(FullVmLifecycle_CreateRunCommandStopDestroy));
            return;
        }

        var vmName = GenerateVmName("lifecycle");
        string? vmId = null;

        try
        {
            // Step 1: Create VM
            _output.WriteLine($"Creating VM: {vmName}");
            var createResult = await _dispatcher!.DispatchAsync("vm_create",
                new Dictionary<string, object?> { ["name"] = vmName });
            AssertSuccessResponse(createResult);
            vmId = ExtractAndTrackVmId(createResult);
            _output.WriteLine($"VM created with ID: {vmId}");

            // Step 2: Wait for VM to be fully ready (Running state + guest services)
            await WaitForVmReadyAsync(vmId, TimeSpan.FromSeconds(120));

            // Step 3: Check VM status — should be Running
            _output.WriteLine("Checking VM status...");
            var statusResult = await _dispatcher.DispatchAsync("vm_status",
                new Dictionary<string, object?> { ["vmId"] = vmId });
            AssertSuccessResponse(statusResult);
            using (var statusDoc = JsonDocument.Parse(statusResult))
            {
                var state = statusDoc.RootElement.GetProperty("data").GetProperty("state").GetString();
                _output.WriteLine($"VM state: {state}");
                state.Should().Be("Running", "VM should be running after creation and readiness probe");
            }

            // Step 4: Run a command inside the VM
            _output.WriteLine("Running 'hostname' command inside VM...");
            var cmdResult = await _dispatcher.DispatchAsync("vm_run_command",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["command"] = "hostname",
                    ["shell"] = "cmd",
                    ["timeoutSeconds"] = 60,
                });
            AssertSuccessResponse(cmdResult);
            using (var cmdDoc = JsonDocument.Parse(cmdResult))
            {
                var data = cmdDoc.RootElement.GetProperty("data");
                var exitCode = data.GetProperty("exitCode").GetInt32();
                var stdout = data.GetProperty("stdout").GetString();
                _output.WriteLine($"Command exit code: {exitCode}, stdout: '{stdout?.Trim()}'");
                exitCode.Should().Be(0, "hostname command should succeed");
                stdout.Should().NotBeNullOrWhiteSpace("hostname should produce output");
            }

            // Step 5: Stop the VM
            _output.WriteLine("Stopping VM...");
            var stopResult = await _dispatcher.DispatchAsync("vm_stop",
                new Dictionary<string, object?> { ["vmId"] = vmId });
            AssertSuccessResponse(stopResult);
            using (var stopDoc = JsonDocument.Parse(stopResult))
            {
                var state = stopDoc.RootElement.GetProperty("data").GetProperty("state").GetString();
                _output.WriteLine($"VM state after stop: {state}");
                state.Should().Be("Off", "VM should be off after stop");
            }

            // Step 6: Destroy the VM
            _output.WriteLine("Destroying VM...");
            var destroyResult = await _dispatcher.DispatchAsync("vm_destroy",
                new Dictionary<string, object?> { ["vmId"] = vmId });
            AssertSuccessResponse(destroyResult);
            using (var destroyDoc = JsonDocument.Parse(destroyResult))
            {
                var destroyed = destroyDoc.RootElement.GetProperty("data").GetProperty("destroyed").GetBoolean();
                _output.WriteLine($"VM destroyed: {destroyed}");
                destroyed.Should().BeTrue("VM should be destroyed successfully");
            }

            // Remove from cleanup list since we already destroyed it
            _createdVmIds.Remove(vmId);
        }
        catch
        {
            // If anything fails, the finally-style cleanup in DisposeAsync
            // will attempt to destroy the VM via the tracked _createdVmIds list.
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. VM List: After Create, the new VM should appear in the list
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a newly created VM appears in the vm_list results.
    ///
    /// Prerequisites: Admin, Hyper-V enabled, HYPERV_MCP_BASE_VHDX env var set.
    /// Timeout: 10 minutes (VM creation + list query + teardown).
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task VmList_AfterCreate_ContainsNewVm()
    {
        if (!CanRunLiveTests())
        {
            SkipTest(nameof(VmList_AfterCreate_ContainsNewVm));
            return;
        }

        var vmName = GenerateVmName("list");
        string? vmId = null;

        try
        {
            // Create a VM
            _output.WriteLine($"Creating VM: {vmName}");
            var createResult = await _dispatcher!.DispatchAsync("vm_create",
                new Dictionary<string, object?> { ["name"] = vmName });
            AssertSuccessResponse(createResult);
            vmId = ExtractAndTrackVmId(createResult);
            _output.WriteLine($"VM created with ID: {vmId}");

            // List all VMs
            _output.WriteLine("Listing VMs...");
            var listResult = await _dispatcher.DispatchAsync("vm_list",
                new Dictionary<string, object?>());
            AssertSuccessResponse(listResult);

            using var listDoc = JsonDocument.Parse(listResult);
            var data = listDoc.RootElement.GetProperty("data");
            var vmsArray = data.GetProperty("vms");
            var count = data.GetProperty("count").GetInt32();
            _output.WriteLine($"Found {count} VMs in list");

            // Find our VM in the list by name or vmId
            var found = false;
            foreach (var vm in vmsArray.EnumerateArray())
            {
                var id = vm.GetProperty("vmId").GetString();
                var name = vm.GetProperty("name").GetString();
                if (string.Equals(id, vmId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, vmName, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    _output.WriteLine($"Found VM in list: {name} ({id})");
                    break;
                }
            }

            found.Should().BeTrue($"VM '{vmName}' ({vmId}) should appear in vm_list results");
        }
        finally
        {
            // Destroy the VM
            if (vmId != null)
            {
                _output.WriteLine($"Cleanup: destroying VM {vmId}");
                try
                {
                    await _dispatcher!.DispatchAsync("vm_destroy",
                        new Dictionary<string, object?> { ["vmId"] = vmId });
                    _createdVmIds.Remove(vmId);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Cleanup warning: {ex.Message}");
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. vm_echo: Simple smoke test — no VM required
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_echo dispatches through the real ToolDispatcher pipeline
    /// and returns the correct message. This is the simplest smoke test that
    /// validates the live pipeline is functional — no VM creation needed.
    ///
    /// Prerequisites: Admin, Hyper-V enabled, HYPERV_MCP_BASE_VHDX env var set.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task VmEcho_LiveServer_ReturnsMessage()
    {
        if (!CanRunLiveTests())
        {
            SkipTest(nameof(VmEcho_LiveServer_ReturnsMessage));
            return;
        }

        const string testMessage = "Hello from LiveE2E test!";

        var result = await _dispatcher!.DispatchAsync("vm_echo",
            new Dictionary<string, object?> { ["message"] = testMessage });

        AssertSuccessResponse(result);
        using var doc = JsonDocument.Parse(result);
        var message = doc.RootElement.GetProperty("data").GetProperty("message").GetString();
        _output.WriteLine($"Echo response: {message}");
        message.Should().Be(testMessage);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. vm_diag: Diagnostics smoke test — no VM required
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_diag dispatches through the real ToolDispatcher pipeline
    /// and returns diagnostic data including dotnet and powershell sections.
    /// No VM creation needed — this tests infrastructure diagnostics.
    ///
    /// Prerequisites: Admin, Hyper-V enabled, HYPERV_MCP_BASE_VHDX env var set.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task VmDiag_LiveServer_ReturnsDiagnostics()
    {
        if (!CanRunLiveTests())
        {
            SkipTest(nameof(VmDiag_LiveServer_ReturnsDiagnostics));
            return;
        }

        var result = await _dispatcher!.DispatchAsync("vm_diag",
            new Dictionary<string, object?>());

        AssertSuccessResponse(result);
        using var doc = JsonDocument.Parse(result);
        var data = doc.RootElement.GetProperty("data");
        _output.WriteLine($"Diag response data properties: {string.Join(", ", data.EnumerateObject().Select(p => p.Name))}");

        // Verify diagnostic sections exist
        data.TryGetProperty("dotnet", out _).Should().BeTrue("diagnostics should include dotnet section");
        data.TryGetProperty("powershell", out _).Should().BeTrue("diagnostics should include powershell section");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. vm_list_images: Image enumeration — no VM required
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_list_images dispatches through the real ToolDispatcher pipeline
    /// and returns at least one image (the configured base VHDX). No VM creation needed.
    ///
    /// Prerequisites: Admin, Hyper-V enabled, HYPERV_MCP_BASE_VHDX env var set.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task VmListImages_LiveServer_ReturnsAvailableImages()
    {
        if (!CanRunLiveTests())
        {
            SkipTest(nameof(VmListImages_LiveServer_ReturnsAvailableImages));
            return;
        }

        var result = await _dispatcher!.DispatchAsync("vm_list_images",
            new Dictionary<string, object?>());

        AssertSuccessResponse(result);
        using var doc = JsonDocument.Parse(result);
        var data = doc.RootElement.GetProperty("data");
        var count = data.GetProperty("count").GetInt32();
        _output.WriteLine($"Found {count} available images");

        count.Should().BeGreaterThanOrEqualTo(1,
            "at least one image should be available (the configured base VHDX)");

        var images = data.GetProperty("images");
        images.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // Log each image found
        foreach (var image in images.EnumerateArray())
        {
            var name = image.GetProperty("name").GetString();
            var path = image.GetProperty("path").GetString();
            _output.WriteLine($"  Image: {name} at {path}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. Full P1 Lifecycle: Create → WaitReady → RunScript → Checkpoint → GetFile → Restart → CleanupOrphans → Destroy
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Exercises all P1 tools in a single comprehensive lifecycle flow:
    /// vm_create → vm_wait_ready → vm_run_script → vm_checkpoint (create/list) →
    /// vm_run_command (create file) → vm_get_file → vm_checkpoint (restore/wait_ready) →
    /// vm_restart → vm_wait_ready → vm_checkpoint (delete) → vm_destroy.
    ///
    /// Prerequisites: Admin, Hyper-V enabled, HYPERV_MCP_BASE_VHDX env var set.
    /// Timeout: 15 minutes (full P1 flow with multiple wait-ready cycles).
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task FullP1Lifecycle_WaitReady_RunScript_Checkpoint_GetFile_Restart()
    {
        if (!CanRunLiveTests())
        {
            SkipTest(nameof(FullP1Lifecycle_WaitReady_RunScript_Checkpoint_GetFile_Restart));
            return;
        }

        var vmName = GenerateVmName("p1-lifecycle");
        string? vmId = null;
        string? tempDir = null;

        try
        {
            // Step 1: vm_create — create a VM
            _output.WriteLine($"Step 1: Creating VM: {vmName}");
            var createResult = await _dispatcher!.DispatchAsync("vm_create",
                new Dictionary<string, object?> { ["name"] = vmName });
            AssertSuccessResponse(createResult);
            vmId = ExtractAndTrackVmId(createResult);
            _output.WriteLine($"VM created with ID: {vmId}");

            // Step 2: vm_wait_ready — wait for VM to be ready (P1 tool, not manual polling helper)
            _output.WriteLine("Step 2: Waiting for VM to be ready via vm_wait_ready...");
            var waitReadyResult = await _dispatcher.DispatchAsync("vm_wait_ready",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["timeoutSeconds"] = 300,
                });
            AssertSuccessResponse(waitReadyResult);
            _output.WriteLine("VM is ready.");

            // Step 3: vm_run_script — execute a multi-line PowerShell script on the guest
            _output.WriteLine("Step 3: Running multi-line PowerShell script on guest...");
            var script = "$hostname = hostname\n$date = Get-Date -Format 'yyyy-MM-dd'\nWrite-Output \"Host: $hostname, Date: $date\"";
            var scriptResult = await _dispatcher.DispatchAsync("vm_run_script",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["script"] = script,
                    ["shell"] = "powershell",
                    ["timeoutSeconds"] = 60,
                });
            AssertSuccessResponse(scriptResult);
            using (var scriptDoc = JsonDocument.Parse(scriptResult))
            {
                var data = scriptDoc.RootElement.GetProperty("data");
                var exitCode = data.GetProperty("exitCode").GetInt32();
                var stdout = data.GetProperty("stdout").GetString();
                _output.WriteLine($"Script exit code: {exitCode}, stdout: '{stdout?.Trim()}'");
                exitCode.Should().Be(0, "PowerShell script should succeed");
                stdout.Should().Contain("Host:", "script output should contain 'Host:'");
            }

            // Step 4: vm_checkpoint (create) — create a checkpoint named "test-checkpoint"
            _output.WriteLine("Step 4: Creating checkpoint 'test-checkpoint'...");
            var cpCreateResult = await _dispatcher.DispatchAsync("vm_checkpoint",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["action"] = "create",
                    ["name"] = "test-checkpoint",
                });
            AssertSuccessResponse(cpCreateResult);
            _output.WriteLine("Checkpoint 'test-checkpoint' created.");

            // Step 5: vm_checkpoint (list) — verify the checkpoint appears in the list
            _output.WriteLine("Step 5: Listing checkpoints to verify 'test-checkpoint' exists...");
            var cpListResult = await _dispatcher.DispatchAsync("vm_checkpoint",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["action"] = "list",
                });
            AssertSuccessResponse(cpListResult);
            using (var cpListDoc = JsonDocument.Parse(cpListResult))
            {
                var data = cpListDoc.RootElement.GetProperty("data");
                var checkpoints = data.GetProperty("checkpoints");
                var found = false;
                foreach (var cp in checkpoints.EnumerateArray())
                {
                    var cpName = cp.GetProperty("name").GetString();
                    _output.WriteLine($"  Checkpoint: {cpName}");
                    if (string.Equals(cpName, "test-checkpoint", StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                    }
                }
                found.Should().BeTrue("checkpoint 'test-checkpoint' should appear in the list");
            }

            // Step 6: vm_run_command — create a test file on the guest
            _output.WriteLine("Step 6: Creating test file on guest via vm_run_command...");
            var createFileResult = await _dispatcher.DispatchAsync("vm_run_command",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["command"] = @"echo test-content > C:\test-file.txt",
                    ["shell"] = "cmd",
                    ["timeoutSeconds"] = 30,
                });
            AssertSuccessResponse(createFileResult);
            _output.WriteLine("Test file created on guest.");

            // Step 7: vm_get_file — retrieve the file from guest to a temp path on host
            _output.WriteLine("Step 7: Retrieving test file from guest via vm_get_file...");
            tempDir = Path.Combine(Path.GetTempPath(), $"hvmcp-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var destPath = Path.Combine(tempDir, "test-file.txt");
            var getFileResult = await _dispatcher.DispatchAsync("vm_get_file",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["sourcePath"] = @"C:\test-file.txt",
                    ["destPath"] = destPath,
                });
            AssertSuccessResponse(getFileResult);
            File.Exists(destPath).Should().BeTrue($"retrieved file should exist at {destPath}");
            _output.WriteLine($"File retrieved to: {destPath}");

            // Step 8: vm_checkpoint (restore) — restore to "test-checkpoint" (undoes file creation)
            _output.WriteLine("Step 8: Restoring checkpoint 'test-checkpoint'...");
            var cpRestoreResult = await _dispatcher.DispatchAsync("vm_checkpoint",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["action"] = "restore",
                    ["name"] = "test-checkpoint",
                });
            AssertSuccessResponse(cpRestoreResult);
            _output.WriteLine("Checkpoint 'test-checkpoint' restored.");

            // Step 9: vm_wait_ready — wait for VM to be ready after restore
            _output.WriteLine("Step 9: Waiting for VM to be ready after checkpoint restore...");
            var waitAfterRestoreResult = await _dispatcher.DispatchAsync("vm_wait_ready",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["timeoutSeconds"] = 300,
                });
            AssertSuccessResponse(waitAfterRestoreResult);
            _output.WriteLine("VM is ready after restore.");

            // Step 10: vm_restart — restart the VM
            _output.WriteLine("Step 10: Restarting VM...");
            var restartResult = await _dispatcher.DispatchAsync("vm_restart",
                new Dictionary<string, object?> { ["vmId"] = vmId });
            AssertSuccessResponse(restartResult);
            _output.WriteLine("VM restarted.");

            // Step 11: vm_wait_ready — wait for VM to be ready after restart
            _output.WriteLine("Step 11: Waiting for VM to be ready after restart...");
            var waitAfterRestartResult = await _dispatcher.DispatchAsync("vm_wait_ready",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["timeoutSeconds"] = 300,
                });
            AssertSuccessResponse(waitAfterRestartResult);
            _output.WriteLine("VM is ready after restart.");

            // Step 12: vm_checkpoint (delete) — delete the "test-checkpoint"
            _output.WriteLine("Step 12: Deleting checkpoint 'test-checkpoint'...");
            var cpDeleteResult = await _dispatcher.DispatchAsync("vm_checkpoint",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["action"] = "delete",
                    ["name"] = "test-checkpoint",
                });
            AssertSuccessResponse(cpDeleteResult);
            _output.WriteLine("Checkpoint 'test-checkpoint' deleted.");

            // Step 13: vm_destroy — destroy the VM
            _output.WriteLine("Step 13: Destroying VM...");
            var destroyResult = await _dispatcher.DispatchAsync("vm_destroy",
                new Dictionary<string, object?> { ["vmId"] = vmId });
            AssertSuccessResponse(destroyResult);
            using (var destroyDoc = JsonDocument.Parse(destroyResult))
            {
                var destroyed = destroyDoc.RootElement.GetProperty("data").GetProperty("destroyed").GetBoolean();
                _output.WriteLine($"VM destroyed: {destroyed}");
                destroyed.Should().BeTrue("VM should be destroyed successfully");
            }

            // Remove from cleanup list since we already destroyed it
            _createdVmIds.Remove(vmId);

            _output.WriteLine("Full P1 lifecycle test completed successfully.");
        }
        finally
        {
            // Step 14: Clean up any temp files created on host
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                    _output.WriteLine($"Cleanup: deleted temp directory {tempDir}");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Cleanup warning: failed to delete temp directory {tempDir}: {ex.Message}");
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. vm_cleanup_orphans: Dry run — no VM required if tagged VMs exist
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_cleanup_orphans with dryRun=true dispatches through the real
    /// ToolDispatcher pipeline and returns a list of orphaned VMs (may be empty).
    /// This test does NOT destroy anything — dryRun is always true.
    ///
    /// Prerequisites: Admin, Hyper-V enabled, HYPERV_MCP_BASE_VHDX env var set.
    /// Timeout: 2 minutes.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task VmCleanupOrphans_DryRun_ReturnsOrphanList()
    {
        if (!CanRunLiveTests())
        {
            SkipTest(nameof(VmCleanupOrphans_DryRun_ReturnsOrphanList));
            return;
        }

        _output.WriteLine("Calling vm_cleanup_orphans with dryRun=true...");
        var result = await _dispatcher!.DispatchAsync("vm_cleanup_orphans",
            new Dictionary<string, object?> { ["dryRun"] = true });

        AssertSuccessResponse(result);
        using var doc = JsonDocument.Parse(result);
        var data = doc.RootElement.GetProperty("data");
        var count = data.GetProperty("count").GetInt32();
        var dryRun = data.GetProperty("dryRun").GetBoolean();
        var action = data.GetProperty("action").GetString();

        _output.WriteLine($"Orphan cleanup result: count={count}, dryRun={dryRun}, action={action}");
        dryRun.Should().BeTrue("dryRun should be true");
        action.Should().Be("detected", "action should be 'detected' for dry run");
        count.Should().BeGreaterThanOrEqualTo(0, "orphan count should be non-negative");

        // Log individual orphans if any exist
        var orphans = data.GetProperty("orphans");
        foreach (var orphan in orphans.EnumerateArray())
        {
            _output.WriteLine($"  Orphan: {orphan}");
        }

        _output.WriteLine($"vm_cleanup_orphans dry run complete — {count} orphan(s) detected.");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. vm_wait_ready: Standalone test — create VM, wait_ready, destroy
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that vm_wait_ready dispatches through the real ToolDispatcher pipeline,
    /// waits for a newly created VM to reach Running state, and returns status data.
    ///
    /// Prerequisites: Admin, Hyper-V enabled, HYPERV_MCP_BASE_VHDX env var set.
    /// Timeout: 10 minutes (VM creation + wait-ready + teardown).
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task VmWaitReady_LiveServer_WaitsForRunningState()
    {
        if (!CanRunLiveTests())
        {
            SkipTest(nameof(VmWaitReady_LiveServer_WaitsForRunningState));
            return;
        }

        var vmName = GenerateVmName("wait-ready");
        string? vmId = null;

        try
        {
            // Create a VM
            _output.WriteLine($"Creating VM: {vmName}");
            var createResult = await _dispatcher!.DispatchAsync("vm_create",
                new Dictionary<string, object?> { ["name"] = vmName });
            AssertSuccessResponse(createResult);
            vmId = ExtractAndTrackVmId(createResult);
            _output.WriteLine($"VM created with ID: {vmId}");

            // Call vm_wait_ready with timeoutSeconds=300
            _output.WriteLine("Calling vm_wait_ready with timeoutSeconds=300...");
            var waitResult = await _dispatcher.DispatchAsync("vm_wait_ready",
                new Dictionary<string, object?>
                {
                    ["vmId"] = vmId,
                    ["timeoutSeconds"] = 300,
                });
            AssertSuccessResponse(waitResult);

            using (var waitDoc = JsonDocument.Parse(waitResult))
            {
                var data = waitDoc.RootElement.GetProperty("data");
                var state = data.GetProperty("state").GetString();
                _output.WriteLine($"vm_wait_ready returned state: {state}");
                state.Should().Be("Running", "VM should be in Running state after wait_ready completes");
            }
        }
        finally
        {
            // Destroy the VM
            if (vmId != null)
            {
                _output.WriteLine($"Cleanup: destroying VM {vmId}");
                try
                {
                    await _dispatcher!.DispatchAsync("vm_destroy",
                        new Dictionary<string, object?> { ["vmId"] = vmId });
                    _createdVmIds.Remove(vmId);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Cleanup warning: {ex.Message}");
                }
            }
        }
    }
}
