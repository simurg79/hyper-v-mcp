using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="HyperVManager"/> with mocked <see cref="IPowerShellExecutor"/>.
/// See /myplans/vm-management/lifecycle/lifecycle-design.md -- VM lifecycle operations.
///
/// These tests verify that HyperVManager:
/// - Composes correct PowerShell scripts for each operation
/// - Parses JSON output from PowerShell into VmInfo objects
/// - Maps Hyper-V integer state enums to string names
/// - Throws appropriate typed exceptions for error conditions
/// - Enforces local-only constraint for Phase 1
///
/// All tests use Moq to mock IPowerShellExecutor so no actual Hyper-V installation is needed.
/// </summary>
// Issue #48 / TI-D5: this class hosts the OS-install allowDump call-site
// verification (OsInstallAsync_PassesAllowDumpFalse_ToPowerShellExecutor) which
// is part of the script-dump diagnostic surface area. The mock IPowerShellExecutor
// means no real env-var or temp-dir mutation occurs here, but membership in the
// ScriptDumpDiagnostic collection keeps the dump-aware test classes serialized
// together as defense in depth (TI-D8).
[Collection("ScriptDumpDiagnostic")]
public class HyperVManagerTests
{
    private readonly Mock<IPowerShellExecutor> _mockExecutor;
    private readonly ServerOptions _options;
    private readonly IHostResolver _hostResolver;
    private readonly ILogger<HyperVManager> _logger;
    private readonly HyperVManager _manager;

    /// <summary>
    /// Standard test VM ID used across tests.
    /// </summary>
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    /// <summary>
    /// Standard test VM name used across tests.
    /// </summary>
    private const string TestVmName = "test-vm";

    /// <summary>
    /// Standard local host ID.
    /// </summary>
    private const string LocalHostId = "local";

    public HyperVManagerTests()
    {
        _mockExecutor = new Mock<IPowerShellExecutor>();
        _options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [LocalHostId] = new HostProfile
                {
                    HostId = LocalHostId,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        _hostResolver = new HostResolver(_options);
        _logger = NullLoggerFactory.Instance.CreateLogger<HyperVManager>();
        _manager = new HyperVManager(_mockExecutor.Object, _hostResolver, _options, _logger, new TestIsoInspector());
    }

    /// <summary>
    /// Builds a manager whose host-profile BaseVhdxPath parent is an actually-existing
    /// directory on disk (a fresh empty temp dir). Required by ST-D7 / Issue #54:
    /// ListImagesAsync now throws ArgumentException → INVALID_PARAMETER when the
    /// configured directory does not exist. Tests that exercise the PS-execution
    /// happy path use this instead of the class-level <c>_manager</c>.
    /// </summary>
    private HyperVManager BuildManagerWithExistingImageDir(out string imageDir)
    {
        imageDir = Path.Combine(Path.GetTempPath(), "hypervmcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imageDir);
        var dummyBaseVhdx = Path.Combine(imageDir, "base.vhdx");

        var options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [LocalHostId] = new HostProfile
                {
                    HostId = LocalHostId,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = dummyBaseVhdx,
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        var resolver = new HostResolver(options);
        return new HyperVManager(_mockExecutor.Object, resolver, options, _logger, new TestIsoInspector());
    }

    // --- Helper Methods -------------------------------------------------

    /// <summary>
    /// Creates a successful <see cref="PowerShellResult"/> with the given JSON stdout.
    /// </summary>
    private static PowerShellResult SuccessResult(string stdout) => new()
    {
        ExitCode = 0,
        Stdout = stdout,
        Stderr = string.Empty,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 100,
    };

    /// <summary>
    /// Creates a failed <see cref="PowerShellResult"/> with the given stderr message.
    /// </summary>
    private static PowerShellResult FailureResult(string stderr) => new()
    {
        ExitCode = 1,
        Stdout = string.Empty,
        Stderr = stderr,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 50,
    };

    /// <summary>
    /// Sample JSON output that PowerShell's ConvertTo-Json would produce for a single VM.
    /// Hyper-V State is an integer enum (2 = Running).
    /// </summary>
    private static string SingleVmJson(
        string id = TestVmId,
        string name = TestVmName,
        int state = 2,
        int cpuCount = 2,
        long memoryMB = 4096,
        double uptimeSeconds = 120.5) =>
        $$"""
        {
            "Id": "{{id}}",
            "Name": "{{name}}",
            "State": {{state}},
            "ProcessorCount": {{cpuCount}},
            "MemoryMB": {{memoryMB}},
            "UptimeSeconds": {{uptimeSeconds}}
        }
        """;

    /// <summary>
    /// Sample JSON array output for multiple VMs.
    /// </summary>
    private static string MultiVmJson() =>
        $$"""
        [
            {
                "Id": "{{TestVmId}}",
                "Name": "vm-1",
                "State": 2,
                "ProcessorCount": 2,
                "MemoryMB": 4096,
                "UptimeSeconds": 120
            },
            {
                "Id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "Name": "vm-2",
                "State": 3,
                "ProcessorCount": 4,
                "MemoryMB": 8192,
                "UptimeSeconds": 0
            }
        ]
        """;

    // --- ListVmsAsync -----------------------------------------------

    /// <summary>
    /// ListVmsAsync should parse a JSON array of VMs and return correctly mapped VmInfo list.
    /// Verifies JSON parsing, state enum mapping, and hostId assignment.
    /// </summary>
    [Fact]
    public async Task ListVmsAsync_ReturnsParsedVms()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(MultiVmJson()));

        var result = await _manager.ListVmsAsync(LocalHostId);

        result.Should().HaveCount(2);
        result[0].VmId.Should().Be(TestVmId);
        result[0].Name.Should().Be("vm-1");
        result[0].State.Should().Be("Running"); // State 2 = Running
        result[0].HostId.Should().Be(LocalHostId);
        result[0].CpuCount.Should().Be(2);
        result[0].MemoryMB.Should().Be(4096);

        result[1].Name.Should().Be("vm-2");
        result[1].State.Should().Be("Off"); // State 3 = Off
        result[1].CpuCount.Should().Be(4);
        result[1].MemoryMB.Should().Be(8192);
    }

    /// <summary>
    /// ListVmsAsync should return empty list when PowerShell returns empty JSON array.
    /// </summary>
    [Fact]
    public async Task ListVmsAsync_EmptyResult_ReturnsEmptyList()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("[]"));

