using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for the <c>vm_create_base_image</c> handler in <see cref="ToolDispatcher"/>
/// (Issue #51 — Phase 3 / P2).
///
/// Covers the success path and the documented failure modes:
///   - VM not running → VM_NOT_RUNNING.
///   - Sysprep timeout (VM never reaches Off) → SYSPREP_FAILED.
///   - ImageDirectory not configured → IMAGE_COPY_FAILED.
///   - Destination file already exists → IMAGE_COPY_FAILED.
///   - Branched checkpoint tree (when mergeCheckpoints=true) → MERGE_NOT_SUPPORTED.
///   - mergeCheckpoints=false → CheckpointManager.MergeAllAsync is NOT called.
///
/// See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D18, ISO-D19, ISO-D20.
/// See /myplans/vm-management/checkpoints/checkpoints-design.md — CP-D6.
/// </summary>
[Trait("Category", "Runtime")]
public class BaseImageCreationTests : IDisposable
{
    private const string TestVmName = "base-image-vm";
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    private readonly Mock<IHyperVManager> _hvManager = new();
    private readonly Mock<ICommandExecutor> _commandExecutor = new();
    private readonly Mock<IFileTransferService> _fileTransfer = new();
    private readonly Mock<ICheckpointManager> _checkpointManager = new();
    private readonly Mock<IHostResolver> _hostResolver = new();
    private readonly Mock<IConcurrencyGate> _gate = new();
    private readonly Mock<IPowerShellDirectChannel> _channel = new();

    private readonly string _imageDir;
    private readonly string _sourceVhdx;

    public BaseImageCreationTests()
    {
        // Per-test scratch directory under %TEMP%. Cleaned up in Dispose.
        _imageDir = Path.Combine(Path.GetTempPath(),
            "hvmcp-issue51-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_imageDir);

        _sourceVhdx = Path.Combine(_imageDir, "source.vhdx");
        File.WriteAllText(_sourceVhdx, "fake-vhdx-bytes");

        _gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_imageDir))
                Directory.Delete(_imageDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private ToolDispatcher CreateDispatcher(string? imageDirectory)
    {
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            QueueTimeoutSeconds = 5,
            VmLockTimeoutSeconds = 5,
            ImageDirectory = imageDirectory,
        };
        options.Hosts["local"] = new HostProfile
        {
            HostId = "local",
            ComputerName = "localhost",
            TrustPolicy = "local",
        };

        return new ToolDispatcher(
            _hvManager.Object,
            _commandExecutor.Object,
            _fileTransfer.Object,
            _checkpointManager.Object,
            _hostResolver.Object,
            new ErrorMapper(),
            _gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            _channel.Object,
            options);
    }

