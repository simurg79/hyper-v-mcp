using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>Mocked IPowerShellExecutor tests cover script composition, JSON/state parsing, typed errors and Phase 1 local-only behavior
/// without Hyper-V.</summary>
// OS-install allowDump verification shares the ScriptDumpDiagnostic collection to serialize dump-aware tests as defense in depth, although
// its executor mock does not mutate environment variables or temp directories.
[Collection("ScriptDumpDiagnostic")]
public class HyperVManagerTests
{
    private readonly Mock<IPowerShellExecutor> _mockExecutor;
    private readonly ServerOptions _options;
    private readonly IHostResolver _hostResolver;
    private readonly ILogger<HyperVManager> _logger;
    private readonly HyperVManager _manager;

    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    private const string TestVmName = "test-vm";

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

    /// <summary>Use an existing empty temp directory for BaseVhdxPath: a missing configured image directory throws ArgumentException
    /// (INVALID_PARAMETER) before PowerShell execution.</summary>
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


    private static PowerShellResult SuccessResult(string stdout) => new()
    {
        ExitCode = 0,
        Stdout = stdout,
        Stderr = string.Empty,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 100,
    };

    private static PowerShellResult FailureResult(string stderr) => new()
    {
        ExitCode = 1,
        Stdout = string.Empty,
        Stderr = stderr,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 50,
    };

    private static string SingleVmJsonWithStateName(
        string stateName,
        string id = TestVmId,
        string name = TestVmName,
        int cpuCount = 2,
        long memoryMB = 4096,
        double uptimeSeconds = 120.5) =>
        $$"""
        {
            "Id": "{{id}}",
            "Name": "{{name}}",
            "State": "{{stateName}}",
            "ProcessorCount": {{cpuCount}},
            "MemoryMB": {{memoryMB}},
            "UptimeSeconds": {{uptimeSeconds}}
        }
        """;

    /// <summary>Numeric State exercises the retained legacy deserialization fallback; production emits enum names.</summary>
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

