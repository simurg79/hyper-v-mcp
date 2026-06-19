using System.Text.Json;
using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for the vm_os_install feature across InputValidation, ToolCatalog,
/// ToolDispatcher, and OsInstallResult model.
/// See /myplans/vm-management/iso-installation/iso-installation-design.md.
///
/// Test conventions follow the existing patterns in:
/// - <see cref="VmNamePathTraversalTests"/> for input validation
/// - <see cref="P1ToolHandlerTests"/> and <see cref="P0ToolHandlerTests"/> for dispatch
/// - <see cref="ToolDispatchTests"/> for catalog and registration
/// </summary>
[Trait("Category", "Runtime")]
public class OsInstallTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Input Validation: ValidateIsoPath
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A valid .iso file path must pass validation without throwing.
    /// </summary>
    [Theory]
    [InlineData(@"C:\ISOs\Windows11.iso")]
    [InlineData(@"D:\images\server2022.ISO")]
    [InlineData(@"\\server\share\image.iso")]
    [InlineData("install.iso")]
    public void ValidateIsoPath_ValidPath_DoesNotThrow(string path)
    {
        var result = InputValidation.ValidateIsoPath(path);

        result.Should().Be(path);
    }

    /// <summary>
    /// Paths with non-.iso extensions must be rejected to prevent
    /// accidental or malicious mounting of non-ISO files.
    /// </summary>
    [Theory]
    [InlineData(@"C:\files\malware.exe")]
    [InlineData(@"C:\images\disk.vhdx")]
    [InlineData(@"C:\images\file.txt")]
    [InlineData(@"C:\images\setup.msi")]
    [InlineData(@"C:\images\archive.zip")]
    public void ValidateIsoPath_NonIsoExtension_Throws(string path)
    {
        var act = () => InputValidation.ValidateIsoPath(path);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("path");
    }

    /// <summary>
    /// Paths containing ".." must be rejected to prevent path traversal attacks.
    /// </summary>
    [Theory]
    [InlineData(@"C:\ISOs\..\Windows\System32\file.iso")]
    [InlineData(@"..\..\secret.iso")]
    [InlineData(@"../../../etc/passwd.iso")]
    public void ValidateIsoPath_PathTraversal_Throws(string path)
    {
        var act = () => InputValidation.ValidateIsoPath(path);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("path");
    }

    /// <summary>
    /// Null and empty ISO paths must be rejected.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateIsoPath_NullOrEmpty_Throws(string? path)
    {
        var act = () => InputValidation.ValidateIsoPath(path!);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("path");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Input Validation: ValidateAdminPassword
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Passwords of 8+ characters must pass validation.
    /// </summary>
    [Theory]
    [InlineData("P@ssw0rd")]
    [InlineData("LongSecurePassword123!")]
    [InlineData("12345678")]
    public void ValidateAdminPassword_ValidPassword_DoesNotThrow(string password)
    {
        var result = InputValidation.ValidateAdminPassword(password);

        result.Should().Be(password);
    }

    /// <summary>
    /// Passwords shorter than 8 characters must be rejected.
    /// </summary>
    [Theory]
    [InlineData("short")]
    [InlineData("1234567")]
    [InlineData("abc")]
    [InlineData("a")]
    public void ValidateAdminPassword_TooShort_Throws(string password)
    {
        var act = () => InputValidation.ValidateAdminPassword(password);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("password");
    }

    /// <summary>
    /// Null and empty passwords must be rejected.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateAdminPassword_NullOrEmpty_Throws(string? password)
    {
        var act = () => InputValidation.ValidateAdminPassword(password!);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("password");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tool Catalog
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// vm_os_install must be present in the AllTools catalog.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
    /// </summary>
    [Fact]
    public void ToolCatalog_ContainsVmOsInstall()
    {
        ToolCatalog.AllTools.Should().Contain(t => t.Name == "vm_os_install",
            "vm_os_install must be in the tool catalog");
    }

    /// <summary>
    /// vm_os_install must be categorized as Lifecycle with P1 priority.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — Tool Definition.
    /// </summary>
    [Fact]
    public void ToolCatalog_VmOsInstall_IsLifecycleP1()
    {
        var tool = ToolCatalog.AllTools.Single(t => t.Name == "vm_os_install");

        tool.Category.Should().Be(ToolCategory.Lifecycle,
            "vm_os_install is a lifecycle tool");
        tool.Priority.Should().Be(ToolPriority.P1,
            "vm_os_install is a P1 priority tool");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tool Dispatcher
    // ═══════════════════════════════════════════════════════════════════

    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
    private readonly Mock<ICheckpointManager> _checkpointManager = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IConcurrencyGate> _gate = new();

    /// <summary>
    /// Creates a ToolDispatcher with the class-level mocks and real ErrorMapper.
    /// All concurrency gates grant locks immediately by default.
    /// </summary>
    private ToolDispatcher CreateDispatcher(ServerOptions? options = null)
    {
        var serverOptions = options ?? new ServerOptions();

        // Default: all concurrency locks succeed immediately
        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        return new ToolDispatcher(
            _hvManager.Object,
            _commandExecutor.Object,
            _fileTransfer.Object,
            _checkpointManager.Object,
            _hostResolver.Object,
            new ErrorMapper(),
            _gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            serverOptions);
    }

    /// <summary>
    /// vm_os_install must be registered in the dispatcher (not throw NotImplementedException).
    /// Verifies the tool name maps to a handler by dispatching with valid args and checking
    /// that the mock OsInstallAsync is called (not a NotImplementedException response).
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_DispatchesToHandler()
    {
        var expected = new OsInstallResult
        {
            VmId = "new-vm-guid",
            Name = "test-vm",
            State = "Running",
            ProcessorCount = 4,
            MemoryMB = 8192,
            InstallationDurationSeconds = 600,
            BootstrapDurationSeconds = 120,
            TotalDurationSeconds = 720
        };
        _hvManager.Setup(m => m.OsInstallAsync(
                "local", "test-vm", @"C:\ISOs\win11.iso", "P@ssw0rd!",
                4, 8192, 127, null, "en-US", "Windows 11 Pro", null, 60,
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\win11.iso",
                ["adminPassword"] = "P@ssw0rd!"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            "vm_os_install with valid args should route to handler and return success");

        _hvManager.Verify(m => m.OsInstallAsync(
            "local", "test-vm", @"C:\ISOs\win11.iso", "P@ssw0rd!",
            4, 8192, 127, null, "en-US", "Windows 11 Pro", null, 60,
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once,
            "vm_os_install must delegate to IHyperVManager.OsInstallAsync with correct arguments");
    }

    /// <summary>
    /// vm_os_install without required 'name' parameter returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_RequiredParams_MissingName_Throws()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["isoPath"] = @"C:\ISOs\win11.iso",
                ["adminPassword"] = "P@ssw0rd!"
                // 'name' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "missing required 'name' must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_os_install without required 'isoPath' parameter returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_RequiredParams_MissingIsoPath_Throws()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["adminPassword"] = "P@ssw0rd!"
                // 'isoPath' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "missing required 'isoPath' must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_os_install without required 'adminPassword' parameter returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_RequiredParams_MissingAdminPassword_Throws()
    {
        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\win11.iso"
                // 'adminPassword' is missing
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "missing required 'adminPassword' must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_os_install with cpuCount &lt; 2 must be rejected.
    /// The minimum CPU validation is enforced in OsInstallAsync (PowerShell script).
    /// We verify the dispatcher forwards the value to OsInstallAsync which throws.
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_ValidatesMinCpuCount()
    {
        _hvManager.Setup(m => m.OsInstallAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                1, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Windows 11 requires minimum 2 vCPUs, got 1"));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\win11.iso",
                ["adminPassword"] = "P@ssw0rd!",
                ["cpuCount"] = 1
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse(
            "cpuCount < 2 must produce an error response");
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "ArgumentException for invalid cpuCount must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_os_install with memoryMB &lt; 4096 must be rejected.
    /// The minimum memory validation is enforced in OsInstallAsync (PowerShell script).
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_ValidatesMinMemory()
    {
        _hvManager.Setup(m => m.OsInstallAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), 2048, It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Windows 11 requires minimum 4096 MB RAM"));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\win11.iso",
                ["adminPassword"] = "P@ssw0rd!",
                ["memoryMB"] = 2048
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse(
            "memoryMB < 4096 must produce an error response");
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "ArgumentException for invalid memoryMB must map to INVALID_PARAMETER");
    }

    /// <summary>
    /// vm_os_install with diskSizeGB &lt; 64 must be rejected.
    /// The minimum disk validation is enforced in OsInstallAsync (PowerShell script).
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_ValidatesMinDiskSize()
    {
        _hvManager.Setup(m => m.OsInstallAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<long>(), 32, It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Windows 11 requires minimum 64 GB disk"));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\win11.iso",
                ["adminPassword"] = "P@ssw0rd!",
                ["diskSizeGB"] = 32
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse(
            "diskSizeGB < 64 must produce an error response");
        response.ErrorCode.Should().Be("INVALID_PARAMETER",
            "ArgumentException for invalid diskSizeGB must map to INVALID_PARAMETER");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Error Code Propagation Tests
    // Verify that typed ISO exceptions map to the correct error codes
    // through ErrorMapper and through ToolDispatcher.HandleOsInstallAsync.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// IsoNotFoundException must map to ISO_NOT_FOUND through ErrorMapper.
    /// </summary>
    [Fact]
    public void ErrorMapper_IsoNotFoundException_MapsToIsoNotFound()
    {
        var mapper = new ErrorMapper();
        var ex = new IsoNotFoundException(@"C:\ISOs\missing.iso");

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("ISO_NOT_FOUND",
            "IsoNotFoundException must map to ISO_NOT_FOUND error code");
        response.Error.Should().Contain("missing.iso");
    }

    /// <summary>
    /// InstallTimeoutException must map to INSTALL_TIMEOUT through ErrorMapper.
    /// </summary>
    [Fact]
    public void ErrorMapper_InstallTimeoutException_MapsToInstallTimeout()
    {
        var mapper = new ErrorMapper();
        var ex = new InstallTimeoutException(
            "Installation timed out after 60 minutes", 60, "os-ready",
            vmId: "abc-123", vmName: "test-vm");

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INSTALL_TIMEOUT",
            "InstallTimeoutException must map to INSTALL_TIMEOUT error code");
        response.Error.Should().Contain("timed out");
    }

    /// <summary>
    /// InstallFailedException must map to INSTALL_FAILED through ErrorMapper.
    /// </summary>
    [Fact]
    public void ErrorMapper_InstallFailedException_MapsToInstallFailed()
    {
        var mapper = new ErrorMapper();
        var ex = new InstallFailedException(
            "Bootstrap failed: session could not be established",
            vmId: "abc-123", vmName: "test-vm");

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INSTALL_FAILED",
            "InstallFailedException must map to INSTALL_FAILED error code");
        response.Error.Should().Contain("Bootstrap failed");
    }

    /// <summary>
    /// AutounattendFailedException must map to AUTOUNATTEND_FAILED through ErrorMapper.
    /// </summary>
    [Fact]
    public void ErrorMapper_AutounattendFailedException_MapsToAutounattendFailed()
    {
        var mapper = new ErrorMapper();
        var ex = new AutounattendFailedException(
            "Failed to create autounattend ISO: oscdimg not found");

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("AUTOUNATTEND_FAILED",
            "AutounattendFailedException must map to AUTOUNATTEND_FAILED error code");
        response.Error.Should().Contain("autounattend");
    }

    /// <summary>
    /// When OsInstallAsync throws IsoNotFoundException, the ToolDispatcher must return
    /// an McpToolResponse with ISO_NOT_FOUND error code (not COMMAND_FAILED).
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_IsoNotFound_ReturnsCorrectErrorCode()
    {
        _hvManager.Setup(m => m.OsInstallAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IsoNotFoundException(@"C:\ISOs\missing.iso"));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\missing.iso",
                ["adminPassword"] = "P@ssw0rd!"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("ISO_NOT_FOUND",
            "IsoNotFoundException must propagate as ISO_NOT_FOUND through ToolDispatcher");
    }

    /// <summary>
    /// When OsInstallAsync throws InstallTimeoutException, the ToolDispatcher must return
    /// an McpToolResponse with INSTALL_TIMEOUT error code (not COMMAND_FAILED).
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_InstallTimeout_ReturnsCorrectErrorCode()
    {
        _hvManager.Setup(m => m.OsInstallAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InstallTimeoutException(
                "Installation timed out after 60 minutes", 60, "os-ready"));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\win11.iso",
                ["adminPassword"] = "P@ssw0rd!"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("INSTALL_TIMEOUT",
            "InstallTimeoutException must propagate as INSTALL_TIMEOUT through ToolDispatcher");
    }

    /// <summary>
    /// When OsInstallAsync throws InstallFailedException (bootstrap failure),
    /// the ToolDispatcher must return INSTALL_FAILED (not COMMAND_FAILED or success).
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_BootstrapFailure_ReturnsInstallFailed()
    {
        _hvManager.Setup(m => m.OsInstallAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InstallFailedException(
                "Bootstrap failed: PS Direct session unavailable",
                vmId: "abc-123", vmName: "test-vm"));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\win11.iso",
                ["adminPassword"] = "P@ssw0rd!"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse(
            "Bootstrap failure must NOT return success");
        response.ErrorCode.Should().Be("INSTALL_FAILED",
            "Bootstrap failure must propagate as INSTALL_FAILED through ToolDispatcher");
    }

    /// <summary>
    /// When OsInstallAsync throws AutounattendFailedException, the ToolDispatcher must return
    /// an McpToolResponse with AUTOUNATTEND_FAILED error code (not COMMAND_FAILED).
    /// </summary>
    [Fact]
    public async Task Dispatch_VmOsInstall_AutounattendFailed_ReturnsCorrectErrorCode()
    {
        _hvManager.Setup(m => m.OsInstallAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AutounattendFailedException(
                "Failed to create autounattend ISO: oscdimg exit code 1"));

        var dispatcher = CreateDispatcher();
        var resultJson = await dispatcher.DispatchAsync("vm_os_install",
            new Dictionary<string, object?>
            {
                ["name"] = "test-vm",
                ["isoPath"] = @"C:\ISOs\win11.iso",
                ["adminPassword"] = "P@ssw0rd!"
            },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(resultJson);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("AUTOUNATTEND_FAILED",
            "AutounattendFailedException must propagate as AUTOUNATTEND_FAILED through ToolDispatcher");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Square Bracket Validation Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// VM names containing square brackets must be rejected because PowerShell
    /// treats them as wildcard patterns in Get-VM -Name.
    /// </summary>
    [Theory]
    [InlineData("[a-z]*")]
    [InlineData("test[0]")]
    [InlineData("[vm]")]
    [InlineData("my[test]vm")]
    public void ValidateVmName_SquareBrackets_Throws(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>(
            "Square brackets must be rejected to prevent PowerShell wildcard injection")
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // OsInstallResult Model
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// OsInstallResult properties must round-trip correctly.
    /// Verifies that all properties can be set and retrieved.
    /// </summary>
    [Fact]
    public void OsInstallResult_PropertiesRoundTrip()
    {
        var warnings = new List<string> { "No product key provided", "Using evaluation license" };

        var result = new OsInstallResult
        {
            VmId = "12345678-1234-1234-1234-123456789abc",
            Name = "test-vm",
            State = "Running",
            ProcessorCount = 4,
            MemoryMB = 8192,
            InstallationDurationSeconds = 600,
            BootstrapDurationSeconds = 120,
            TotalDurationSeconds = 720,
            GuestIpAddress = "192.168.1.100",
            Warnings = warnings
        };

        result.VmId.Should().Be("12345678-1234-1234-1234-123456789abc");
        result.Name.Should().Be("test-vm");
        result.State.Should().Be("Running");
        result.ProcessorCount.Should().Be(4);
        result.MemoryMB.Should().Be(8192);
        result.InstallationDurationSeconds.Should().Be(600);
        result.BootstrapDurationSeconds.Should().Be(120);
        result.TotalDurationSeconds.Should().Be(720);
        result.GuestIpAddress.Should().Be("192.168.1.100");
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain("No product key provided");
        result.Warnings.Should().Contain("Using evaluation license");
    }
}