    private void SetupRunningVm(string state = "Running")
    {
        _hvManager.Setup(m => m.ListVmsAsync("local", TestVmName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>
            {
                new() { VmId = TestVmId, Name = TestVmName, State = state, HostId = "local" }
            });
    }

    private void SetupVhdxResolution()
    {
        _hvManager.Setup(m => m.GetPrimaryVhdxPathAsync("local", TestVmName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_sourceVhdx);
    }

    private void SetupVmReachesOff()
    {
        // After sysprep, status polls return "Off" so the handler proceeds.
        _hvManager.Setup(m => m.GetVmStatusAsync("local", TestVmId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmId, Name = TestVmName, State = "Off", HostId = "local" });
    }

    private void SetupSysprepSucceeds()
    {
        _channel.Setup(c => c.InvokeScriptAsync(
                "local", TestVmId,
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(
                Success: true, Output: Array.Empty<object?>(), Stderr: string.Empty, ExitCode: 0));
    }

    private static Dictionary<string, object?> BaseArgs(string imageName = "win11-base",
        bool mergeCheckpoints = true, int shutdownTimeoutSeconds = 600)
        => new()
        {
            ["vmName"] = TestVmName,
            ["imageName"] = imageName,
            ["hostId"] = "local",
            ["mergeCheckpoints"] = mergeCheckpoints,
            ["shutdownTimeoutSeconds"] = shutdownTimeoutSeconds,
            ["username"] = "Administrator",
            ["password"] = "p@ss-test-only",
        };

    private static McpToolResponse? Deserialize(string json) =>
        JsonSerializer.Deserialize<McpToolResponse>(json);

    // ── Success path ───────────────────────────────────────────────────

    [Fact]
    public async Task SuccessPath_RunsSysprep_MergesCheckpoints_CopiesVhdx_ReturnsGeneralizedImageInfo()
    {
        SetupRunningVm();
        SetupSysprepSucceeds();
        SetupVmReachesOff();
        SetupVhdxResolution();
        _checkpointManager.Setup(c => c.MergeAllAsync("local", TestVmId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult(true, MergedCount: 2, FailureReason: null));

        var dispatcher = CreateDispatcher(_imageDir);

        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(imageName: "win11-base"), CancellationToken.None);

        var response = Deserialize(json);
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue($"expected success, got {response.Error} ({response.ErrorCode})");

        var expectedDest = Path.Combine(_imageDir, "win11-base.vhdx");
        File.Exists(expectedDest).Should().BeTrue("the source VHDX must have been host-side copied to the image directory (ISO-D18)");

        _channel.Verify(c => c.InvokeScriptAsync("local", TestVmId,
            "Administrator", "p@ss-test-only",
            It.Is<string>(s => s.Contains("sysprep.exe", StringComparison.OrdinalIgnoreCase)
                               && s.Contains("/generalize", StringComparison.OrdinalIgnoreCase)
                               && s.Contains("/shutdown", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<CancellationToken>()),
            Times.Once, "sysprep must be invoked via IPowerShellDirectChannel with /generalize /shutdown");

        _checkpointManager.Verify(c => c.MergeAllAsync("local", TestVmId, It.IsAny<CancellationToken>()),
            Times.Once, "mergeCheckpoints=true must call MergeAllAsync");

        // IA-Gate 6 R1 Finding 2: response shape — ImageInfo carries Generalized=true
        // directly, and the envelope exposes source identifiers + merged-checkpoint count.
        response.Data.Should().NotBeNull();
        var data = (JsonElement)JsonSerializer.SerializeToElement(response.Data);
        data.TryGetProperty("image", out var imageEl).Should().BeTrue("envelope must carry the ImageInfo under 'image'");
        imageEl.GetProperty("name").GetString().Should().Be("win11-base");
        imageEl.GetProperty("path").GetString().Should().Be(expectedDest);
        imageEl.GetProperty("generalized").GetBoolean().Should().BeTrue(
            "base images produced by vm_create_base_image must set ImageInfo.Generalized=true");
        data.GetProperty("sourceVmName").GetString().Should().Be(TestVmName);
        data.GetProperty("sourceVmId").GetString().Should().Be(TestVmId);
        data.GetProperty("mergedCheckpointCount").GetInt32().Should().Be(2);
        data.GetProperty("checkpointsMerged").GetBoolean().Should().BeTrue();
    }

    // ── Sysprep channel returns Success=false → SYSPREP_FAILED ─────────

    [Fact]
    public async Task SysprepChannelReturnsFailure_ReturnsSysprepFailedEnvelope()
    {
        // IA-Gate 6 R1 Finding 1: PowerShellHost reports in-guest terminating
        // script errors as Success=false / ExitCode=1, NOT as thrown exceptions.
        // The handler must inspect the result and fail fast.
        SetupRunningVm();
        _channel.Setup(c => c.InvokeScriptAsync(
                "local", TestVmId, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(
                Success: false,
                Output: Array.Empty<object?>(),
                Stderr: "sysprep.exe : A fatal error occurred while trying to sysprep the machine.",
                ExitCode: 1));
        _checkpointManager.Setup(c => c.MergeAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult(true, 0, null));

        var dispatcher = CreateDispatcher(_imageDir);
        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(), CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SysprepFailed);
        response.Error.Should().Contain("fatal error",
            "the channel's stderr must be surfaced in the envelope's error message");

        _hvManager.Verify(m => m.GetVmStatusAsync("local", TestVmId, It.IsAny<CancellationToken>()),
            Times.Never, "must not proceed to poll-for-Off when sysprep result is Success=false");
        _hvManager.Verify(m => m.GetPrimaryVhdxPathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "must not proceed to VHDX copy when sysprep result is Success=false");
    }

    // ── Checkpoint merge runtime failure → CHECKPOINT_MERGE_FAILED ─────

    [Fact]
    public async Task CheckpointMergeRuntimeFailure_ReturnsCheckpointMergeFailedEnvelope()
    {
        SetupRunningVm();
        _checkpointManager.Setup(c => c.MergeAllAsync("local", TestVmId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CheckpointMergeFailedException("local", TestVmId,
                "Checkpoint merge failed (exit code 1): Remove-VMSnapshot: Hyper-V merge job failed."));

        var dispatcher = CreateDispatcher(_imageDir);
        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(mergeCheckpoints: true), CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.CheckpointMergeFailed);

        _channel.Verify(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Never, "sysprep must not run when checkpoint merge fails at runtime");
    }

    // ── Invalid imageName (path traversal) → INVALID_PARAMETER ─────────

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData("sub\\dir")]
    [InlineData("C:image")]
    public async Task InvalidImageName_WithTraversalCharacters_ReturnsInvalidParameter(string badName)
    {
        SetupRunningVm();
        SetupSysprepSucceeds();

        var dispatcher = CreateDispatcher(_imageDir);
        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(imageName: badName), CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            $"imageName '{badName}' contains traversal/path characters and must be rejected");

        _channel.Verify(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Never, "sysprep must not run when imageName validation fails");
    }

    // ── VM not running ─────────────────────────────────────────────────

    [Fact]
    public async Task VmNotRunning_ReturnsVmNotRunningEnvelope()
    {
        SetupRunningVm(state: "Off");

        var dispatcher = CreateDispatcher(_imageDir);
        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(), CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.VmNotRunning);

        _channel.Verify(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Never, "sysprep must not run when VM is not Running");
    }

    // ── Sysprep timeout (VM never reaches Off) ─────────────────────────

    [Fact]
    public async Task SysprepTimeout_VmNeverReachesOff_ReturnsSysprepFailedEnvelope()
    {
        SetupRunningVm();
        SetupSysprepSucceeds();
        // Status keeps returning Running for the entire polling window.
        _hvManager.Setup(m => m.GetVmStatusAsync("local", TestVmId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VmInfo { VmId = TestVmId, Name = TestVmName, State = "Running", HostId = "local" });
        _checkpointManager.Setup(c => c.MergeAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult(true, 0, null));

        var dispatcher = CreateDispatcher(_imageDir);

        // shutdownTimeoutSeconds is small; the polling interval inside the handler is 5s,
        // so one iteration triggers the timeout branch.
        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(shutdownTimeoutSeconds: 1), CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SysprepFailed);
    }

    // ── ImageDirectory not configured → IMAGE_COPY_FAILED ──────────────

    [Fact]
    public async Task MissingImageDirectory_ReturnsImageCopyFailed()
    {
        SetupRunningVm();
        SetupSysprepSucceeds();
        SetupVmReachesOff();
        SetupVhdxResolution();
        _checkpointManager.Setup(c => c.MergeAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult(true, 0, null));

        var dispatcher = CreateDispatcher(imageDirectory: null);

        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(), CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.ImageCopyFailed);
        response.Error.Should().Contain("ImageDirectory");
    }

    // ── Existing destination file → IMAGE_COPY_FAILED ──────────────────

    [Fact]
    public async Task ExistingDestinationFile_ReturnsImageCopyFailed()
    {
        SetupRunningVm();
        SetupSysprepSucceeds();
        SetupVmReachesOff();
        SetupVhdxResolution();
        _checkpointManager.Setup(c => c.MergeAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult(true, 0, null));

        // Pre-populate the destination so File.Copy(overwrite:false) throws IOException.
        File.WriteAllText(Path.Combine(_imageDir, "already-exists.vhdx"), "stale");

        var dispatcher = CreateDispatcher(_imageDir);
        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(imageName: "already-exists"), CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.ImageCopyFailed,
            "destination file already exists; File.Copy(overwrite:false) must surface IMAGE_COPY_FAILED");
    }

