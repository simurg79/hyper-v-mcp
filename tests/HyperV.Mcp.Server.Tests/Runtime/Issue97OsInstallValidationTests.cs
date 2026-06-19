using System.Text.Json;
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
/// Regression tests for Issue #97 / ISO-D16 + ISO-D17:
/// the C#-side preflight that runs at the top of <see cref="HyperVManager.OsInstallAsync"/>
/// before any PowerShell is spawned.
///
/// Validation order pinned by these tests (matches HyperVManager.OsInstallAsync):
///   1. ISO existence            → <see cref="IsoNotFoundException"/> (ISO_NOT_FOUND)
///   2. OS-family / install.wim  → <see cref="OsNotSupportedException"/> (OS_NOT_SUPPORTED) — ALWAYS, never bypassable
///   3. Resource floors          → <see cref="InsufficientResourcesException"/> (INSUFFICIENT_RESOURCES) — bypassed by skipPreflight=true
///
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D16, ISO-D17.
/// </summary>
[Collection("EnvVarMutating")]
[Trait("Category", "Runtime")]
public class Issue97OsInstallValidationTests : IDisposable
{
    private const string LocalHostId = "local";
    private const string TestVmName = "issue97-vm";

    private readonly Mock<IPowerShellExecutor> _mockExecutor;
    private readonly ServerOptions _options;
    private readonly IHostResolver _hostResolver;
    private readonly ILogger<HyperVManager> _logger;
    private readonly string _tempDir;
    private readonly string _fakeIsoPath;