        var result = await _manager.ListVmsAsync(LocalHostId);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// ListVmsAsync with nameFilter should include the filter in the PowerShell script.
    /// PowerShell uses wildcard matching: Get-VM -Name '*filter*'.
    /// </summary>
    [Fact]
    public async Task ListVmsAsync_WithNameFilter_IncludesFilterInScript()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.Is<string>(s => s.Contains("*test*")), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("[]"));

        var result = await _manager.ListVmsAsync(LocalHostId, "test");

        result.Should().BeEmpty();
        _mockExecutor.Verify(
            x => x.ExecuteAsync(It.Is<string>(s => s.Contains("*test*")), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    // --- GetVmStatusAsync -------------------------------------------

    /// <summary>
    /// GetVmStatusAsync should parse single VM JSON and return correctly mapped VmInfo.
    /// </summary>
    [Fact]
    public async Task GetVmStatusAsync_ReturnsVmInfo()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        var result = await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.VmId.Should().Be(TestVmId);
        result.Name.Should().Be(TestVmName);
        result.State.Should().Be("Running"); // State 2 = Running
        result.HostId.Should().Be(LocalHostId);
        result.CpuCount.Should().Be(2);
        result.MemoryMB.Should().Be(4096);
        result.UptimeSeconds.Should().Be(120);
    }

    /// <summary>
    /// GetVmStatusAsync should throw VmNotFoundException when PowerShell stderr contains "not found".
    /// This tests the error handling path that maps PowerShell errors to domain exceptions.
    /// </summary>
    [Fact]
    public async Task GetVmStatusAsync_VmNotFound_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("VM not found: " + TestVmId));

        Func<Task> act = async () => await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        var ex = await act.Should().ThrowAsync<VmNotFoundException>();
        ex.Which.VmId.Should().Be(TestVmId);
        ex.Which.HostId.Should().Be(LocalHostId);
    }

    /// <summary>
    /// Regression test: GetVmStatusAsync should also throw VmNotFoundException
    /// when the error message uses "does not exist" phrasing (alternate Hyper-V error wording).
    /// </summary>
    [Fact]
    public async Task GetVmStatusAsync_VmDoesNotExist_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Hyper-V: The virtual machine does not exist"));

        Func<Task> act = async () => await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<VmNotFoundException>();
    }

    /// <summary>
    /// Regression test: GetVmStatusAsync should throw VmNotFoundException when Hyper-V reports
    /// "unable to find a virtual machine with id" -- a pattern previously missed by HandleError().
    /// See GitHub Issue #18.
    /// </summary>
    [Fact]
    public async Task GetVmStatusAsync_UnableToFindVmWithId_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Get-VM : Hyper-V was unable to find a virtual machine with id \"12345678-1234-1234-1234-123456789abc\"."));

        Func<Task> act = async () => await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<VmNotFoundException>();
    }

    /// <summary>
    /// Regression test: GetVmStatusAsync should throw VmNotFoundException when Hyper-V reports
    /// "unable to find a virtual machine with name" -- a pattern previously missed by HandleError().
    /// See GitHub Issue #18.
    /// </summary>
    [Fact]
    public async Task GetVmStatusAsync_UnableToFindVmWithName_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Get-VM : Hyper-V was unable to find a virtual machine with name \"test-vm\"."));

        Func<Task> act = async () => await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<VmNotFoundException>();
    }

    // --- StartVmAsync -----------------------------------------------

    /// <summary>
    /// StartVmAsync should compose a script with Start-VM and return updated VM info.
    /// </summary>
    [Fact]
    public async Task StartVmAsync_CallsExecutorAndReturnsUpdatedInfo()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(
                It.Is<string>(s => s.Contains("Start-VM") && s.Contains(TestVmId)),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2))); // State 2 = Running

        var result = await _manager.StartVmAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.State.Should().Be("Running");
        _mockExecutor.Verify(
            x => x.ExecuteAsync(It.Is<string>(s => s.Contains("Start-VM")), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    /// <summary>
    /// Issue #19: StartVmAsync should be idempotent -- if the VM is already Running,
    /// the script contains a guard ($vm.State -ne 'Running') that skips the Start-VM
    /// cmdlet and returns the current state directly.
    /// </summary>
    [Fact]
    public async Task StartVmAsync_AlreadyRunning_ReturnsSuccessWithCurrentState()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2))); // State 2 = Running

        var result = await _manager.StartVmAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.State.Should().Be("Running");
        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$vm.State -ne 'Running'",
            "Issue #19: StartVmAsync script must contain idempotency guard that checks if VM is already Running");
    }

    /// <summary>
    /// PB-D11 (PR-B HyperVManager lifecycle dedup): assert the refactored
    /// <c>StartVmAsync</c> emits a PowerShell script containing the load-bearing
    /// projection tokens from the shared <c>VmInfoProjection</c> const. This is the
    /// Gate 9 (Tester) functional canary that the shared projection const flows
    /// correctly into the seven refactored lifecycle methods. See
    /// /myplans/code-cleanup/pr-b-hypervmanager-dedup/pr-b-hypervmanager-dedup-design.md
    /// — PB-D11.
    /// </summary>
    [Fact]
    public async Task StartVmAsync_Script_ContainsVmInfoProjectionTokens()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2)));

        await _manager.StartVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("MemoryStartup/1MB",
            "PB-D11: shared VmInfoProjection const must contribute the 'MemoryStartup/1MB' literal to the emitted script");
        capturedScript.Should().Contain("UptimeSeconds",
            "PB-D11: shared VmInfoProjection const must contribute the 'UptimeSeconds' literal to the emitted script");
        capturedScript.Should().Contain("Select-Object Id, Name, State, ProcessorCount",
            "PB-D11: shared VmInfoProjection const must contribute the 'Select-Object Id, Name, State, ProcessorCount' literal to the emitted script");
    }

    // --- StopVmAsync ------------------------------------------------

    /// <summary>
    /// StopVmAsync with force=true should include -TurnOff flag in the PowerShell script.
    /// Per LF-D3, force=true means hard power-off.
    /// </summary>
    [Fact]
    public async Task StopVmAsync_WithForce_UsesTurnOffFlag()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(
                It.Is<string>(s => s.Contains("-TurnOff")),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 3))); // State 3 = Off

        var result = await _manager.StopVmAsync(LocalHostId, TestVmId, force: true);

        result.Should().NotBeNull();
        result.State.Should().Be("Off");
        _mockExecutor.Verify(
            x => x.ExecuteAsync(It.Is<string>(s => s.Contains("-TurnOff")), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    /// <summary>
    /// StopVmAsync with force=false should NOT include -TurnOff flag (graceful shutdown).
    /// The script should still contain -Force to suppress confirmation prompt.
    /// </summary>
    [Fact]
    public async Task StopVmAsync_WithoutForce_UsesGracefulStop()
    {
        // Capture the script that was passed to the executor.
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 3)));

        var result = await _manager.StopVmAsync(LocalHostId, TestVmId, force: false);

        result.Should().NotBeNull();
        capturedScript.Should().NotBeNull();
        // The stop command line should contain "Stop-VM -Force" but NOT "-TurnOff".
        // We check the specific stop command line, not the whole script (which may have -TurnOff in comments).
        capturedScript!.Should().Contain("Stop-VM -Force");
        capturedScript!.Should().NotContain("Stop-VM -TurnOff");
    }

    /// <summary>
    /// Issue #19: StopVmAsync should be idempotent -- if the VM is already Off,
    /// the script contains a guard ($vm.State -ne 'Off') that skips the Stop-VM
    /// cmdlet and returns the current state directly. Tests force=true path.
    /// </summary>
    [Fact]
    public async Task StopVmAsync_AlreadyOff_WithForce_ReturnsSuccessWithCurrentState()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 3))); // State 3 = Off

        var result = await _manager.StopVmAsync(LocalHostId, TestVmId, force: true);

        result.Should().NotBeNull();
        result.State.Should().Be("Off");
        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$vm.State -ne 'Off'",
            "Issue #19: StopVmAsync script must contain idempotency guard that checks if VM is already Off");
    }

    /// <summary>
    /// Issue #19: StopVmAsync with force=false should also contain the idempotency
    /// guard ($vm.State -ne 'Off') so graceful shutdown is skipped when already off.
    /// </summary>
    [Fact]
    public async Task StopVmAsync_AlreadyOff_WithoutForce_ReturnsSuccessWithCurrentState()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 3))); // State 3 = Off

        var result = await _manager.StopVmAsync(LocalHostId, TestVmId, force: false);

        result.Should().NotBeNull();
        result.State.Should().Be("Off");
        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$vm.State -ne 'Off'",
            "Issue #19: StopVmAsync script must contain idempotency guard that checks if VM is already Off");
    }

    // --- CreateVmAsync ----------------------------------------------

    /// <summary>
    /// CreateVmAsync should compose a create script and return the new VM info.
    /// Verifies that the script includes New-VM, New-VHD, Set-VM, Start-VM commands,
    /// and the hyper-v-mcp tag per LF-D4.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_ReturnsNewVmInfo()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(
                It.Is<string>(s =>
                    s.Contains("New-VM") &&
                    s.Contains("New-VHD") &&
                    s.Contains("hyper-v-mcp:created") &&
                    s.Contains("Start-VM")),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        var result = await _manager.CreateVmAsync(
            LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx",
            cpuCount: 2, memoryMB: 4096);

        result.Should().NotBeNull();
        result.Name.Should().Be(TestVmName);
        result.State.Should().Be("Running");
        result.CpuCount.Should().Be(2);
        result.MemoryMB.Should().Be(4096);
    }

    /// <summary>
    /// CreateVmAsync should use host profile's BaseVhdxPath when no explicit path is given.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_UsesHostProfileBaseVhdxPath()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(
                It.Is<string>(s => s.Contains(@"C:\Base\base.vhdx")),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        var result = await _manager.CreateVmAsync(LocalHostId, TestVmName);

        result.Should().NotBeNull();
        _mockExecutor.Verify(
            x => x.ExecuteAsync(
                It.Is<string>(s => s.Contains(@"C:\Base\base.vhdx")),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    /// <summary>
    /// CreateVmAsync should surface a duplicate-VM failure when PowerShell stderr
    /// contains "already exists". Per Issue #203 / VC-DUP-D5 (which supersedes the
    /// earlier LF-D17 wrapping for this case), name-collision failures throw
    /// <see cref="VmAlreadyExistsException"/> directly WITHOUT rollback wrapping
    /// (no VM was created on this code path, so there is nothing to roll back —
    /// see also <c>LfD19ProbeHit_ShortCircuits_AndSkipsRollbackEntirely</c> in
    /// <c>Issue164VmCreateRollbackTests</c> for the parallel pre-script-probe case).
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_VmAlreadyExists_ThrowsVmAlreadyExistsException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("VM with name 'test-vm' already exists"));

        Func<Task> act = async () => await _manager.CreateVmAsync(LocalHostId, TestVmName);

        var ex = await act.Should().ThrowAsync<VmAlreadyExistsException>(
            "VC-DUP-D5: name-collision throws VmAlreadyExistsException directly, not a rollback wrapper.");
        ex.Which.VmName.Should().Be(TestVmName);
        ex.Which.HostId.Should().Be(LocalHostId);
    }

    /// <summary>
    /// CreateVmAsync should throw InvalidOperationException when no base VHDX path
    /// is available from any source (parameter, env var, or host profile).
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_NoBaseVhdxPath_ThrowsInvalidOperationException()
    {
        // Create a manager with no BaseVhdxPath in host profile.
        var options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [LocalHostId] = new HostProfile
                {
                    HostId = LocalHostId,
                    ComputerName = "localhost",
                    BaseVhdxPath = null,
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var manager = new HyperVManager(_mockExecutor.Object, hostResolver, options, _logger, new TestIsoInspector());

        Func<Task> act = async () => await manager.CreateVmAsync(LocalHostId, TestVmName);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*base VHDX*");
    }

    // --- DestroyVmAsync ---------------------------------------------

    /// <summary>
    /// DestroyVmAsync should compose a script with Stop-VM -TurnOff, Remove-VM, and VHDX cleanup.
    /// Per LF-D3, destroy performs hard power-off, not graceful shutdown.
    /// </summary>
    [Fact]
    public async Task DestroyVmAsync_CallsStopThenRemove()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(
                It.Is<string>(s =>
                    s.Contains("Stop-VM") &&
                    s.Contains("-TurnOff") &&
                    s.Contains("Remove-VM") &&
                    s.Contains("Remove-Item")),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("destroyed"));

        await _manager.DestroyVmAsync(LocalHostId, TestVmId);

        _mockExecutor.Verify(
            x => x.ExecuteAsync(
                It.Is<string>(s => s.Contains("Remove-VM") && s.Contains("-TurnOff")),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    /// <summary>
    /// DestroyVmAsync should throw VmNotFoundException when VM doesn't exist.
    /// </summary>
    [Fact]
    public async Task DestroyVmAsync_VmNotFound_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("VM not found: " + TestVmId));

        Func<Task> act = async () => await _manager.DestroyVmAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<VmNotFoundException>();
    }

    /// <summary>
    /// Issue #25: DestroyVmAsync must clean up the per-VM directory after VHDX deletion
    /// so the storage root does not accumulate empty stub directories. Since the actual
    /// filesystem deletion happens inside the embedded PowerShell script (executed via
    /// the mocked IPowerShellExecutor), the deterministic unit-level assertion is to
    /// capture the script text and verify it contains a recursive Remove-Item targeting
    /// the resolved per-VM directory under the configured storage root.
    ///
    /// Updated for Gate 6 finding #1: per-VM directory is now derived from
    /// "$expectedStorageRoot \ $vm.Name" (Join-Path) instead of $vm.ConfigurationFileLocation,
    /// and the configured StorageRoot must be embedded in the script.
    /// </summary>
    [Fact]
    public async Task DestroyVmAsync_Script_RemovesPerVmDirectoryRecursively()
    {
        const string expectedStorageRoot = @"C:\HyperVMCP\VMs";
        var originalStorageRoot = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT");
        Environment.SetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT", expectedStorageRoot);

        try
        {
            string? capturedScript = null;
            _mockExecutor
                .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
                .ReturnsAsync(SuccessResult("destroyed"));

            await _manager.DestroyVmAsync(LocalHostId, TestVmId);

            capturedScript.Should().NotBeNull("the executor should have been called with the destroy script");

            // Gate 6 finding #1: per-VM directory must be computed from the configured
            // managed storage root and the Hyper-V-reported VM name (Join-Path), not from
            // ConfigurationFileLocation.
            capturedScript.Should().Contain(expectedStorageRoot,
                "Issue #25: the configured StorageRoot must be embedded as $expectedStorageRoot");
            capturedScript.Should().Contain("$expectedStorageRoot",
                "Issue #25: the script must declare an $expectedStorageRoot variable from the host profile");
            capturedScript.Should().Contain("$vmName = $vm.Name",
                "Issue #25: the script should read the authoritative VM name from $vm.Name");
            capturedScript.Should().Contain("$expectedVmDir = Join-Path $expectedStorageRoot $vmName",
                "Issue #25: the per-VM directory must be derived via Join-Path on storageRoot + vmName");

            // The script must recursively remove the resolved per-VM directory after VHDX cleanup.
            // Gate 6 follow-up: -LiteralPath is required to avoid wildcard interpretation of VM names.
            capturedScript.Should().MatchRegex(
                @"Remove-Item\s+-LiteralPath\s+\$resolvedExpected\s+-Recurse\s+-Force",
                "Issue #25: the destroy script must recursively remove the resolved per-VM directory using -LiteralPath");

            // The VHDX cleanup loop must still be present (regression guard) and must use -LiteralPath.
            capturedScript.Should().Contain("Remove-Item -LiteralPath $path -Force",
                "the existing VHDX cleanup loop must be preserved alongside the new directory removal and use -LiteralPath");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT", originalStorageRoot);
        }
    }

    /// <summary>
    /// Gate 6 follow-up (Issue #25): both Test-Path and Remove-Item in the destroy
    /// script's cleanup region must use -LiteralPath instead of -Path (or positional
    /// path) so that VM names containing PowerShell wildcard characters ([, ], *, ?)
    /// are NOT interpreted as glob patterns. This applies to:
    ///   1. The per-VM-directory existence check (Test-Path).
    ///   2. The per-VM-directory removal (Remove-Item).
    ///   3. The VHDX-file removal loop (Remove-Item).
    /// The prior unsafe forms (Test-Path $var, Remove-Item -Path $var) must NOT
    /// appear anywhere in the issue #25 cleanup block.
    /// </summary>
    [Fact]
    public async Task DestroyVmAsync_Script_UsesLiteralPathForCleanupOperations()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("destroyed"));

        await _manager.DestroyVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().NotBeNull();

        // 1. Per-VM-directory existence check must use -LiteralPath.
        capturedScript.Should().MatchRegex(
            @"Test-Path\s+-LiteralPath\s+\$resolvedExpected",
            "Gate 6 follow-up: per-VM directory existence check must use -LiteralPath to avoid wildcard expansion of VM names");

        // 2. Per-VM-directory removal must use -LiteralPath.
        capturedScript.Should().MatchRegex(
            @"Remove-Item\s+-LiteralPath\s+\$resolvedExpected\s+-Recurse\s+-Force",
            "Gate 6 follow-up: per-VM directory removal must use -LiteralPath to avoid wildcard expansion of VM names");

        // 3. VHDX-file removal loop must use -LiteralPath.
        capturedScript.Should().MatchRegex(
            @"Remove-Item\s+-LiteralPath\s+\$path\s+-Force",
            "Gate 6 follow-up: VHDX-file removal loop must use -LiteralPath because VHDX paths can contain VM names with wildcard chars");

        // 4. The prior unsafe patterns must NOT appear anywhere in the script.
        // Catches both `Test-Path $var` (positional) and `Test-Path -Path $var`.
        capturedScript.Should().NotMatchRegex(
            @"Test-Path\s+(-Path\s+)?\$(resolvedExpected|path)\b",
            "Gate 6 follow-up: the unsafe `Test-Path $var` / `Test-Path -Path $var` form must not remain in the issue #25 cleanup block");
        capturedScript.Should().NotMatchRegex(
            @"Remove-Item\s+-Path\s+\$(resolvedExpected|path)\b",
            "Gate 6 follow-up: the unsafe `Remove-Item -Path $var` form must not remain in the issue #25 cleanup block");
    }

    /// <summary>
    /// Issue #25: The per-VM directory removal is best-effort. If Remove-Item fails,
    /// the script should emit a [WARN] stdout line rather than throw, so a successful
    /// destroy stays successful. Verify the try/catch wrapper and the [WARN] output
    /// are present in the embedded script.
    /// </summary>
    [Fact]
    public async Task DestroyVmAsync_Script_PerVmDirectoryRemovalIsBestEffort()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("destroyed"));

        await _manager.DestroyVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("try",
            "Issue #25: the directory-removal block must be wrapped in try/catch");
        capturedScript.Should().Contain("catch",
            "Issue #25: the directory-removal block must catch exceptions to remain best-effort");
        capturedScript.Should().Contain("[WARN] Failed to remove per-VM directory",
            "Issue #25 (Gate 6 finding #3): directory-removal failures must surface as a [WARN] stdout line, not as a hard error");
    }

    /// <summary>
    /// Gate 6 finding #1 (safe-skip): the destroy script must include a safety guard
    /// that compares the resolved expected VM directory against the resolved storage
    /// root and skips deletion (with a [WARN] line) if the path does not live strictly
    /// under the managed root. This protects against any Hyper-V-reported VM name
    /// containing path-traversal characters that could escape the storage root.
    /// </summary>
    [Fact]
    public async Task DestroyVmAsync_Script_HasSafetyGuardForExpectedManagedPath()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("destroyed"));

        await _manager.DestroyVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().NotBeNull();

        // Path normalization for both sides of the comparison.
        capturedScript.Should().Contain("[System.IO.Path]::GetFullPath($expectedVmDir)",
            "Gate 6 finding #1: the expected VM directory must be normalized via [System.IO.Path]::GetFullPath");
        capturedScript.Should().Contain("[System.IO.Path]::GetFullPath($expectedStorageRoot)",
            "Gate 6 finding #1: the storage root must be normalized via [System.IO.Path]::GetFullPath for the prefix check");

        // Case-insensitive comparison.
        capturedScript.Should().Contain("OrdinalIgnoreCase",
            "Gate 6 finding #1: the safety-guard comparison must be case-insensitive (OrdinalIgnoreCase)");

        // The skip-warning must be emitted as a [WARN] stdout line.
        capturedScript.Should().Contain("[WARN] Skipping per-VM directory cleanup",
            "Gate 6 finding #1: a skip must emit a '[WARN] Skipping per-VM directory cleanup' line on stdout");
    }

    /// <summary>
    /// Gate 6 finding #2: the previous VHDX-parent fallback used "$vhdPaths[0]" which
    /// returned the first character (not first path) when $vhdPaths was a scalar string
    /// from a single-disk VM. With the rewrite to derive the directory purely from
    /// StorageRoot + vmName, the VHDX fallback must be removed entirely. Assert the
    /// buggy syntax is gone.
    /// </summary>
    [Fact]
    public async Task DestroyVmAsync_Script_DoesNotUseBuggyVhdPathsFallback()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("destroyed"));

        await _manager.DestroyVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().NotBeNull();

        // The buggy single-disk fallback must be gone. $vhdPaths is still used to drive
        // the per-file Remove-Item loop, but it must NOT be indexed with [0] (which
        // returns the first CHARACTER on a scalar string in PowerShell).
        capturedScript.Should().NotContain("$vhdPaths[0]",
            "Gate 6 finding #2: $vhdPaths[0] returns the first character on a scalar single-path string; the fallback must be removed");

        // The directory derivation must NOT depend on ConfigurationFileLocation anymore;
        // the simpler StorageRoot + vmName derivation replaces it.
        capturedScript.Should().NotContain("ConfigurationFileLocation",
            "Gate 6 finding #1/#2: directory derivation must come from StorageRoot + vmName, not ConfigurationFileLocation");
    }

    /// <summary>
    /// Gate 6 finding #3 (warning observability): Write-Warning may be invisible to the
    /// API caller because the executor does not merge the warning stream and successful
    /// destroy discards captured output. The script must instead use Write-Output
    /// "[WARN] ..." so cleanup messages reliably appear in the executor's captured stdout.
    /// Assert that no Write-Warning calls remain in the cleanup block and that [WARN]
    /// stdout lines are used in their place.
    /// </summary>
    [Fact]
    public async Task DestroyVmAsync_Script_UsesWriteOutputWarnPrefixForCleanupMessages()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("destroyed"));

        await _manager.DestroyVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().NotBeNull();

        // Cleanup messages must use the [WARN]-prefixed Write-Output pattern so they
        // are visible regardless of stream-merging behavior.
        capturedScript.Should().Contain(@"Write-Output ""[WARN] Failed to remove per-VM directory",
            "Gate 6 finding #3: cleanup-failure messages must be emitted via Write-Output \"[WARN] ...\"");
        capturedScript.Should().Contain(@"Write-Output ""[WARN] Skipping per-VM directory cleanup",
            "Gate 6 finding #3: safety-skip messages must be emitted via Write-Output \"[WARN] ...\"");

        // No Write-Warning should remain in the cleanup block (it would be lost on
        // successful destroy because stdout is discarded and warnings are not merged).
        capturedScript.Should().NotContain("Write-Warning",
            "Gate 6 finding #3: Write-Warning is not observable to the caller; use Write-Output \"[WARN] ...\" instead");
    }

    // --- Remote Host Rejection --------------------------------------

    /// <summary>
    /// All methods should throw NotSupportedException for remote hosts in Phase 1.
    /// Remote host support (WinRM) will be added in a future phase.
    /// </summary>
    [Fact]
    public async Task RemoteHost_ThrowsNotSupportedException()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "remote1",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["remote1"] = new HostProfile
                {
                    HostId = "remote1",
                    ComputerName = "hyperv-server.contoso.com", // Not local --> IsLocal = false
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var manager = new HyperVManager(_mockExecutor.Object, hostResolver, options, _logger, new TestIsoInspector());

        // Verify each method throws NotSupportedException for remote hosts.
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.CreateVmAsync("remote1", "test"));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.StartVmAsync("remote1", TestVmId));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.StopVmAsync("remote1", TestVmId));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.DestroyVmAsync("remote1", TestVmId));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.ListVmsAsync("remote1"));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.GetVmStatusAsync("remote1", TestVmId));
    }

    // --- State Mapping ------------------------------------------------

    /// <summary>
    /// Verify that Hyper-V integer state values are mapped to correct string names.
    /// Regression test: ensures the state mapping dictionary covers common states.
    /// </summary>
    [Theory]
    [InlineData(2, "Running")]
    [InlineData(3, "Off")]
    [InlineData(6, "Paused")]
    [InlineData(5, "Saved")]
    [InlineData(9, "Saving")]
    public async Task GetVmStatusAsync_MapsStateEnumToString(int stateValue, string expectedState)
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson(state: stateValue)));

        var result = await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        result.State.Should().Be(expectedState);
    }

    // --- Error Handling -----------------------------------------------

    /// <summary>
    /// Unrecognized errors should throw InvalidOperationException with the stderr content.
    /// This ensures no PowerShell errors are silently swallowed.
    /// </summary>
    [Fact]
    public async Task GenericError_ThrowsInvalidOperationException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Some unexpected PowerShell error occurred"));

        Func<Task> act = async () => await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Some unexpected PowerShell error occurred");
    }

    /// <summary>
    /// ListVmsAsync should handle single-object JSON (not array) from PowerShell.
    /// PowerShell's ConvertTo-Json returns a single object, not an array, when there's exactly one result.
    /// Regression test for this known PowerShell behavior.
    /// </summary>
    [Fact]
    public async Task ListVmsAsync_SingleObject_ReturnsSingleItemList()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        var result = await _manager.ListVmsAsync(LocalHostId);

        result.Should().HaveCount(1);
        result[0].VmId.Should().Be(TestVmId);
        result[0].Name.Should().Be(TestVmName);
    }

    // --- Issue 8: Avoid Parameterless Get-VM -------------------------

    /// <summary>
    /// Issue 8 + WMI workaround (LF-D7): ListVmsAsync with null/empty nameFilter must NOT
    /// generate a bare parameterless "$vms = Get-VM" command. The Hyper-V WMI provider fails
    /// with "Value cannot be null. Parameter name: name" when Get-VM is invoked without
    /// parameters in the MCP server's spawned PowerShell process on Windows 11 build 26200+.
    ///
    /// The script should use "Get-VM -Name '*' -ComputerName localhost" to avoid both the
    /// parameterless bug and the WMI provider null-name bug.
    ///
    /// This test captures the script passed to the executor and asserts it does NOT
    /// contain a bare "$vms = Get-VM" without a -Name parameter following it.
    /// </summary>
    [Fact]
    public async Task ListVmsAsync_NoFilter_AvoidsBareParameterlessGetVm()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("[]"));

        await _manager.ListVmsAsync(LocalHostId);

        capturedScript.Should().NotBeNull("the executor should have been called");

        // The script must NOT contain a bare parameterless "Get-VM" assignment.
        // A bare "$vms = Get-VM" (without -Name or -Id) fails in the MCP process context.
        capturedScript.Should().NotMatchRegex(
            @"\$vms\s*=\s*Get-VM\s*$",
            "the script should not use parameterless Get-VM (Issue #8); " +
            "use 'Get-VM -Name ''*'' -ComputerName localhost' instead to avoid WMI provider null-name error");

        // Positive assertion: the script should contain a parameterized Get-VM form.
        capturedScript.Should().Contain("Get-VM -Name",
            "the script should use 'Get-VM -Name' with a wildcard parameter");

        // WMI workaround assertion: must include -ComputerName localhost.
        capturedScript.Should().Contain("-ComputerName localhost",
            "the script should use '-ComputerName localhost' to work around the WMI null-name bug (LF-D7)");
    }

    /// <summary>
    /// Issue 8 + WMI workaround (LF-D7): ListVmsAsync with null nameFilter should use
    /// "Get-VM -Name '*' -ComputerName localhost" to list all VMs.
    /// This is a positive regression test ensuring the wildcard form with WMI workaround works.
    /// </summary>
    [Fact]
    public async Task ListVmsAsync_NullFilter_UsesWildcardGetVmName()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("[]"));

        await _manager.ListVmsAsync(LocalHostId, nameFilter: null);

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("Get-VM -Name '*' -ComputerName localhost",
            "when no filter is provided, the script should use 'Get-VM -Name ''*'' -ComputerName localhost' " +
            "to enumerate all VMs while avoiding the WMI null-name bug");
    }

    /// <summary>
    /// Issue 8 + WMI workaround (LF-D7) regression: ListVmsAsync with a specific nameFilter
    /// should still generate a script containing the filter wrapped in wildcards, with
    /// -ComputerName localhost. This verifies existing filter behavior is not broken.
    /// </summary>
    [Fact]
    public async Task ListVmsAsync_WithNameFilter_StillUsesWildcardWrappedFilter()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("[]"));

        await _manager.ListVmsAsync(LocalHostId, nameFilter: "web-server");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("*web-server*",
            "the name filter should still be wrapped in wildcards for pattern matching");
        capturedScript.Should().Contain("Get-VM -Name",
            "the script should use parameterized 'Get-VM -Name' even with a filter");
        capturedScript.Should().Contain("-ComputerName localhost",
            "the script should use '-ComputerName localhost' to work around the WMI null-name bug (LF-D7)");
    }

    // --- CreateVmAsync: WMI Workaround ------------------------------

    /// <summary>
    /// WMI workaround (LF-D7): CreateVmAsync should use -ComputerName localhost on
    /// New-VHD to avoid the "Value cannot be null" WMI provider bug.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_UsesComputerNameLocalhostOnNewVhd()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("New-VHD",
            "the create script should contain New-VHD for differencing disk creation");
        capturedScript.Should().Contain("-ComputerName localhost",
            "New-VHD should use '-ComputerName localhost' to work around the WMI null-name bug (LF-D7)");
    }

    // --- ListImagesAsync --------------------------------------------

    /// <summary>
    /// ListImagesAsync should parse a JSON array of images and return correctly mapped ImageInfo list.
    /// </summary>
    [Fact]
    public async Task ListImagesAsync_ReturnsParsedImages()
    {
        var imageJson = """
        [
            {
                "Name": "base",
                "Path": "C:\\HyperVMCP\\Images\\base.vhdx",
                "SizeGB": 4.5,
                "MaxSizeGB": 127.0,
                "VhdType": "Dynamic",
                "ParentPath": null
            },
            {
                "Name": "win11-clean",
                "Path": "C:\\HyperVMCP\\Images\\win11-clean.vhdx",
                "SizeGB": 8.2,
                "MaxSizeGB": 127.0,
                "VhdType": "Dynamic",
                "ParentPath": null
            }
        ]
        """;

        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(imageJson));

        // ST-D7 (Issue #54): the configured image dir must actually exist or
        // ListImagesAsync throws INVALID_PARAMETER before running the PS script.
        var imageDirEnv = Environment.GetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR");
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", null);
        var manager = BuildManagerWithExistingImageDir(out var dir);
        ImageListResult result;
        try { result = await manager.ListImagesAsync(LocalHostId); }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", imageDirEnv);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }

        result.Should().NotBeNull();
        result.Configured.Should().BeTrue("a host-profile BaseVhdxPath was configured (ST-D7).");
        result.Images.Should().HaveCount(2);
        result.Count.Should().Be(2);
        result.Images[0].Name.Should().Be("base");
        result.Images[0].Path.Should().Be(@"C:\HyperVMCP\Images\base.vhdx");
        result.Images[0].SizeGB.Should().Be(4.5);
        result.Images[0].MaxSizeGB.Should().Be(127.0);
        result.Images[0].VhdType.Should().Be("Dynamic");
        result.Images[0].ParentPath.Should().BeNull();

        result.Images[1].Name.Should().Be("win11-clean");
    }

    /// <summary>
    /// ListImagesAsync should return empty list when PowerShell returns empty JSON array.
    /// </summary>
    [Fact]
    public async Task ListImagesAsync_EmptyResult_ReturnsEmptyList()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("[]"));

        var imageDirEnv = Environment.GetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR");
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", null);
        var manager = BuildManagerWithExistingImageDir(out var dir);
        ImageListResult result;
        try { result = await manager.ListImagesAsync(LocalHostId); }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", imageDirEnv);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }

        result.Should().NotBeNull();
        result.Images.Should().BeEmpty();
        result.Count.Should().Be(0);
    }

    /// <summary>
    /// ListImagesAsync should use -ComputerName localhost on Get-VHD per WMI workaround (LF-D7).
    /// </summary>
    [Fact]
    public async Task ListImagesAsync_UsesComputerNameLocalhost()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult("[]"));

        var imageDirEnv = Environment.GetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR");
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", null);
        var manager = BuildManagerWithExistingImageDir(out var dir);
        try { await manager.ListImagesAsync(LocalHostId); }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", imageDirEnv);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("Get-VHD",
            "the list images script should use Get-VHD for VHDX metadata");
        capturedScript.Should().Contain("-ComputerName localhost",
            "Get-VHD should use '-ComputerName localhost' to work around the WMI null-name bug (LF-D7)");
    }

    /// <summary>
    /// ListImagesAsync (ST-D7 / Issue #54) should NOT throw when no image directory
    /// is configured — instead it returns a successful envelope with
    /// <c>Configured=false</c>, empty <c>Images</c>, and a populated <c>Hint</c>
    /// describing how to enable enumeration. This was previously a hard
    /// InvalidOperationException; the soft "unconfigured" state is the new contract.
    /// See /myplans/vm-management/storage/storage-design.md — ST-D7.
    /// See https://github.com/simurg79/hyper-v-mcp-server/issues/54.
    /// </summary>
    [Fact]
    public async Task ListImagesAsync_NoImageDir_ReturnsUnconfiguredEnvelope()
    {
        // Snapshot + clear the env vars so the host-profile null path is the only resolution.
        var imageDirEnv = Environment.GetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR");
        var baseVhdxEnv = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX");
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", null);
        Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", null);
        try
        {
            // Create a manager with no BaseVhdxPath in host profile (and no env var set).
            var options = new ServerOptions
            {
                DefaultHostId = LocalHostId,
                Hosts = new Dictionary<string, HostProfile>
                {
                    [LocalHostId] = new HostProfile
                    {
                        HostId = LocalHostId,
                        ComputerName = "localhost",
                        BaseVhdxPath = null,
                        StorageRoot = @"C:\HyperVMCP\VMs",
                    },
                },
            };
            var hostResolver = new HostResolver(options);
            var manager = new HyperVManager(_mockExecutor.Object, hostResolver, options, _logger, new TestIsoInspector());

            var result = await manager.ListImagesAsync(LocalHostId);

            result.Should().NotBeNull();
            result.Configured.Should().BeFalse(
                "no image directory source was configured (ST-D7 unconfigured soft success).");
            result.Images.Should().BeEmpty();
            result.Count.Should().Be(0);
            result.ImageDir.Should().BeNull();
            result.Hint.Should().NotBeNullOrWhiteSpace(
                "ST-D7 requires an operator-facing hint when unconfigured.");

            // Manager must NOT call into the PowerShell executor when unconfigured.
            _mockExecutor.Verify(
                x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
                Times.Never,
                "unconfigured short-circuit must skip the PS enumeration script.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", imageDirEnv);
            Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", baseVhdxEnv);
        }
    }

    // --- RestartVmAsync ----------------------------------------------

    /// <summary>
    /// RestartVmAsync should compose a script containing both Stop-VM and Start-VM commands.
    /// Restart is an atomic stop + start operation.
    /// </summary>
    [Fact]
    public async Task RestartVm_Calls_PowerShell_With_StopAndStart()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2))); // State 2 = Running

        await _manager.RestartVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().NotBeNull("the executor should have been called");
        capturedScript.Should().Contain("Stop-VM",
            "restart script must contain Stop-VM for the stop phase");
        capturedScript.Should().Contain("Start-VM",
            "restart script must contain Start-VM for the start phase");
        capturedScript.Should().Contain(TestVmId,
            "restart script must reference the target VM ID");
    }

    /// <summary>
    /// RestartVmAsync should parse JSON output and return correctly mapped VmInfo.
    /// </summary>
    [Fact]
    public async Task RestartVm_Returns_VmInfo_On_Success()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(
                It.Is<string>(s => s.Contains("Stop-VM") && s.Contains("Start-VM")),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2))); // State 2 = Running

        var result = await _manager.RestartVmAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.VmId.Should().Be(TestVmId);
        result.State.Should().Be("Running");
        result.HostId.Should().Be(LocalHostId);
    }

    /// <summary>
    /// RestartVmAsync should throw VmNotFoundException when the VM doesn't exist.
    /// </summary>
    [Fact]
    public async Task RestartVm_Throws_VmNotFound_On_MissingVm()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("VM not found: " + TestVmId));

        Func<Task> act = async () => await _manager.RestartVmAsync(LocalHostId, TestVmId);

        var ex = await act.Should().ThrowAsync<VmNotFoundException>();
        ex.Which.VmId.Should().Be(TestVmId);
        ex.Which.HostId.Should().Be(LocalHostId);
    }

    /// <summary>
    /// RestartVmAsync should throw NotSupportedException for remote hosts in Phase 1.
    /// </summary>
    [Fact]
    public async Task RestartVm_RejectsRemoteHost()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "remote1",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["remote1"] = new HostProfile
                {
                    HostId = "remote1",
                    ComputerName = "hyperv-server.contoso.com",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var manager = new HyperVManager(_mockExecutor.Object, hostResolver, options, _logger, new TestIsoInspector());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.RestartVmAsync("remote1", TestVmId));
    }

    // --- WaitForReadyAsync -------------------------------------------

    /// <summary>
    /// WaitForReadyAsync should return VmInfo when the VM is ready (Running + heartbeat OK).
    /// </summary>
    [Fact]
    public async Task WaitForReady_Returns_When_Vm_Is_Ready()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2))); // State 2 = Running

        var result = await _manager.WaitForReadyAsync(LocalHostId, TestVmId, timeoutSeconds: 60);

        result.Should().NotBeNull();
        result.VmId.Should().Be(TestVmId);
        result.State.Should().Be("Running");
        result.HostId.Should().Be(LocalHostId);
    }

    /// <summary>
    /// WaitForReadyAsync should throw TimeoutException when the VM doesn't become ready
    /// within the specified timeout.
    /// </summary>
    [Fact]
    public async Task WaitForReady_Throws_TimeoutException_On_Timeout()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Timed out waiting for VM '" + TestVmId + "' to become ready after 10 seconds"));

        Func<Task> act = async () => await _manager.WaitForReadyAsync(LocalHostId, TestVmId, timeoutSeconds: 10);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    /// <summary>
    /// WaitForReadyAsync should throw NotSupportedException for remote hosts in Phase 1.
    /// </summary>
    [Fact]
    public async Task WaitForReady_RejectsRemoteHost()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "remote1",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["remote1"] = new HostProfile
                {
                    HostId = "remote1",
                    ComputerName = "hyperv-server.contoso.com",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var manager = new HyperVManager(_mockExecutor.Object, hostResolver, options, _logger, new TestIsoInspector());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.WaitForReadyAsync("remote1", TestVmId));
    }

    // --- CleanupOrphansAsync -----------------------------------------

    /// <summary>
    /// CleanupOrphansAsync with dryRun=true should return orphan list without destroying VMs.
    /// Verifies the script passes $true for dryRun flag.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_DryRun_Returns_OrphanList_Without_Destroying()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(MultiVmJson()));

        var result = await _manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().HaveCount(2);
        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$true",
            "dryRun=true should pass $true to the script");
    }

    /// <summary>
    /// CleanupOrphansAsync with dryRun=false should pass $false to the destroy script.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_Execute_Destroys_Orphans()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(MultiVmJson()));

        var result = await _manager.CleanupOrphansAsync(LocalHostId, dryRun: false);

        result.Should().HaveCount(2);
        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$false",
            "dryRun=false should pass $false to the script, enabling orphan destruction");
    }

    /// <summary>
    /// CleanupOrphansAsync should return empty list when no orphans are found.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_Returns_Empty_When_No_Orphans()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("[]"));

        var result = await _manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// CleanupOrphansAsync should throw NotSupportedException for remote hosts in Phase 1.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_RejectsRemoteHost()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "remote1",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["remote1"] = new HostProfile
                {
                    HostId = "remote1",
                    ComputerName = "hyperv-server.contoso.com",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var manager = new HyperVManager(_mockExecutor.Object, hostResolver, options, _logger, new TestIsoInspector());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.CleanupOrphansAsync("remote1"));
    }
    // --- PauseVmAsync ---

    [Fact]
    public async Task PauseVmAsync_CallsExecutorWithSuspendVm()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 6)));

        var result = await _manager.PauseVmAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.State.Should().Be("Paused");
        capturedScript.Should().Contain("Suspend-VM");
        capturedScript.Should().Contain(TestVmId);
        capturedScript.Should().Contain("-ComputerName localhost");
    }

    [Fact]
    public async Task PauseVmAsync_IncludesImportModule()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 6)));

        await _manager.PauseVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().Contain("Import-Module Hyper-V");
    }

    [Fact]
    public async Task PauseVmAsync_ValidatesRunningState()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 6)));

        await _manager.PauseVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().Contain("Running");
    }

    [Fact]
    public async Task PauseVmAsync_VmNotFound_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("VM not found: " + TestVmId));

        Func<Task> act = async () => await _manager.PauseVmAsync(LocalHostId, TestVmId);

        var ex = await act.Should().ThrowAsync<VmNotFoundException>();
        ex.Which.VmId.Should().Be(TestVmId);
    }

    [Fact]
    public async Task PauseVmAsync_RejectsRemoteHost()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "remote1",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["remote1"] = new HostProfile
                {
                    HostId = "remote1",
                    ComputerName = "hyperv-server.contoso.com",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var manager = new HyperVManager(_mockExecutor.Object, hostResolver, options, _logger, new TestIsoInspector());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.PauseVmAsync("remote1", TestVmId));
    }

    // --- ResumeVmAsync ---

    [Fact]
    public async Task ResumeVmAsync_CallsExecutorWithResumeVm()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2)));

        var result = await _manager.ResumeVmAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.State.Should().Be("Running");
        capturedScript.Should().Contain("Resume-VM");
        capturedScript.Should().Contain(TestVmId);
        capturedScript.Should().Contain("-ComputerName localhost");
    }

    [Fact]
    public async Task ResumeVmAsync_IncludesImportModule()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2)));

        await _manager.ResumeVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().Contain("Import-Module Hyper-V");
    }

    [Fact]
    public async Task ResumeVmAsync_ValidatesPausedState()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2)));

        await _manager.ResumeVmAsync(LocalHostId, TestVmId);

        capturedScript.Should().Contain("Paused");
        capturedScript.Should().Contain("Saved");
    }

    [Fact]
    public async Task ResumeVmAsync_ValidatesSavedStateAccepted()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2)));

        var result = await _manager.ResumeVmAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.State.Should().Be("Running");
        // The state guard in the script should accept 'Saved' state
        capturedScript.Should().Contain("'Saved'",
            "the resume script state guard must accept Saved state since Suspend-VM produces Saved VMs");
    }

    [Fact]
    public async Task ResumeVmAsync_VmNotFound_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("VM not found: " + TestVmId));

        Func<Task> act = async () => await _manager.ResumeVmAsync(LocalHostId, TestVmId);

        var ex = await act.Should().ThrowAsync<VmNotFoundException>();
        ex.Which.VmId.Should().Be(TestVmId);
    }

    [Fact]
    public async Task ResumeVmAsync_RejectsRemoteHost()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "remote1",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["remote1"] = new HostProfile
                {
                    HostId = "remote1",
                    ComputerName = "hyperv-server.contoso.com",
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                },
            },
        };
        var hostResolver = new HostResolver(options);
        var manager = new HyperVManager(_mockExecutor.Object, hostResolver, options, _logger, new TestIsoInspector());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.ResumeVmAsync("remote1", TestVmId));
    }

    // --- CreateVmAsync: autoStart parameter (Issue #24) ----------------

    /// <summary>
    /// Issue #24: CreateVmAsync with autoStart=true (non-default, explicitly passed) should include
    /// "if ($autoStart) { Start-VM" in the script with $autoStart = $true.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_AutoStartTrue_ScriptContainsStartVm()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx",
            cpuCount: 2, memoryMB: 4096, autoStart: true);

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$autoStart = $true",
            "Issue #24: autoStart=true must set $autoStart = $true in the PowerShell script");
        capturedScript.Should().Contain("Start-VM",
            "Issue #24: the script must contain Start-VM for the conditional start");
    }

    /// <summary>
    /// Issue #24: CreateVmAsync with autoStart=false should set $autoStart = $false
    /// so the conditional "if ($autoStart) { Start-VM ... }" is skipped.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_AutoStartFalse_ScriptSetsAutoStartFalse()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 3))); // State 3 = Off (not started)

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx",
            cpuCount: 2, memoryMB: 4096, autoStart: false);

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$autoStart = $false",
            "Issue #24: autoStart=false must set $autoStart = $false in the PowerShell script");
        capturedScript.Should().NotContain("$autoStart = $true",
            "Issue #24: autoStart=false must NOT set $autoStart = $true");
    }

    /// <summary>
    /// Issue #39: CreateVmAsync default (no autoStart specified) should behave
    /// as autoStart=false -- the script should contain $autoStart = $false.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_DefaultAutoStart_IsFalse()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        // Call without specifying autoStart -- should default to false
        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$autoStart = $false",
            "Issue #39: default autoStart must be false");
    }

    // --- CreateVmAsync: Base VHDX Mutation Guard (Issue #23) ----------------

    /// <summary>
    /// Issue #23 (ADR-4 / ST-D1): CreateVmAsync script must set the ReadOnly attribute
    /// on the base VHDX before creating the differencing disk.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_Script_ContainsBaseVhdxReadOnlyGuard()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull("the executor should have been called with the create script");
        capturedScript.Should().Contain("Set-ItemProperty -LiteralPath",
            "Issue #23: the script must call Set-ItemProperty -LiteralPath to set the ReadOnly flag on the base VHDX");
        capturedScript.Should().Contain("IsReadOnly -Value $true",
            "Issue #23: the script must set IsReadOnly to $true to guard the base VHDX against mutation");
    }

    /// <summary>
    /// Issue #23 + Issue #164 / ST-D6a: SHA-256 pre/post hashing of the base VHDX
    /// is no longer performed inline by the PowerShell script — it is owned by the
    /// host-side <see cref="IBaseImageHashCache"/> (so the dual-hash cost is paid
    /// at most once per (path, stat-tuple, TTL) and cannot blow past the 60 s RPC
    /// budget). The original Issue #23 mutation-guard regression is now covered by:
    ///   • <see cref="BaseImageHashCacheTests"/> — pre/post hash compute + cache contract.
    ///   • <see cref="CreateVmAsync_Script_ContainsBaseVhdxReadOnlyGuard"/> — ReadOnly attribute.
    ///   • <see cref="BuildBaseVhdxGuardScript_ContainsExpectedCommands"/> — script asserts
    ///     ReadOnly enforcement and explicitly forbids inline <c>Get-FileHash</c>.
    /// This test verifies that the script no longer contains the inline-hash
    /// commands (regression guard: Issue #23 must not be re-introduced inline).
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_Script_DoesNotContainInlinePreHashComputation()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().NotContain("Get-FileHash",
            "ST-D6a: SHA-256 hashing is now host-side via IBaseImageHashCache; inline Get-FileHash must NOT appear");
        capturedScript.Should().NotContain("$preHash",
            "ST-D6a: inline $preHash must NOT appear — host-side cache owns pre-hash");
    }

    /// <summary>
    /// Issue #23 + Issue #164 / ST-D6a: post-operation hash verification is now
    /// host-side (via <see cref="IBaseImageHashCache"/>). This test guards against
    /// the inline pattern being re-introduced. The host-side mutation-detection
    /// behavior (cheap stat-tuple + cached hash) is covered by
    /// <see cref="BaseImageHashCacheTests"/>.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_Script_DoesNotContainInlinePostHashVerification()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().NotContain("$postHash",
            "ST-D6a: inline $postHash must NOT appear — host-side cache owns post-hash");
        capturedScript.Should().NotContain("CRITICAL: Base VHDX was mutated",
            "ST-D6a: inline post-hash mutation-detection must NOT appear in the PS script");
    }

    /// <summary>
    /// Issue #23 (ADR-4 / ST-D1) refined by Issue #164 / ST-D6a:
    /// <c>BuildBaseVhdxGuardScript</c> must emit ONLY ReadOnly-attribute
    /// management (Get-ItemProperty / Set-ItemProperty on <c>IsReadOnly</c>).
    /// Inline SHA-256 hashing has been removed and is now owned by the host-side
    /// <see cref="IBaseImageHashCache"/> so the cache cannot be bypassed by
    /// pipeline cancellation and the dual-hash cost is paid at most once per
    /// (path, stat-tuple, TTL).
    /// </summary>
    [Fact]
    public async Task BuildBaseVhdxGuardScript_ContainsExpectedCommands()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull("the executor should have been called with the create script");

        // The guard script comment marker must be present (ReadOnly enforcement only).
        capturedScript.Should().Contain("Base VHDX mutation guard",
            "Issue #23: the guard script comment from BuildBaseVhdxGuardScript must be in the composed script");

        // ReadOnly-attribute management must be present.
        capturedScript.Should().Contain("Get-ItemProperty -LiteralPath",
            "ST-D6a: the guard script must check the IsReadOnly attribute via Get-ItemProperty -LiteralPath");
        capturedScript.Should().Contain("Set-ItemProperty -LiteralPath",
            "ST-D6a: the guard script must set the IsReadOnly attribute via Set-ItemProperty -LiteralPath");
        capturedScript.Should().Contain("IsReadOnly",
            "ST-D6a: the guard script must reference the IsReadOnly attribute");

        // The base VHDX path must be embedded in the guard commands.
        capturedScript.Should().Contain(@"C:\Base\base.vhdx",
            "Issue #23: the base VHDX path must be embedded in the guard script");

        // Negative regression: inline hashing must NOT be in the guard script.
        capturedScript.Should().NotContain("Get-FileHash",
            "ST-D6a: inline Get-FileHash is forbidden — SHA-256 is now host-side via IBaseImageHashCache");
    }
    // --- CreateVmAsync: Rollback & Guard Coverage (Iteration 4) --------

    /// <summary>
    /// Iteration 4 Finding 2: Verify ExecuteAsync is called with timeoutSeconds: 600
    /// to accommodate dual SHA-256 hashing of the base VHDX.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_Script_Uses600SecondTimeout()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.Is<int>(t => t == 600), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        _mockExecutor.Verify(
            x => x.ExecuteAsync(It.IsAny<string>(), It.Is<int>(t => t == 600), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once,
            "CreateVmAsync must use timeoutSeconds: 600 for dual SHA-256 hashing");
    }

    /// <summary>
    /// Issue #164 / LF-D17: Inline rollback has been removed from the primary
    /// CreateVmAsync script — rollback is now run host-side via
    /// <c>RunCreateRollbackAsync</c> under a detached <see cref="CancellationTokenSource"/>
    /// so it survives inbound-CT cancellation. The original Iteration 4 invariant
    /// ("cleanup uses -LiteralPath to avoid wildcard expansion") is preserved by
    /// re-targeting this assertion at the standalone rollback script: the
    /// rollback PowerShell must still address VHDX and per-VM-directory paths
    /// via <c>-LiteralPath</c>. We capture the second ExecuteAsync invocation
    /// (the rollback call) by forcing the primary call to fail.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_RollbackScript_ContainsLiteralPathInCleanup()
    {
        var capturedScripts = new List<string>();
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScripts.Add(s))
            .ReturnsAsync(FailureResult("New-VM : simulated failure to trigger rollback"));

        // Trigger primary failure ⇒ host-side rollback script runs as the second call.
        try { await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx"); }
        catch (VmCreateRollbackException) { /* expected */ }

        capturedScripts.Should().HaveCountGreaterThanOrEqualTo(2,
            "LF-D17: primary failure must be followed by a host-side rollback PS call");

        var rollbackScript = capturedScripts[1];
        rollbackScript.Should().Contain("-LiteralPath",
            "LF-D17 + Iteration 4: rollback cleanup must use -LiteralPath to avoid wildcard expansion of VM names / paths");
    }

    /// <summary>
    /// Iteration 4 Finding 2: Verify the script contains Get-ItemProperty -LiteralPath
    /// for the conditional ReadOnly check on the base VHDX.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_Script_ContainsConditionalReadOnly()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("Get-ItemProperty -LiteralPath",
            "Iteration 4: the script must use Get-ItemProperty -LiteralPath for the conditional ReadOnly check");
    }

    /// <summary>
    /// Issue #164 / LF-D17: Inline rollback has been REMOVED from the primary
    /// CreateVmAsync script (the previous "try { …guard… New-VM …; } catch { Remove-VM }"
    /// boundary no longer exists). Rollback is now performed host-side via
    /// <c>RunCreateRollbackAsync</c> against a detached
    /// <see cref="CancellationTokenSource"/> so it survives inbound-CT cancellation.
    /// The original Iteration 5 invariant ("guard section is inside the rollback
    /// boundary") is preserved at the host-side level by
    /// <see cref="Issue164VmCreateRollbackTests"/>, which verifies that rollback
    /// always runs after primary failure (including cancellation) and reports
    /// structured residual-artifact info.
    ///
    /// This test now guards the negative invariant: the primary script must NOT
    /// contain the obsolete inline rollback (<c>Remove-VM</c>) nor the
    /// $postHash / $newVhdError variables that drove it.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_PrimaryScript_DoesNotContainInlineRollback()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().NotContain("Remove-VM",
            "LF-D17: inline rollback (Remove-VM) must NOT appear in the primary create script — rollback is host-side via RunCreateRollbackAsync");
        capturedScript.Should().NotContain("$newVhdError",
            "LF-D17: inline rollback's $newVhdError tracking variable must be gone");
        capturedScript.Should().NotContain("$postHash",
            "ST-D6a: inline post-hash variable must be gone (host-side cache owns it)");
    }

    // ─── OS-Install: allowDump call-site verification ────────────────────
    // See /myplans/operational/script-dump/script-dump-design.md — Decision SD-D4 and §5 (non-goal #6):
    // The OS-install code path must call IPowerShellExecutor.ExecuteAsync with
    // allowDump=false so the script-dump diagnostic cannot leak the embedded admin
    // password (variable-backed credential + unattended-XML <Password>) even if the
    // operator has the HYPERV_MCP_DUMP_PS_SCRIPTS env var set.

    /// <summary>
    /// Executor-level test (<see cref="PowerShellExecutorTests.ExecuteAsync_WhenOsInstallScript_NeverWritesDump_EvenIfEnvVarSet"/>)
    /// proves the executor honors <c>allowDump: false</c>; this test proves the
    /// <see cref="HyperVManager.OsInstallAsync"/> call-site actually passes it.
    /// Uses Moq verification on the bool argument and keeps the test narrow:
    /// the OsInstall script returns a minimal success envelope so OsInstallAsync
    /// can complete without touching live Hyper-V.
    /// </summary>
    [Fact]
    public async Task OsInstallAsync_PassesAllowDumpFalse_ToPowerShellExecutor()
    {
        // Minimal success envelope matching what the OS-install PS script emits.
        // See HyperVManager.ParseOsInstallResult and the success branch around
        // /src/HyperV.Mcp.Server/Infrastructure/HyperVManager.cs:1689.
        const string successJson = """
            {
                "success": true,
                "data": {
                    "VmId": "12345678-1234-1234-1234-123456789abc",
                    "Name": "test-vm",
                    "State": 2,
                    "ProcessorCount": 4,
                    "MemoryMB": 8192,
                    "InstallationDurationSeconds": 600,
                    "BootstrapDurationSeconds": 30,
                    "TotalDurationSeconds": 630
                }
            }
            """;

        _mockExecutor
            .Setup(x => x.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(successJson));

        // Issue #97: OsInstallAsync now performs C#-side ISO existence + OS-family
        // preflight before any PS execution. Use a real temp file so existence check
        // passes; TestIsoInspector (default ctor) reports it as Windows.
        var tempIso = Path.Combine(Path.GetTempPath(),
            "issue97-allowdump-" + Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllBytes(tempIso, new byte[] { 0 });
        try
        {
            await _manager.OsInstallAsync(
                hostId: LocalHostId,
                name: "test-vm",
                isoPath: tempIso,
                adminPassword: "P@ssw0rd!",
                cpuCount: 4,
                memoryMB: 8192,
                diskSizeGB: 127,
                timeoutMinutes: 60);
        }
        finally
        {
            try { File.Delete(tempIso); } catch { /* best-effort */ }
        }

        // Verify OS-install path explicitly passed allowDump: false (SD-D4).
        _mockExecutor.Verify(
            x => x.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                /* allowDump: */ false),
            Times.Once,
            "OsInstallAsync must pass allowDump: false to suppress script-dump diagnostic for the OS-install path");

        // And it must NOT have called ExecuteAsync with allowDump: true on this path.
        _mockExecutor.Verify(
            x => x.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                /* allowDump: */ true),
            Times.Never,
            "OsInstallAsync must never let the dump-enabled overload through (would leak admin password)");
    }
}