    // ── Branched checkpoint tree → MERGE_NOT_SUPPORTED ─────────────────

    [Fact]
    public async Task BranchedCheckpointTree_WhenMergeRequested_ReturnsMergeNotSupported()
    {
        SetupRunningVm();
        _checkpointManager.Setup(c => c.MergeAllAsync("local", TestVmId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MergeNotSupportedException("local", TestVmId,
                "Checkpoint tree is not a linear chain (branched)."));

        var dispatcher = CreateDispatcher(_imageDir);
        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(mergeCheckpoints: true), CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.MergeNotSupported);

        _channel.Verify(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Never, "sysprep must not run when merge pre-flight rejects the topology");
    }

    // ── mergeCheckpoints=false short-circuits MergeAllAsync ────────────

    [Fact]
    public async Task MergeCheckpointsFalse_DoesNotCallMergeAllAsync()
    {
        SetupRunningVm();
        SetupSysprepSucceeds();
        SetupVmReachesOff();
        SetupVhdxResolution();

        var dispatcher = CreateDispatcher(_imageDir);
        var json = await dispatcher.DispatchAsync("vm_create_base_image",
            BaseArgs(imageName: "nomerge-base", mergeCheckpoints: false),
            CancellationToken.None);

        var response = Deserialize(json);
        response!.Success.Should().BeTrue($"expected success, got {response.Error} ({response.ErrorCode})");

        _checkpointManager.Verify(c => c.MergeAllAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "mergeCheckpoints=false must short-circuit the merge step (ISO-D19)");
    }
}