    public Issue97OsInstallValidationTests()
    {
        _mockExecutor = new Mock<IPowerShellExecutor>(MockBehavior.Loose);
        _tempDir = Path.Combine(Path.GetTempPath(), "issue97-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        // A real .iso file on disk so the C#-side File.Exists check passes —
        // we want preflight to advance past ISO existence into OS-family / floor checks.
        _fakeIsoPath = Path.Combine(_tempDir, "fake.iso");
        File.WriteAllBytes(_fakeIsoPath, new byte[] { 0 });

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
                    BaseVhdxPath = Path.Combine(_tempDir, "base.vhdx"),
                    StorageRoot = _tempDir,
                },
            },
        };
        _hostResolver = new HostResolver(_options);
        _logger = NullLoggerFactory.Instance.CreateLogger<HyperVManager>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Builds a manager wired to a mocked <see cref="IIsoInspector"/>.
    /// Default behavior: report (true, null) — i.e. ISO is Windows.
    /// </summary>
    private HyperVManager BuildManager(IIsoInspector isoInspector)
        => new HyperVManager(_mockExecutor.Object, _hostResolver, _options, _logger, isoInspector);

    private static Mock<IIsoInspector> MockIsoInspector(bool found, string? diagnostic = null)
    {
        var m = new Mock<IIsoInspector>(MockBehavior.Strict);
        m.Setup(i => i.ContainsWindowsInstallWimWithDiagnosticAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((found, diagnostic));
        return m;
    }

    // ═════════════════════════════════════════════════════════════════════
    // 1. OS-family rejection (ISO-D16) — ALWAYS enforced, including skipPreflight=true
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OsInstall_NonWindowsIso_ThrowsOsNotSupported()
    {
        var inspector = MockIsoInspector(found: false, diagnostic: "no install.wim");
        var manager = BuildManager(inspector.Object);

        var act = async () => await manager.OsInstallAsync(
            LocalHostId, TestVmName, _fakeIsoPath, "P@ssw0rd!",
            cpuCount: 4, memoryMB: 8192, diskSizeGB: 127,
            skipPreflight: false);

        await act.Should().ThrowAsync<OsNotSupportedException>(
            "ISO without sources\\install.wim must be rejected with OS_NOT_SUPPORTED (ISO-D16)");
    }

    [Fact]
    public async Task OsInstall_NonWindowsIso_StillThrows_WhenSkipPreflightTrue()
    {
        // ISO-D16 is mandatory and not bypassable by skipPreflight.
        var inspector = MockIsoInspector(found: false, diagnostic: "no install.wim");
        var manager = BuildManager(inspector.Object);

        var act = async () => await manager.OsInstallAsync(
            LocalHostId, TestVmName, _fakeIsoPath, "P@ssw0rd!",
            cpuCount: 4, memoryMB: 8192, diskSizeGB: 127,
            skipPreflight: true);

        await act.Should().ThrowAsync<OsNotSupportedException>(
            "skipPreflight=true must NOT bypass the OS-family check (ISO-D16)");
    }

    // ═════════════════════════════════════════════════════════════════════
    // 2. Resource-floor preflight (ISO-D17)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OsInstall_CpuBelowFloor_ThrowsInsufficientResources()
    {
        var manager = BuildManager(MockIsoInspector(found: true).Object);

        var act = async () => await manager.OsInstallAsync(
            LocalHostId, TestVmName, _fakeIsoPath, "P@ssw0rd!",
            cpuCount: 1, memoryMB: 8192, diskSizeGB: 127,
            skipPreflight: false);

        var ex = (await act.Should().ThrowAsync<InsufficientResourcesException>()).Which;
        ex.FailedFloor.Should().Be("cpuCount");
        ex.Minimum.Should().Be(2);
        ex.Actual.Should().Be(1);
    }

    [Fact]
    public async Task OsInstall_MemoryBelowFloor_ThrowsInsufficientResources()
    {
        var manager = BuildManager(MockIsoInspector(found: true).Object);

        var act = async () => await manager.OsInstallAsync(
            LocalHostId, TestVmName, _fakeIsoPath, "P@ssw0rd!",
            cpuCount: 4, memoryMB: 2048, diskSizeGB: 127,
            skipPreflight: false);

        var ex = (await act.Should().ThrowAsync<InsufficientResourcesException>()).Which;
        ex.FailedFloor.Should().Be("memoryMB");
        ex.Minimum.Should().Be(4096);
        ex.Actual.Should().Be(2048);
    }

    [Fact]
    public async Task OsInstall_DiskBelowFloor_ThrowsInsufficientResources()
    {
        var manager = BuildManager(MockIsoInspector(found: true).Object);

        var act = async () => await manager.OsInstallAsync(
            LocalHostId, TestVmName, _fakeIsoPath, "P@ssw0rd!",
            cpuCount: 4, memoryMB: 8192, diskSizeGB: 32,
            skipPreflight: false);

        var ex = (await act.Should().ThrowAsync<InsufficientResourcesException>()).Which;
        ex.FailedFloor.Should().Be("diskSizeGB");
        ex.Minimum.Should().Be(64);
        ex.Actual.Should().Be(32);
    }

    [Fact]
    public async Task OsInstall_SkipPreflight_BypassesAllResourceFloors()
    {
        // Stub IsoInspector reports Windows so OS-family check passes; resource floors
        // are all violated. With skipPreflight=true none of the floors must throw —
        // execution proceeds past preflight into PowerShell orchestration. We do not
        // mock the entire orchestration, so the call will fail later, but it must NOT
        // be an InsufficientResourcesException or OsNotSupportedException.
        var manager = BuildManager(MockIsoInspector(found: true).Object);

        var act = async () => await manager.OsInstallAsync(
            LocalHostId, TestVmName, _fakeIsoPath, "P@ssw0rd!",
            cpuCount: 1, memoryMB: 1024, diskSizeGB: 16,
            skipPreflight: true,
            ct: new CancellationTokenSource(TimeSpan.FromMilliseconds(1)).Token);

        // Whatever it ends up throwing, it must not be the preflight-floor exception.
        var thrown = await Record.ExceptionAsync(act);
        thrown.Should().NotBeOfType<InsufficientResourcesException>(
            "skipPreflight=true must bypass all resource-floor checks (ISO-D17)");
        thrown.Should().NotBeOfType<OsNotSupportedException>(
            "OS-family check returned (true, null), so OS_NOT_SUPPORTED must not be raised");
    }

    // ═════════════════════════════════════════════════════════════════════
    // 3. Validation order: ISO existence → OS-family → resource floors
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OsInstall_MissingIso_ThrowsIsoNotFound_BeforeOsFamilyOrFloors()
    {
        // Inspector should never even be called — ISO existence runs first.
        var inspector = new Mock<IIsoInspector>(MockBehavior.Strict);
        var manager = BuildManager(inspector.Object);

        var missingIso = Path.Combine(_tempDir, "does-not-exist.iso");

        var act = async () => await manager.OsInstallAsync(
            LocalHostId, TestVmName, missingIso, "P@ssw0rd!",
            cpuCount: 1, memoryMB: 1024, diskSizeGB: 16, // also below floors
            skipPreflight: false);

        await act.Should().ThrowAsync<IsoNotFoundException>(
            "missing ISO must throw ISO_NOT_FOUND before OS-family / floor checks");

        inspector.Verify(
            i => i.ContainsWindowsInstallWimWithDiagnosticAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "IsoInspector must not be called when the ISO file does not exist");
    }

    [Fact]
    public async Task OsInstall_NonWindowsIso_PrecedesResourceFloors()
    {
        // Floors are violated AND OS-family check fails. OS-family must win.
        var inspector = MockIsoInspector(found: false, diagnostic: "no install.wim");
        var manager = BuildManager(inspector.Object);

        var act = async () => await manager.OsInstallAsync(
            LocalHostId, TestVmName, _fakeIsoPath, "P@ssw0rd!",
            cpuCount: 1, memoryMB: 1024, diskSizeGB: 16,
            skipPreflight: false);

        await act.Should().ThrowAsync<OsNotSupportedException>(
            "OS-family check (ISO-D16) must run before resource floors (ISO-D17)");
    }

    // ═════════════════════════════════════════════════════════════════════
    // 4. ErrorMapper translation
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void ErrorMapper_OsNotSupported_MapsToOsNotSupportedEnvelope()
    {
        var mapper = new ErrorMapper();
        var ex = new OsNotSupportedException(@"C:\ISOs\linux.iso");

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.OsNotSupported);
        response.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ErrorMapper_InsufficientResources_MapsToEnvelopeWithStructuredData()
    {
        var mapper = new ErrorMapper();
        var ex = new InsufficientResourcesException(
            failedFloor: "memoryMB", minimum: 4096, actual: 2048,
            message: "Windows 11 requires minimum 4096 MB RAM (got 2048).");

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InsufficientResources);
        response.Data.Should().NotBeNull();

        // The data payload is anonymous; round-trip through JSON for stable property access.
        var json = JsonSerializer.Serialize(response.Data);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("failedFloor").GetString().Should().Be("memoryMB");
        root.GetProperty("minimum").GetInt64().Should().Be(4096);
        root.GetProperty("actual").GetInt64().Should().Be(2048);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 5. Tool surface — vm_os_install schema includes skipPreflight (default false)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void VmTools_VmOsInstall_DeclaresSkipPreflightBooleanParameter_DefaultFalse()
    {
        // Tool surface check: ModelContextProtocol.Server discovers tool parameters via
        // reflection on the C# method signature decorated with [McpServerTool]. The
        // schema therefore mirrors the method's parameter list, names, types, and defaults.
        // We assert against the method directly to pin issue #97 / ISO-D17 contract:
        // skipPreflight must be bool with default false.
        var method = typeof(HyperV.Mcp.Server.Tools.VmTools)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .FirstOrDefault(m => string.Equals(m.Name, "VmOsInstall", StringComparison.Ordinal));

        method.Should().NotBeNull("VmTools.VmOsInstall must exist as the vm_os_install tool entry point");

        var skipParam = method!.GetParameters()
            .FirstOrDefault(p => p.Name == "skipPreflight");

        skipParam.Should().NotBeNull(
            "vm_os_install must expose a 'skipPreflight' parameter (issue #97 / ISO-D17)");
        skipParam!.ParameterType.Should().Be(typeof(bool),
            "skipPreflight is a boolean toggle in the MCP schema");
        skipParam.HasDefaultValue.Should().BeTrue(
            "skipPreflight must be optional, default false");
        skipParam.DefaultValue.Should().Be(false);
    }
}
