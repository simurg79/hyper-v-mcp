using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #54 / ST-D7 — vm_list_images envelope contract.
/// See /myplans/vm-management/storage/storage-design.md — ST-D7.
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/54.
///
/// Covers the four envelope outcomes:
/// <list type="bullet">
///   <item>All env vars unset + no host-profile <c>BaseVhdxPath</c> ⇒ success envelope,
///         <c>Configured=false</c>, empty <c>Images</c>, populated <c>Hint</c>.</item>
///   <item><c>HYPERV_MCP_IMAGE_DIR</c> pointing to a non-existent path ⇒ INVALID_PARAMETER.</item>
///   <item>Configured directory exists but the underlying enumeration throws
///         <see cref="System.UnauthorizedAccessException"/> ⇒ IO_ERROR.</item>
///   <item>Resolution-order priority: <c>HYPERV_MCP_IMAGE_DIR</c> &gt; host-profile
///         <c>BaseVhdxPath</c> &gt; <c>HYPERV_MCP_BASE_VHDX</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public class Issue54ListImagesEnvelopeTests : IDisposable
{
    private const string LocalHostId = "local";

    private readonly string? _origImageDir;
    private readonly string? _origBaseVhdx;

    public Issue54ListImagesEnvelopeTests()
    {
        // Snapshot env vars so each test can mutate freely without leaking.
        _origImageDir = Environment.GetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR");
        _origBaseVhdx = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX");
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", null);
        Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", _origImageDir);
        Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", _origBaseVhdx);
    }

    private static (HyperVManager mgr, Mock<IPowerShellExecutor> exec) BuildManager(
        HostProfile profile)
    {
        var options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile> { [LocalHostId] = profile },
        };
        var resolver = new HostResolver(options);
        var exec = new Mock<IPowerShellExecutor>();
        var mgr = new HyperVManager(
            exec.Object,
            resolver,
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());
        return (mgr, exec);
    }

    /// <summary>
    /// ST-D7 unconfigured success envelope: no env var, no host-profile BaseVhdxPath.
    /// </summary>
    [Fact]
    public async Task Unconfigured_Returns_SoftSuccess_With_Hint()
    {
        var (mgr, exec) = BuildManager(new HostProfile
        {
            HostId = LocalHostId,
            ComputerName = "localhost",
            BaseVhdxPath = null,
        });

        var result = await mgr.ListImagesAsync(LocalHostId);

        result.Should().NotBeNull();
        result.Configured.Should().BeFalse();
        result.Images.Should().BeEmpty();
        result.Count.Should().Be(0);
        result.ImageDir.Should().BeNull();
        result.Hint.Should().NotBeNullOrWhiteSpace();
        result.Hint.Should().Contain("HYPERV_MCP_IMAGE_DIR");

        exec.Verify(
            x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never,
            "unconfigured short-circuit must not run any PowerShell.");
    }

    /// <summary>
    /// ST-D7: HYPERV_MCP_IMAGE_DIR pointing to a path that does not exist on disk
    /// must surface as INVALID_PARAMETER through ToolDispatcher (manager throws
    /// ArgumentException → ErrorMapper → INVALID_PARAMETER).
    /// </summary>
    [Fact]
    public async Task ConfiguredButMissingPath_Maps_To_InvalidParameter()
    {
        var bogus = Path.Combine(Path.GetTempPath(),
            "hypervmcp-test-missing-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", bogus);

        var (mgr, _) = BuildManager(new HostProfile
        {
            HostId = LocalHostId,
            ComputerName = "localhost",
            BaseVhdxPath = null,
        });

        // Direct manager call: ArgumentException by contract.
        Func<Task> act = () => mgr.ListImagesAsync(LocalHostId);
        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.Which.ParamName.Should().Be("imageDir");

        // Dispatcher round-trip: structured INVALID_PARAMETER envelope.
        var dispatcher = BuildDispatcher(mgr);
        var json = await dispatcher.DispatchAsync(
            "vm_list_images",
            new Dictionary<string, object?>(),
            CancellationToken.None);
        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
    }

    /// <summary>
    /// ST-D7: configured directory exists but enumeration fails with
    /// UnauthorizedAccessException ⇒ IO_ERROR through the dispatcher.
    /// </summary>
    [Fact]
    public async Task EnumerationFails_With_UnauthorizedAccess_Maps_To_IoError()
    {
        var realDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "hypervmcp-test-images-" + Guid.NewGuid().ToString("N")));
        try
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", realDir.FullName);

            var (mgr, exec) = BuildManager(new HostProfile
            {
                HostId = LocalHostId,
                ComputerName = "localhost",
                BaseVhdxPath = null,
            });

            exec.Setup(x => x.ExecuteAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<bool>()))
                .ThrowsAsync(new UnauthorizedAccessException("access is denied"));

            // Direct call: IoOperationFailedException.
            Func<Task> act = () => mgr.ListImagesAsync(LocalHostId);
            await act.Should().ThrowAsync<IoOperationFailedException>();

            // Dispatcher round-trip: IO_ERROR envelope.
            var dispatcher = BuildDispatcher(mgr);
            var json = await dispatcher.DispatchAsync(
                "vm_list_images",
                new Dictionary<string, object?>(),
                CancellationToken.None);
            var response = JsonSerializer.Deserialize<McpToolResponse>(json);
            response!.Success.Should().BeFalse();
            response.ErrorCode.Should().Be(ErrorCodes.IoError,
                "ST-D7: ACL/IO enumeration failure on a configured directory must map to IO_ERROR.");
        }
        finally
        {
            try { realDir.Delete(recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// ST-D7 resolution-order priority: HYPERV_MCP_IMAGE_DIR wins over host-profile
    /// BaseVhdxPath, which wins over HYPERV_MCP_BASE_VHDX.
    /// </summary>
    [Fact]
    public async Task ResolutionOrder_ImageDirEnv_Wins_Over_HostProfile_And_BaseVhdxEnv()
    {
        var winnerDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "hypervmcp-test-winner-" + Guid.NewGuid().ToString("N")));
        var profileDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "hypervmcp-test-profile-" + Guid.NewGuid().ToString("N")));
        try
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", winnerDir.FullName);
            Environment.SetEnvironmentVariable(
                "HYPERV_MCP_BASE_VHDX",
                Path.Combine(Path.GetTempPath(), "envloser", "base.vhdx"));

            var (mgr, exec) = BuildManager(new HostProfile
            {
                HostId = LocalHostId,
                ComputerName = "localhost",
                BaseVhdxPath = Path.Combine(profileDir.FullName, "base.vhdx"),
            });

            string? capturedScript = null;
            exec.Setup(x => x.ExecuteAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<bool>()))
                .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
                .ReturnsAsync(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = "[]",
                    Stderr = string.Empty,
                });

            var result = await mgr.ListImagesAsync(LocalHostId);

            result.Configured.Should().BeTrue();
            result.ImageDir.Should().Be(winnerDir.FullName,
                "HYPERV_MCP_IMAGE_DIR must beat host-profile BaseVhdxPath and HYPERV_MCP_BASE_VHDX (ST-D7 priority 1).");
            capturedScript.Should().NotBeNull();
            capturedScript.Should().Contain(winnerDir.FullName);
            capturedScript.Should().NotContain(profileDir.FullName,
                "the host-profile dir must be ignored when HYPERV_MCP_IMAGE_DIR is set.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", null);
            try { winnerDir.Delete(recursive: true); } catch { /* ignore */ }
            try { profileDir.Delete(recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// ST-D7 resolution-order priority: when HYPERV_MCP_IMAGE_DIR is unset, the
    /// host-profile BaseVhdxPath parent directory wins over HYPERV_MCP_BASE_VHDX.
    /// </summary>
    [Fact]
    public async Task ResolutionOrder_HostProfile_Wins_Over_BaseVhdxEnv()
    {
        var profileDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "hypervmcp-test-profile-" + Guid.NewGuid().ToString("N")));
        var envDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "hypervmcp-test-env-" + Guid.NewGuid().ToString("N")));
        try
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", null);
            Environment.SetEnvironmentVariable(
                "HYPERV_MCP_BASE_VHDX",
                Path.Combine(envDir.FullName, "base.vhdx"));

            var (mgr, exec) = BuildManager(new HostProfile
            {
                HostId = LocalHostId,
                ComputerName = "localhost",
                BaseVhdxPath = Path.Combine(profileDir.FullName, "base.vhdx"),
            });

            exec.Setup(x => x.ExecuteAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new PowerShellResult { ExitCode = 0, Stdout = "[]", Stderr = string.Empty });

            var result = await mgr.ListImagesAsync(LocalHostId);

            result.Configured.Should().BeTrue();
            result.ImageDir.Should().Be(profileDir.FullName,
                "host-profile BaseVhdxPath parent must beat HYPERV_MCP_BASE_VHDX (ST-D7 priority 2).");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERV_MCP_BASE_VHDX", null);
            try { profileDir.Delete(recursive: true); } catch { /* ignore */ }
            try { envDir.Delete(recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Code Review Gate 6 Blocker #1 (#54): the manager now probes the configured
    /// image directory with <c>DirectoryInfo.Exists</c> + <c>EnumerateFileSystemEntries</c>
    /// before invoking PowerShell. An <see cref="UnauthorizedAccessException"/> from the
    /// probe (existing-but-unenumerable directory) MUST map to <c>IO_ERROR</c>, not
    /// <c>INVALID_PARAMETER</c>. We trigger the probe deterministically via an ACL
    /// Deny-Read on the directory — no executor mock is needed because the probe
    /// runs before any PowerShell invocation.
    /// </summary>
    /// <remarks>
    /// On systems where the test cannot deterministically apply a Deny-Read ACL
    /// (non-NTFS CI worker, restricted token, etc.) we fall back to asserting the
    /// simulated-throw layer via the executor — the existing
    /// <c>EnumerationFails_With_UnauthorizedAccess_Maps_To_IoError</c> test already
    /// covers that path, so this test silently no-ops in that environment rather
    /// than producing a false failure. The intent of this test is specifically to
    /// lock in the probe-layer behavior introduced by the Gate 3 re-loop fix.
    /// </remarks>
    [Fact]
    public async Task Probe_UnauthorizedAccess_OnExistingDirectory_Maps_To_IoError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ACL probe is Windows-specific; skip on other OSes.
        }

        var realDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "hypervmcp-test-acl-" + Guid.NewGuid().ToString("N")));

        bool aclApplied = false;
        FileSystemAccessRule? denyRule = null;
        try
        {
            // Apply Deny-Read ACL for the current user so EnumerateFileSystemEntries
            // throws UnauthorizedAccessException at the probe layer.
            try
            {
                var sid = WindowsIdentity.GetCurrent().User!;
                denyRule = new FileSystemAccessRule(
                    sid,
                    FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Deny);
                var sec = realDir.GetAccessControl();
                sec.AddAccessRule(denyRule);
                realDir.SetAccessControl(sec);

                // Verify the deny actually takes effect — admins/SYSTEM may
                // bypass user-level deny rules. If we can still enumerate, fall
                // back to the simulated-throw test (no-op for this case).
                try
                {
                    using var probe = Directory.EnumerateFileSystemEntries(realDir.FullName).GetEnumerator();
                    probe.MoveNext();
                    // Enumeration succeeded → ACL did not block us. Skip.
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    aclApplied = true;
                }
                catch (IOException)
                {
                    aclApplied = true;
                }
            }
            catch
            {
                // ACL plumbing failed (non-NTFS, restricted token); skip silently.
                return;
            }

            if (!aclApplied) return;

            Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", realDir.FullName);

            var (mgr, exec) = BuildManager(new HostProfile
            {
                HostId = LocalHostId,
                ComputerName = "localhost",
                BaseVhdxPath = null,
            });

            // The probe runs BEFORE the executor; if the fix is wired correctly,
            // the executor must never be invoked on this path.
            Func<Task> act = () => mgr.ListImagesAsync(LocalHostId);
            await act.Should().ThrowAsync<IoOperationFailedException>(
                "Gate 3 re-loop Blocker #1: probe-layer UnauthorizedAccessException must surface as IoOperationFailedException, not ArgumentException.");

            exec.Verify(
                x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
                Times.Never,
                "the probe must short-circuit before any PowerShell execution.");

            // Dispatcher round-trip: IO_ERROR envelope (NOT INVALID_PARAMETER).
            var dispatcher = BuildDispatcher(mgr);
            var json = await dispatcher.DispatchAsync(
                "vm_list_images",
                new Dictionary<string, object?>(),
                CancellationToken.None);
            var response = JsonSerializer.Deserialize<McpToolResponse>(json);
            response!.Success.Should().BeFalse();
            response.ErrorCode.Should().Be(ErrorCodes.IoError,
                "Gate 3 re-loop Blocker #1: existing-but-unenumerable directory must map to IO_ERROR, not INVALID_PARAMETER.");
            response.ErrorCode.Should().NotBe(ErrorCodes.InvalidParameter,
                "regression guard: the historical mis-mapping to INVALID_PARAMETER must not return.");
        }
        finally
        {
            // Best-effort: remove the deny rule before deleting the directory.
            try
            {
                if (denyRule is not null)
                {
                    var sec = realDir.GetAccessControl();
                    sec.RemoveAccessRule(denyRule);
                    realDir.SetAccessControl(sec);
                }
            }
            catch { /* swallow */ }
            try { realDir.Delete(recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Companion to the probe-layer Blocker #1 test: an existing path that
    /// becomes missing between configuration and probe still maps to
    /// <c>INVALID_PARAMETER</c> via <c>ArgumentException("imageDir")</c>. This
    /// pins down the "Exists==false" branch of the new probe so a future
    /// refactor cannot accidentally collapse missing and unauthorized into the
    /// same error code again.
    /// </summary>
    [Fact]
    public async Task Probe_DirectoryNotFound_AfterConfigure_Maps_To_InvalidParameter()
    {
        var transientDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "hypervmcp-test-transient-" + Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR", transientDir.FullName);
        // Delete it BEFORE invoking the manager so DirectoryInfo.Exists==false.
        try { transientDir.Delete(recursive: true); } catch { /* ignore */ }

        var (mgr, exec) = BuildManager(new HostProfile
        {
            HostId = LocalHostId,
            ComputerName = "localhost",
            BaseVhdxPath = null,
        });

        Func<Task> act = () => mgr.ListImagesAsync(LocalHostId);
        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.Which.ParamName.Should().Be("imageDir",
            "Exists==false branch of the new probe must remain ArgumentException(\"imageDir\") → INVALID_PARAMETER.");

        exec.Verify(
            x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
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
