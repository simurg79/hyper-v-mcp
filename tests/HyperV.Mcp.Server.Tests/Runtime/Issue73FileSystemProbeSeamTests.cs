using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #73 — deterministic, cross-platform coverage of
/// <see cref="HyperVManager.ListImagesAsync"/>'s pre-execution probe via the
/// <see cref="IFileSystemProbe"/> seam. Replaces the ACL-driven
/// Windows-only path inside <c>Issue54ListImagesEnvelopeTests</c> with a
/// deterministic fake that drives both probe-time exception branches without
/// touching the host filesystem ACL machinery.
///
/// The error-mapping envelope contract (probe-time <c>UnauthorizedAccess</c> ⇒
/// IO_ERROR, missing directory ⇒ INVALID_PARAMETER) is locked here.
/// See PR #67 review comment 3179029483 and ST-D7.
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public class Issue73FileSystemProbeSeamTests : IDisposable
{
    private const string LocalHostId = "local";

    private readonly string? _origImageDir;
    private readonly string? _origBaseVhdx;
    private readonly string _configuredDir;

    public Issue73FileSystemProbeSeamTests()
    {
        _origImageDir = Environment.GetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR");
        _origBaseVhdx = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX");
        Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", null);

        // The probe is fully faked, so this path need not exist on disk — the
        // fake decides what to throw. Using a stable per-test path keeps any
        // diagnostic strings deterministic.
        _configuredDir = Path.Combine(
            Path.GetTempPath(),
            "hypervmcp-issue73-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", _configuredDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", _origImageDir);
        Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", _origBaseVhdx);
    }

    private (HyperVManager mgr, Mock<IPowerShellExecutor> exec, FakeFileSystemProbe probe) BuildManager()
    {
        var profile = new HostProfile
        {
            HostId = LocalHostId,
            ComputerName = "localhost",
            BaseVhdxPath = null,
        };
        var options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile> { [LocalHostId] = profile },
        };
        var resolver = new HostResolver(options);
        var exec = new Mock<IPowerShellExecutor>();
        var probe = new FakeFileSystemProbe();
        var mgr = new HyperVManager(
            exec.Object,
            resolver,
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(),
            probe);
        return (mgr, exec, probe);
    }

    /// <summary>
    /// Probe throws <see cref="UnauthorizedAccessException"/> ⇒ manager raises
    /// <see cref="IoOperationFailedException"/> and ToolDispatcher returns
    /// IO_ERROR. No PowerShell execution occurs.
    /// </summary>
    [Fact]
    public async Task Probe_UnauthorizedAccess_Maps_To_IoError()
    {
        var (mgr, exec, probe) = BuildManager();
        probe.ExceptionToThrow = new UnauthorizedAccessException("access denied (fake)");

        Func<Task> act = () => mgr.ListImagesAsync(LocalHostId);
        await act.Should().ThrowAsync<IoOperationFailedException>(
            "Issue #73: probe-time UnauthorizedAccessException must surface as IoOperationFailedException so ErrorMapper emits IO_ERROR.");

        probe.InvocationCount.Should().Be(1, "the seam must be invoked exactly once per ListImagesAsync call.");
        probe.LastProbedPath.Should().Be(_configuredDir);

        exec.Verify(
            x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never,
            "the probe must short-circuit before any PowerShell execution.");

        var dispatcher = BuildDispatcher(mgr);
        var json = await dispatcher.DispatchAsync(
            "vm_list_images",
            new Dictionary<string, object?>(),
            CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.IoError,
            "ST-D7 envelope contract: probe-layer UnauthorizedAccess ⇒ IO_ERROR.");
        response.ErrorCode.Should().NotBe(ErrorCodes.InvalidParameter,
            "regression guard: must not collapse into INVALID_PARAMETER.");
    }

    /// <summary>
    /// Probe throws <see cref="System.IO.DirectoryNotFoundException"/> ⇒
    /// manager raises <see cref="ArgumentException"/> with paramName "imageDir"
    /// and ToolDispatcher returns INVALID_PARAMETER.
    /// </summary>
    [Fact]
    public async Task Probe_DirectoryNotFound_Maps_To_InvalidParameter()
    {
        var (mgr, exec, probe) = BuildManager();
        probe.ExceptionToThrow = new System.IO.DirectoryNotFoundException("not found (fake)");

        Func<Task> act = () => mgr.ListImagesAsync(LocalHostId);
        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.Which.ParamName.Should().Be("imageDir",
            "Issue #73: probe-time DirectoryNotFoundException must surface as ArgumentException(\"imageDir\") so ErrorMapper emits INVALID_PARAMETER.");

        probe.InvocationCount.Should().Be(1);
        probe.LastProbedPath.Should().Be(_configuredDir);

        exec.Verify(
            x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);

        var dispatcher = BuildDispatcher(mgr);
        var json = await dispatcher.DispatchAsync(
            "vm_list_images",
            new Dictionary<string, object?>(),
            CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "ST-D7 envelope contract: probe-layer DirectoryNotFound ⇒ INVALID_PARAMETER.");
    }

    /// <summary>
    /// Probe throws <see cref="System.IO.IOException"/> ⇒ manager raises
    /// <see cref="IoOperationFailedException"/> (IO_ERROR), preserving
    /// the existing exception fidelity for non-permission IO failures.
    /// </summary>
    [Fact]
    public async Task Probe_IoException_Maps_To_IoError()
    {
        var (mgr, exec, probe) = BuildManager();
        probe.ExceptionToThrow = new System.IO.IOException("device not ready (fake)");

        Func<Task> act = () => mgr.ListImagesAsync(LocalHostId);
        await act.Should().ThrowAsync<IoOperationFailedException>();

        exec.Verify(
            x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
    }

    /// <summary>
    /// Happy path: probe returns silently ⇒ manager proceeds to invoke
    /// PowerShell. This pins the seam contract: a successful probe is a no-op
    /// pass-through with no envelope-shape side effects.
    /// </summary>
    [Fact]
    public async Task Probe_SuccessfulProbe_Allows_Execution()
    {
        var (mgr, exec, probe) = BuildManager();
        // No exception configured → probe returns silently.

        exec.Setup(x => x.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "[]",
                Stderr = string.Empty,
            });

        var result = await mgr.ListImagesAsync(LocalHostId);

        result.Configured.Should().BeTrue();
        result.ImageDir.Should().Be(_configuredDir);
        probe.InvocationCount.Should().Be(1);
        exec.Verify(
            x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once,
            "successful probe must allow PowerShell execution to proceed.");
    }

    private static ToolDispatcher BuildDispatcher(IHyperVManager hv)
    {
        var gate = new Mock<IConcurrencyGate>();
        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        return new ToolDispatcher(
            hv,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            new ServerOptions { DefaultHostId = LocalHostId });
    }
}
