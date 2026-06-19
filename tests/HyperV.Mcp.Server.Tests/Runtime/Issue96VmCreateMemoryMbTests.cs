using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Regression tests for Issue #96 — vm_create response reports memoryMB:0 despite VM
/// created with the correct memory.
///
/// Root cause: PowerShell <c>Select-Object</c> projections used
/// <c>@{N='MemoryMB';E={$_.MemoryAssigned/1MB}}</c>. Hyper-V reports
/// <c>MemoryAssigned</c> as 0 when a VM is in the <c>Off</c> state. Because
/// <c>vm_create</c> defaults <c>autoStart:false</c>, the freshly-created VM is Off and
/// the response reported <c>memoryMB:0</c>.
///
/// Fix: replace <c>MemoryAssigned/1MB</c> with <c>MemoryStartup/1MB</c> in every
/// <c>VmInfo</c>-shaped projection. <c>MemoryStartup</c> is state-independent and
/// matches the <c>-MemoryStartupBytes</c> value passed to <c>New-VM</c>.
///
/// See GitHub issue: https://github.com/simurg79/hyper-v-mcp-server/issues/96
/// </summary>
public class Issue96VmCreateMemoryMbTests
{
    private const string LocalHostId = "local";
    private const string TestVmName = "test-vm-issue96";
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    private static (HyperVManager manager, Mock<IPowerShellExecutor> mock) BuildManager()
    {
        var mock = new Mock<IPowerShellExecutor>();
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
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        var resolver = new HostResolver(options);
        var logger = NullLogger<HyperVManager>.Instance;
        var manager = new HyperVManager(mock.Object, resolver, options, logger, new TestIsoInspector());
        return (manager, mock);
    }

    /// <summary>
    /// String-content regression: the composed PowerShell script for <c>CreateVmAsync</c>
    /// must project <c>MemoryStartup/1MB</c> and must NOT contain the buggy
    /// <c>MemoryAssigned/1MB</c> literal. This is a unit-level guard against the bug
    /// reappearing — Hyper-V's <c>MemoryAssigned</c> is 0 for Off-state VMs.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_Script_ProjectsMemoryStartupNotMemoryAssigned()
    {
        var (manager, mock) = BuildManager();
        string? capturedScript = null;
        mock.Setup(x => x.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = SingleVmJson(memoryStartupBytes: 4096L * 1024 * 1024),
                Stderr = string.Empty,
                TimedOut = false,
                Cancelled = false,
                DurationMs = 1,
            });

        await manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: @"C:\Base\base.vhdx",
            cpuCount: 2, memoryMB: 4096, autoStart: false);

        capturedScript.Should().NotBeNull();
        capturedScript!.Should().Contain("MemoryStartup/1MB",
            "Issue #96: VmInfo projection must use MemoryStartup (state-independent) " +
            "instead of MemoryAssigned (reports 0 for Off-state VMs).");
        capturedScript.Should().NotContain("MemoryAssigned/1MB",
            "Issue #96: MemoryAssigned/1MB returns 0 when the VM is Off, which is the " +
            "default state immediately after vm_create with autoStart:false.");
    }

    /// <summary>
    /// End-to-end (mocked) regression: when <c>vm_create</c> runs with autoStart:false
    /// and Hyper-V returns the canonical Get-VM JSON shape (where <c>MemoryAssigned</c>
    /// is 0 because the VM is Off but <c>MemoryStartup</c> equals the configured value),
    /// the parsed <c>VmInfo.MemoryMB</c> must reflect the configured memory — not 0.
    /// </summary>
    [Fact]
    public async Task CreateVmAsync_OffVm_ReturnsConfiguredMemoryMB_NotZero()
    {
        var (manager, mock) = BuildManager();
        // Simulate the post-fix script: project MemoryStartup, which is non-zero even
        // though the VM is Off. The fix replaces MemoryAssigned/1MB with MemoryStartup/1MB,
        // so the JSON the server sees should report MemoryMB = 4096.
        mock.Setup(x => x.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = SingleVmJson(state: 3 /* Off */, memoryMB: 4096),
                Stderr = string.Empty,
                TimedOut = false,
                Cancelled = false,
                DurationMs = 1,
            });

        var result = await manager.CreateVmAsync(LocalHostId, TestVmName,
            baseVhdxPath: @"C:\Base\base.vhdx", cpuCount: 2, memoryMB: 4096, autoStart: false);

        result.Should().NotBeNull();
        result.State.Should().Be("Off");
        result.MemoryMB.Should().Be(4096,
            "Issue #96: with autoStart:false the VM is Off, but MemoryStartup is " +
            "state-independent so the response must report the configured memory.");
    }

    /// <summary>
    /// Builds a single-VM JSON payload mimicking <c>Get-VM | Select-Object</c> output
    /// after the fix. <paramref name="memoryStartupBytes"/> is unused in the JSON
    /// (kept for documentation of what MemoryStartup represents in PowerShell);
    /// <paramref name="memoryMB"/> is the projected MemoryMB value.
    /// </summary>
    private static string SingleVmJson(
        string id = TestVmId,
        string name = TestVmName,
        int state = 3,
        int processorCount = 2,
        long memoryMB = 4096,
        long memoryStartupBytes = 0,
        double uptimeSeconds = 0) =>
        $$"""
        {
          "Id": "{{id}}",
          "Name": "{{name}}",
          "State": {{state}},
          "ProcessorCount": {{processorCount}},
          "MemoryMB": {{memoryMB}},
          "UptimeSeconds": {{uptimeSeconds}}
        }
        """;
}