    [Fact]
    public async Task ListVmsAsync_EmptyResult_ReturnsEmptyList()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("[]"));

        var result = await _manager.ListVmsAsync(LocalHostId);

        result.Should().BeEmpty();
    }

    /// <summary>PowerShell name filters use wildcard matching: Get-VM -Name '*filter*'.</summary>
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

    [Fact]
    public async Task GetVmStatusAsync_VmDoesNotExist_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Hyper-V: The virtual machine does not exist"));

        Func<Task> act = async () => await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<VmNotFoundException>();
    }

    /// <summary>Alternate Hyper-V "unable to find a virtual machine with id" wording was previously missed by HandleError().</summary>
    [Fact]
    public async Task GetVmStatusAsync_UnableToFindVmWithId_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Get-VM : Hyper-V was unable to find a virtual machine with id \"12345678-1234-1234-1234-123456789abc\"."));

        Func<Task> act = async () => await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<VmNotFoundException>();
    }

    /// <summary>Alternate Hyper-V "unable to find a virtual machine with name" wording was previously missed by HandleError().</summary>
    [Fact]
    public async Task GetVmStatusAsync_UnableToFindVmWithName_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Get-VM : Hyper-V was unable to find a virtual machine with name \"test-vm\"."));

        Func<Task> act = async () => await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<VmNotFoundException>();
    }


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

    /// <summary>StartVmAsync is the functional canary that shared VmInfoProjection tokens reach the seven refactored lifecycle
    /// methods.</summary>
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
        capturedScript.Should().Contain("Select-Object Id, Name, ProcessorCount",
            "the shared VmInfoProjection const must contribute the Select-Object literal to the emitted script");
        capturedScript.Should().Contain("@{N='State';E={[string]$_.State}}",
            "the shared projection must stringify State so no lifecycle method emits a bare ordinal");
    }


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

    /// <summary>Graceful stop still needs -Force to suppress confirmation, but not -TurnOff.</summary>
    [Fact]
    public async Task StopVmAsync_WithoutForce_UsesGracefulStop()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 3)));

        var result = await _manager.StopVmAsync(LocalHostId, TestVmId, force: false);

        result.Should().NotBeNull();
        capturedScript.Should().NotBeNull();
        // Inspect only the stop command: comments elsewhere may contain -TurnOff.
        capturedScript!.Should().Contain("Stop-VM -Force");
        capturedScript!.Should().NotContain("Stop-VM -TurnOff");
    }

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

    /// <summary>Name collisions throw VmAlreadyExistsException without rollback wrapping: no VM was created. Issue164VmCreateRollbackTests
    /// covers the parallel pre-script-probe case.</summary>
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

    [Fact]
    public async Task CreateVmAsync_NoBaseVhdxPath_ThrowsInvalidOperationException()
    {
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

    [Fact]
    public async Task DestroyVmAsync_VmNotFound_ThrowsVmNotFoundException()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("VM not found: " + TestVmId));

        Func<Task> act = async () => await _manager.DestroyVmAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<VmNotFoundException>();
    }

    /// <summary>Destroy removes the per-VM directory after VHDX cleanup to avoid empty stubs. With a mocked executor, inspect recursive
    /// Remove-Item targeting configured StorageRoot joined with vm.Name, not ConfigurationFileLocation.</summary>
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

            capturedScript.Should().Contain(expectedStorageRoot,
                "Issue #25: the configured StorageRoot must be embedded as $expectedStorageRoot");
            capturedScript.Should().Contain("$expectedStorageRoot",
                "Issue #25: the script must declare an $expectedStorageRoot variable from the host profile");
            capturedScript.Should().Contain("$vmName = $vm.Name",
                "Issue #25: the script should read the authoritative VM name from $vm.Name");
            capturedScript.Should().Contain("$expectedVmDir = Join-Path $expectedStorageRoot $vmName",
                "Issue #25: the per-VM directory must be derived via Join-Path on storageRoot + vmName");

            // -LiteralPath prevents wildcard interpretation of VM names.
            capturedScript.Should().MatchRegex(
                @"Remove-Item\s+-LiteralPath\s+\$resolvedExpected\s+-Recurse\s+-Force",
                "Issue #25: the destroy script must recursively remove the resolved per-VM directory using -LiteralPath");

            capturedScript.Should().Contain("Remove-Item -LiteralPath $path -Force",
                "the existing VHDX cleanup loop must be preserved alongside the new directory removal and use -LiteralPath");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT", originalStorageRoot);
        }
    }

    /// <summary>Use -LiteralPath for directory existence/removal and VHDX removal: VM names containing [, ], * or ? must not become globs.
    /// Reject positional paths and -Path in the cleanup block.</summary>
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

        capturedScript.Should().MatchRegex(
            @"Test-Path\s+-LiteralPath\s+\$resolvedExpected",
            "Gate 6 follow-up: per-VM directory existence check must use -LiteralPath to avoid wildcard expansion of VM names");

        capturedScript.Should().MatchRegex(
            @"Remove-Item\s+-LiteralPath\s+\$resolvedExpected\s+-Recurse\s+-Force",
            "Gate 6 follow-up: per-VM directory removal must use -LiteralPath to avoid wildcard expansion of VM names");

        capturedScript.Should().MatchRegex(
            @"Remove-Item\s+-LiteralPath\s+\$path\s+-Force",
            "Gate 6 follow-up: VHDX-file removal loop must use -LiteralPath because VHDX paths can contain VM names with wildcard chars");

        capturedScript.Should().NotMatchRegex(
            @"Test-Path\s+(-Path\s+)?\$(resolvedExpected|path)\b",
            "Gate 6 follow-up: the unsafe `Test-Path $var` / `Test-Path -Path $var` form must not remain in the issue #25 cleanup block");
        capturedScript.Should().NotMatchRegex(
            @"Remove-Item\s+-Path\s+\$(resolvedExpected|path)\b",
            "Gate 6 follow-up: the unsafe `Remove-Item -Path $var` form must not remain in the issue #25 cleanup block");
    }

    /// <summary>Directory removal is best-effort: a [WARN] stdout line, not an exception, preserves successful destruction. Inspect the
    /// script's try/catch and warning output.</summary>
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

    /// <summary>Skip deletion with [WARN] unless the resolved VM directory is strictly under resolved StorageRoot; Hyper-V-reported names
    /// may contain traversal characters.</summary>
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

        capturedScript.Should().Contain("[System.IO.Path]::GetFullPath($expectedVmDir)",
            "Gate 6 finding #1: the expected VM directory must be normalized via [System.IO.Path]::GetFullPath");
        capturedScript.Should().Contain("[System.IO.Path]::GetFullPath($expectedStorageRoot)",
            "Gate 6 finding #1: the storage root must be normalized via [System.IO.Path]::GetFullPath for the prefix check");

        capturedScript.Should().Contain("OrdinalIgnoreCase",
            "Gate 6 finding #1: the safety-guard comparison must be case-insensitive (OrdinalIgnoreCase)");

        capturedScript.Should().Contain("[WARN] Skipping per-VM directory cleanup",
            "Gate 6 finding #1: a skip must emit a '[WARN] Skipping per-VM directory cleanup' line on stdout");
    }

    /// <summary>The removed VHDX-parent fallback indexed $vhdPaths[0], returning a character for a scalar single-disk path. Derive the
    /// directory only from StorageRoot + vmName.</summary>
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

        // Keep the per-file removal loop, but never index scalar $vhdPaths[0]: PowerShell returns a character, not a path.
        capturedScript.Should().NotContain("$vhdPaths[0]",
            "Gate 6 finding #2: $vhdPaths[0] returns the first character on a scalar single-path string; the fallback must be removed");

        capturedScript.Should().NotContain("ConfigurationFileLocation",
            "Gate 6 finding #1/#2: directory derivation must come from StorageRoot + vmName, not ConfigurationFileLocation");
    }

    /// <summary>Write-Warning can be lost: the executor does not merge warnings and successful destroy discards output. Use [WARN]-prefixed
    /// Write-Output so cleanup messages reach captured stdout.</summary>
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

        capturedScript.Should().Contain(@"Write-Output ""[WARN] Failed to remove per-VM directory",
            "Gate 6 finding #3: cleanup-failure messages must be emitted via Write-Output \"[WARN] ...\"");
        capturedScript.Should().Contain(@"Write-Output ""[WARN] Skipping per-VM directory cleanup",
            "Gate 6 finding #3: safety-skip messages must be emitted via Write-Output \"[WARN] ...\"");

        capturedScript.Should().NotContain("Write-Warning",
            "Gate 6 finding #3: Write-Warning is not observable to the caller; use Write-Output \"[WARN] ...\" instead");
    }


    /// <summary>Phase 1 rejects remote hosts; WinRM support is deferred.</summary>
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


    /// <summary>Numeric fallback uses measured PowerShell VMState ordinals, not CIM EnabledState: the latter decoded Paused (9) as
    /// Saving.</summary>
    [Theory]
    [InlineData(2, "Running")]
    [InlineData(3, "Off")]
    [InlineData(6, "Saved")]
    [InlineData(9, "Paused")]
    [InlineData(10, "Starting")]
    public async Task GetVmStatusAsync_MapsStateEnumToString(int stateValue, string expectedState)
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson(state: stateValue)));

        var result = await _manager.GetVmStatusAsync(LocalHostId, TestVmId);

        result.State.Should().Be(expectedState);
    }


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

    /// <summary>ConvertTo-Json returns an object, not an array, for one result.</summary>
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


    /// <summary>Parameterless Get-VM fails with "Value cannot be null. Parameter name: name" in the MCP-spawned PowerShell process on
    /// Windows 11 build 26200+. Use Get-VM -Name '*' -ComputerName localhost to avoid both parameterless and WMI null-name bugs.</summary>
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

        capturedScript.Should().NotMatchRegex(
            @"\$vms\s*=\s*Get-VM\s*$",
            "the script should not use parameterless Get-VM (Issue #8); " +
            "use 'Get-VM -Name ''*'' -ComputerName localhost' instead to avoid WMI provider null-name error");

        capturedScript.Should().Contain("Get-VM -Name",
            "the script should use 'Get-VM -Name' with a wildcard parameter");

        capturedScript.Should().Contain("-ComputerName localhost",
            "the script should use '-ComputerName localhost' to work around the WMI null-name bug (LF-D7)");
    }

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


    /// <summary>New-VHD needs -ComputerName localhost to avoid the WMI "Value cannot be null" bug.</summary>
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

        // The configured directory must exist or enumeration fails before PowerShell execution.
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

    /// <summary>Unconfigured image enumeration is a soft state: Configured=false, no images and an enabling Hint, not the former
    /// InvalidOperationException.</summary>
    [Fact]
    public async Task ListImagesAsync_NoImageDir_ReturnsUnconfiguredEnvelope()
    {
        // Clear environment overrides so only the null host-profile path is resolved.
        var imageDirEnv = Environment.GetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR");
        var baseVhdxEnv = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX");
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", null);
        Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", null);
        try
        {
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


    /// <summary>Running state and heartbeat cannot substitute for authenticated guest access.</summary>
    [Fact]
    public async Task WaitForReady_Refuses_Without_Credentials_Despite_Running_Heartbeat()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(SingleVmJson(state: 2))); // State 2 = Running

        using var _ = new ClearedGuestCredentialEnvironment();

        Func<Task> act = async () => await _manager.WaitForReadyAsync(LocalHostId, TestVmId, timeoutSeconds: 60);

        await act.Should().ThrowAsync<MissingCredentialsException>();
    }

    /// <summary>An unconfirmed login must identify the VM and readiness failure, not report a bare timeout.</summary>
    [Fact]
    public async Task WaitForReady_Reports_ReadinessNotReached_When_Login_Never_Confirms()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(FailureResult("Timed out waiting for VM '" + TestVmId + "' to become ready after 10 seconds"));

        var budget = new ReadinessBudget(10);
        Func<Task> act = async () => await _manager.WaitForReadyAsync(
            LocalHostId, TestVmId, budget, "readiness-user", "readiness-pass");

        var thrown = await act.Should().ThrowAsync<ReadinessNotReachedException>();
        thrown.Which.Message.Should().Contain(TestVmId)
            .And.Contain("guest-login readiness was not confirmed");
    }

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

    [Fact]
    public async Task CleanupOrphans_Returns_Empty_When_No_Orphans()
    {
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult("[]"));

        var result = await _manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().BeEmpty();
    }

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

    [Fact]
    public async Task PauseVmAsync_CallsExecutorWithInMemoryPauseRequest()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJsonWithStateName("Paused")));

        var result = await _manager.PauseVmAsync(LocalHostId, TestVmId);

        result.Should().NotBeNull();
        result.State.Should().Be("Paused");
        // #267: in-memory pause via WMI RequestStateChange, NOT the save-to-disk Suspend-VM/Save-VM verbs.
        capturedScript.Should().Contain("RequestStateChange");
        capturedScript.Should().NotContain("Suspend-VM");
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
            .ReturnsAsync(SuccessResult(SingleVmJsonWithStateName("Paused")));

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
            .ReturnsAsync(SuccessResult(SingleVmJsonWithStateName("Paused")));

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

    [Fact]
    public async Task CreateVmAsync_DefaultAutoStart_IsFalse()
    {
        string? capturedScript = null;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((script, _, _, _) => capturedScript = script)
            .ReturnsAsync(SuccessResult(SingleVmJson()));

        await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("$autoStart = $false",
            "Issue #39: default autoStart must be false");
    }


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

    /// <summary>Host-side IBaseImageHashCache owns SHA-256 pre/post checks, paying dual-hash cost once per (path, stat-tuple, TTL) to stay
    /// within the 60s RPC budget. BaseImageHashCacheTests covers hashes/cache; ReadOnly and guard-script tests cover attribute enforcement.
    /// Never restore inline Get-FileHash.</summary>
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

    /// <summary>Post-operation mutation detection is host-side (cheap stat tuple + cached hash), covered by BaseImageHashCacheTests; never
    /// restore inline hashing.</summary>
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

    /// <summary>BuildBaseVhdxGuardScript manages only IsReadOnly via Get-ItemProperty/Set-ItemProperty. Host-side IBaseImageHashCache
    /// prevents pipeline cancellation from bypassing the cache and pays dual-hash cost once per (path, stat-tuple, TTL).</summary>
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

        capturedScript.Should().Contain("Base VHDX mutation guard",
            "Issue #23: the guard script comment from BuildBaseVhdxGuardScript must be in the composed script");

        capturedScript.Should().Contain("Get-ItemProperty -LiteralPath",
            "ST-D6a: the guard script must check the IsReadOnly attribute via Get-ItemProperty -LiteralPath");
        capturedScript.Should().Contain("Set-ItemProperty -LiteralPath",
            "ST-D6a: the guard script must set the IsReadOnly attribute via Set-ItemProperty -LiteralPath");
        capturedScript.Should().Contain("IsReadOnly",
            "ST-D6a: the guard script must reference the IsReadOnly attribute");

        capturedScript.Should().Contain(@"C:\Base\base.vhdx",
            "Issue #23: the base VHDX path must be embedded in the guard script");

        capturedScript.Should().NotContain("Get-FileHash",
            "ST-D6a: inline Get-FileHash is forbidden — SHA-256 is now host-side via IBaseImageHashCache");
    }

    /// <summary>The 600s executor timeout accommodates dual SHA-256 hashing.</summary>
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

    /// <summary>Host-side RunCreateRollbackAsync uses detached cancellation so inbound cancellation cannot stop cleanup. Force primary
    /// failure and inspect the second executor call: rollback must use -LiteralPath for VHDX and VM-directory paths to avoid wildcard
    /// expansion.</summary>
    [Fact]
    public async Task CreateVmAsync_RollbackScript_ContainsLiteralPathInCleanup()
    {
        var capturedScripts = new List<string>();
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScripts.Add(s))
            .ReturnsAsync(FailureResult("New-VM : simulated failure to trigger rollback"));

        try { await _manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx"); }
        catch (VmCreateRollbackException) { /* expected */ }

        capturedScripts.Should().HaveCountGreaterThanOrEqualTo(2,
            "LF-D17: primary failure must be followed by a host-side rollback PS call");

        var rollbackScript = capturedScripts[1];
        rollbackScript.Should().Contain("-LiteralPath",
            "LF-D17 + Iteration 4: rollback cleanup must use -LiteralPath to avoid wildcard expansion of VM names / paths");
    }

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

    /// <summary>Rollback runs host-side under detached cancellation, not in the primary script. Issue164VmCreateRollbackTests covers
    /// cleanup after failure/cancellation and structured residual artifacts. The primary script must not regain Remove-VM, $postHash or
    /// $newVhdError.</summary>
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

    // OS-install must pass allowDump=false even when HYPERV_MCP_DUMP_PS_SCRIPTS is set: script dumps would expose the variable-backed admin
    // credential and unattended-XML Password.

    /// <summary>PowerShellExecutorTests covers honoring allowDump=false; this test checks that OsInstallAsync passes it. A minimal mocked
    /// success envelope avoids live Hyper-V.</summary>
    [Fact]
    public async Task OsInstallAsync_PassesAllowDumpFalse_ToPowerShellExecutor()
    {
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

        // Use a real temp ISO to pass the C# existence preflight; TestIsoInspector defaults to Windows.
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

        _mockExecutor.Verify(
            x => x.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                /* allowDump: */ false),
            Times.Once,
            "OsInstallAsync must pass allowDump: false to suppress script-dump diagnostic for the OS-install path");

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