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
/// Unit tests for the rewritten <see cref="FileTransferService"/> (issue #52, ST-5/ST-7).
///
/// The service now flows entirely through <see cref="IPowerShellDirectChannel"/>:
///   - Single file to-guest:  CopyToSessionAsync → InvokeScriptAsync (verify size)
///   - Directory to-guest:    InvokeScriptAsync (resolve guest temp path)
///                            → CopyToSessionAsync (zip)
///                            → InvokeScriptAsync (Expand-Archive + cleanup)
///   - Single file from-guest: InvokeScriptAsync (probe) → CopyFromSessionAsync
///                             → local size check
///   - Directory from-guest:  probe → InvokeScriptAsync (Compress-Archive)
///                            → CopyFromSessionAsync → best-effort cleanup
///
/// Tests use captured-call sequences rather than Moq's <c>MockSequence</c> to keep the
/// assertions readable.
/// </summary>
[Trait("Category", "Runtime")]
public class FileTransferServiceTests : IDisposable
{
    private readonly Mock<IPowerShellDirectChannel> _mockChannel;
    private readonly Mock<IHostResolver> _mockHostResolver;
    private readonly ILogger<FileTransferService> _logger;
    private readonly FileTransferService _service;

    private const string TestHostId = "local";
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    private readonly string _tempSourceFile;
    private readonly string _tempSourceDir;

    public FileTransferServiceTests()
    {
        _mockChannel = new Mock<IPowerShellDirectChannel>();
        _mockHostResolver = new Mock<IHostResolver>();
        _logger = NullLoggerFactory.Instance.CreateLogger<FileTransferService>();

        _mockHostResolver.Setup(r => r.ResolveRequired(It.IsAny<string>()))
            .Returns(new HostProfile { HostId = "local", ComputerName = "localhost" });

        _service = new FileTransferService(
            _mockChannel.Object, _mockHostResolver.Object, _logger);

        _tempSourceFile = Path.GetTempFileName();
        File.WriteAllText(_tempSourceFile, "test content");

        _tempSourceDir = Path.Combine(Path.GetTempPath(), $"hvmcp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempSourceDir);
        File.WriteAllText(Path.Combine(_tempSourceDir, "a.txt"), "content-a");
        File.WriteAllText(Path.Combine(_tempSourceDir, "b.txt"), "content-b");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempSourceFile)) File.Delete(_tempSourceFile); } catch { }
        try { if (Directory.Exists(_tempSourceDir)) Directory.Delete(_tempSourceDir, recursive: true); } catch { }
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a successful <see cref="PowerShellHostResult"/> with optional output objects.
    /// </summary>
    private static PowerShellHostResult Success(params object?[] output) =>
        new(Success: true, Output: output, Stderr: string.Empty, ExitCode: 0);

    private static PowerShellHostResult Failure(string stderr) =>
        new(Success: false, Output: Array.Empty<object?>(), Stderr: stderr, ExitCode: 1);

    // ═══════════════════════════════════════════════════════════════════
    // CopyToGuestAsync — single file
    // ═══════════════════════════════════════════════════════════════════

    // Issue #204 / VC-DEST-D1 / FT-D14: the single-file to-guest flow is now
    // ensure-parent → Copy-Item -ToSession → verify. Both the ensure-parent and
    // verify steps go through InvokeScriptAsync; the single Setup below routes
    // both, and the Callback labels them by invocation order (1st = "ensure-parent",
    // 2nd = "verify"). See Issue204DestinationParentTests.T1 for the dedicated
    // happy-path pin of the new ordering contract.
    [Fact]
    public async Task CopyToGuestAsync_File_CallsEnsureParent_ThenCopyToSession_ThenInvokeVerify_InOrder()
    {
        var calls = new List<string>();

        _mockChannel
            .Setup(c => c.CopyToSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("copy"))
            .ReturnsAsync(Success());

        var invokeCount = 0;
        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                invokeCount++;
                calls.Add(invokeCount == 1 ? "ensure-parent" : "verify");
            })
            .ReturnsAsync(() => invokeCount == 1 ? Success() : Success((long)2048));

        var result = await _service.CopyToGuestAsync(
            TestHostId, TestVmId,
            _tempSourceFile, @"C:\dest\file.txt",
            isDirectory: false,
            username: "testuser", password: "testpass");

        calls.Should().Equal("ensure-parent", "copy", "verify");
        result.Verified.Should().BeTrue();
        result.IsDirectory.Should().BeFalse();
        result.BytesTransferred.Should().Be(2048);
        result.FileCount.Should().Be(1);
        result.SourcePath.Should().Be(_tempSourceFile);
        result.DestPath.Should().Be(@"C:\dest\file.txt");
    }

    [Fact]
    public async Task CopyToGuestAsync_File_CopyFails_ThrowsInvalidOperationException()
    {
        // Issue #204 / PR #211 review feedback: the single-file to-guest flow now
        // ensures the destination parent before Copy-Item -ToSession. Stub the
        // ensure-parent (and verify) InvokeScriptAsync calls so the test exercises
        // the *copy* failure path explicitly, not the contract-violation null guard.
        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success());

        _mockChannel
            .Setup(c => c.CopyToSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failure("Access is denied"));

        Func<Task> act = () => _service.CopyToGuestAsync(
            TestHostId, TestVmId, _tempSourceFile, @"C:\dest\file.txt",
            isDirectory: false, username: "testuser", password: "testpass");

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Access is denied");
    }

    [Fact]
    public async Task CopyToGuestAsync_MissingLocalSource_ThrowsFileNotFoundException()
    {
        Func<Task> act = () => _service.CopyToGuestAsync(
            TestHostId, TestVmId,
            @"C:\definitely\does\not\exist.bin", @"C:\dest\file.bin",
            isDirectory: false, username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<FileNotFoundException>();

        // Channel must NEVER be touched — validation runs before any RPC.
        _mockChannel.Verify(c => c.CopyToSessionAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CopyToGuestAsync_IsDirectoryTrueButPathIsFile_ThrowsInvalidOperationException()
    {
        Func<Task> act = () => _service.CopyToGuestAsync(
            TestHostId, TestVmId, _tempSourceFile, @"C:\dest\dir",
            isDirectory: true, username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a directory*");
    }

    // ═══════════════════════════════════════════════════════════════════
    // CopyToGuestAsync — directory (zip-staged)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CopyToGuestAsync_Directory_OrdersCalls_ResolveTemp_CopyZip_ExpandCleanup()
    {
        var calls = new List<string>();

        // First InvokeScriptAsync call: resolve guest temp path (returns guest path string).
        // Second InvokeScriptAsync call: Expand-Archive + cleanup (returns total bytes).
        var invokeCount = 0;
        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                invokeCount++;
                if (invokeCount == 1)
                {
                    calls.Add("resolveTemp");
                    return Success(@"C:\Users\test\AppData\Local\Temp\hvmcp_xyz.zip");
                }
                calls.Add("expand");
                return Success((long)4096);
            });

        _mockChannel
            .Setup(c => c.CopyToSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("copyZip"))
            .ReturnsAsync(Success());

        var result = await _service.CopyToGuestAsync(
            TestHostId, TestVmId,
            _tempSourceDir, @"C:\dest\dir",
            isDirectory: true,
            username: "testuser", password: "testpass");

        calls.Should().Equal("resolveTemp", "copyZip", "expand");
        result.IsDirectory.Should().BeTrue();
        result.Verified.Should().BeTrue();
        result.BytesTransferred.Should().Be(4096);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CopyFromGuestAsync — single file
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CopyFromGuestAsync_File_OrdersCalls_Probe_CopyFromSession()
    {
        var calls = new List<string>();
        var localDest = Path.Combine(Path.GetTempPath(), $"hvmcp_pull_{Guid.NewGuid():N}.txt");

        // Probe returns a numeric size string (file branch).
        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("probe"))
            .ReturnsAsync(Success("12"));

        _mockChannel
            .Setup(c => c.CopyFromSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, string, CancellationToken>(
                (_, _, _, _, _, dst, _) =>
                {
                    calls.Add("copyFrom");
                    // Simulate that the channel actually wrote the file locally.
                    File.WriteAllText(dst, "hello world!");
                })
            .ReturnsAsync(Success());

        try
        {
            var result = await _service.CopyFromGuestAsync(
                TestHostId, TestVmId,
                @"C:\guest\file.txt", localDest,
                username: "testuser", password: "testpass");

            calls.Should().Equal("probe", "copyFrom");
            result.IsDirectory.Should().BeFalse();
            result.Verified.Should().BeTrue();
            result.BytesTransferred.Should().Be(12);
            result.SourcePath.Should().Be(@"C:\guest\file.txt");
            result.DestPath.Should().Be(localDest);
        }
        finally
        {
            try { if (File.Exists(localDest)) File.Delete(localDest); } catch { }
        }
    }

    [Fact]
    public async Task CopyFromGuestAsync_GuestProbeFileNotFound_ThrowsFileNotFoundException()
    {
        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failure("Cannot find path 'C:\\guest\\missing.txt' because it does not exist."));

        Func<Task> act = () => _service.CopyFromGuestAsync(
            TestHostId, TestVmId,
            @"C:\guest\missing.txt", @"C:\host\file.txt",
            username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ═══════════════════════════════════════════════════════════════════
    // CopyFromGuestAsync — directory
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CopyFromGuestAsync_Directory_OrdersCalls_Probe_ResolveTemp_Compress_Copy_Cleanup()
    {
        var calls = new List<string>();
        var localDestDir = Path.Combine(Path.GetTempPath(), $"hvmcp_pull_dir_{Guid.NewGuid():N}");

        // Build a real local zip that ZipFile.ExtractToDirectory can read,
        // and have CopyFromSessionAsync deposit it at the local destination zip path.
        var realZipSource = Path.Combine(Path.GetTempPath(), $"hvmcp_seed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(realZipSource);
        File.WriteAllText(Path.Combine(realZipSource, "x.txt"), "12345");
        var seededZipPath = Path.Combine(Path.GetTempPath(), $"hvmcp_seed_zip_{Guid.NewGuid():N}.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(realZipSource, seededZipPath);

        var invokeCount = 0;
        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                invokeCount++;
                return invokeCount switch
                {
                    1 => RecordAndReturn("probe", Success("dir")),
                    2 => RecordAndReturn("resolveTemp", Success(@"C:\Temp\hvmcp_x.zip")),
                    3 => RecordAndReturn("compress", Success((long)999)),
                    _ => RecordAndReturn("cleanup", Success(true)),
                };
            });

        _mockChannel
            .Setup(c => c.CopyFromSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, string, CancellationToken>(
                (_, _, _, _, _, dst, _) =>
                {
                    calls.Add("copyFromZip");
                    // Simulate that the channel pulled the guest zip locally.
                    File.Copy(seededZipPath, dst, overwrite: true);
                })
            .ReturnsAsync(Success());

        try
        {
            var result = await _service.CopyFromGuestAsync(
                TestHostId, TestVmId,
                @"C:\guest\dir", localDestDir,
                username: "testuser", password: "testpass");

            calls.Should().StartWith(new[] { "probe", "resolveTemp", "compress", "copyFromZip" });
            calls.Should().Contain("cleanup");
            result.IsDirectory.Should().BeTrue();
            result.Verified.Should().BeTrue();
            Directory.Exists(localDestDir).Should().BeTrue();
        }
        finally
        {
            try { if (File.Exists(seededZipPath)) File.Delete(seededZipPath); } catch { }
            try { if (Directory.Exists(realZipSource)) Directory.Delete(realZipSource, recursive: true); } catch { }
            try { if (Directory.Exists(localDestDir)) Directory.Delete(localDestDir, recursive: true); } catch { }
        }

        PowerShellHostResult RecordAndReturn(string label, PowerShellHostResult r)
        {
            calls.Add(label);
            return r;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Local-only host enforcement
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CopyToGuestAsync_RemoteHost_ThrowsNotSupportedException()
    {
        _mockHostResolver.Setup(r => r.ResolveRequired("remote-host"))
            .Returns(new HostProfile { HostId = "remote-host", ComputerName = "remote.server.com" });

        Func<Task> act = () => _service.CopyToGuestAsync(
            "remote-host", TestVmId, _tempSourceFile, @"C:\dst.txt",
            username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Remote host*not supported*Phase 1*");
    }

    [Fact]
    public async Task CopyFromGuestAsync_RemoteHost_ThrowsNotSupportedException()
    {
        _mockHostResolver.Setup(r => r.ResolveRequired("remote-host"))
            .Returns(new HostProfile { HostId = "remote-host", ComputerName = "remote.server.com" });

        Func<Task> act = () => _service.CopyFromGuestAsync(
            "remote-host", TestVmId, @"C:\src.txt", @"C:\dst.txt",
            username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Remote host*not supported*Phase 1*");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Argument validation
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CopyToGuestAsync_InvalidHostId_ThrowsArgumentException(string? hostId)
    {
        Func<Task> act = () => _service.CopyToGuestAsync(
            hostId!, TestVmId, _tempSourceFile, @"C:\dst.txt");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CopyToGuestAsync_InvalidVmId_ThrowsArgumentException(string? vmId)
    {
        Func<Task> act = () => _service.CopyToGuestAsync(
            TestHostId, vmId!, _tempSourceFile, @"C:\dst.txt");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CopyToGuestAsync_InvalidSourcePath_ThrowsArgumentException(string? sourcePath)
    {
        Func<Task> act = () => _service.CopyToGuestAsync(
            TestHostId, TestVmId, sourcePath!, @"C:\dst.txt");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CopyFromGuestAsync_InvalidDestPath_ThrowsArgumentException(string? destPath)
    {
        Func<Task> act = () => _service.CopyFromGuestAsync(
            TestHostId, TestVmId, @"C:\src.txt", destPath!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("'; Remove-Item -Recurse C:\\ -Force; '")]
    [InlineData("vm-name-123")]
    public async Task CopyToGuestAsync_NonGuidVmId_ThrowsArgumentException(string vmId)
    {
        Func<Task> act = () => _service.CopyToGuestAsync(
            TestHostId, vmId, _tempSourceFile, @"C:\dst.txt",
            username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*vmId must be a valid GUID*");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// In-memory ILogger used by the cleanup-redaction test below to capture the
// fully-formatted log output and assert credentials never reach the log sink.
// ═══════════════════════════════════════════════════════════════════════════════

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string FormattedMessage, Exception? Exception);

    public List<Entry> Entries { get; } = new();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }
}

[Trait("Category", "Runtime")]
public class FileTransferServiceCleanupRedactionTests
{
    private const string TestHostId = "local";
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    /// <summary>
    /// Regression: <c>BestEffortGuestCleanupAsync</c> must NOT pass the raw exception object
    /// to <c>ILogger.LogWarning</c>. If it did, common logging providers would serialize the
    /// inner-exception chain — and a <see cref="PowerShellDirectChannelException"/>'s inner
    /// message can carry credentials. The fix logs only the exception type name + the
    /// already-redacted top-level message.
    /// </summary>
    [Fact]
    public async Task BestEffortGuestCleanup_When_ChannelThrows_PowerShellDirectChannelException_Does_Not_Leak_InnerCredential()
    {
        const string password = "hunter2";

        // Inner carries the raw credential as a real PowerShell would (e.g. failed login).
        var inner = new InvalidOperationException(
            $"Failed login for user admin with password {password}");

        // Wrapper's top-level Message is the channel's already-redacted text.
        var wrapper = new PowerShellDirectChannelException(
            "PowerShell Direct cleanup invocation failed: ***REDACTED***",
            inner);

        var capturingLogger = new CapturingLogger<FileTransferService>();
        var mockHostResolver = new Mock<IHostResolver>();
        mockHostResolver.Setup(r => r.ResolveRequired(It.IsAny<string>()))
            .Returns(new HostProfile { HostId = "local", ComputerName = "localhost" });

        var mockChannel = new Mock<IPowerShellDirectChannel>();

        // Build a real local zip so ZipFile.ExtractToDirectory succeeds; we only care about
        // exercising the cleanup-failure logging path.
        var seedDir = Path.Combine(Path.GetTempPath(), $"hvmcp_seed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(seedDir);
        File.WriteAllText(Path.Combine(seedDir, "x.txt"), "1");
        var seededZipPath = Path.Combine(Path.GetTempPath(), $"hvmcp_seed_zip_{Guid.NewGuid():N}.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(seedDir, seededZipPath);

        var localDestDir = Path.Combine(Path.GetTempPath(), $"hvmcp_pull_dir_{Guid.NewGuid():N}");

        var invokeCount = 0;
        mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, string, string, string, IDictionary<string, object?>?, CancellationToken>(
                (_, _, _, _, _, _, _) =>
                {
                    invokeCount++;
                    return invokeCount switch
                    {
                        // probe → "dir"
                        1 => Task.FromResult(new PowerShellHostResult(true, new object?[] { "dir" }, string.Empty, 0)),
                        // resolve guest temp path
                        2 => Task.FromResult(new PowerShellHostResult(true, new object?[] { @"C:\Temp\hvmcp_x.zip" }, string.Empty, 0)),
                        // compress
                        3 => Task.FromResult(new PowerShellHostResult(true, new object?[] { (long)999 }, string.Empty, 0)),
                        // cleanup → THROW the credential-bearing wrapper
                        _ => throw wrapper,
                    };
                });

        mockChannel
            .Setup(c => c.CopyFromSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, string, CancellationToken>(
                (_, _, _, _, _, dst, _) => File.Copy(seededZipPath, dst, overwrite: true))
            .ReturnsAsync(new PowerShellHostResult(true, Array.Empty<object?>(), string.Empty, 0));

        var service = new FileTransferService(
            mockChannel.Object, mockHostResolver.Object, capturingLogger);

        try
        {
            // Should succeed end-to-end — cleanup is best-effort and its failure is swallowed.
            var result = await service.CopyFromGuestAsync(
                TestHostId, TestVmId,
                @"C:\guest\dir", localDestDir,
                username: "testuser", password: "testpass");

            result.IsDirectory.Should().BeTrue();

            // The warning MUST be logged.
            var warnings = capturingLogger.Entries
                .Where(e => e.Level == LogLevel.Warning)
                .ToList();
            warnings.Should().NotBeEmpty(
                "the cleanup failure must surface as a Warning log entry with safe metadata");

            // Critical assertion: the password must NEVER appear in the formatted message,
            // and the raw exception object must NOT have been attached to the log entry
            // (otherwise providers will serialize the inner chain and leak the credential).
            foreach (var w in warnings)
            {
                w.FormattedMessage.Should().NotContain(password,
                    "BestEffortGuestCleanupAsync must not pass the raw exception (whose " +
                    "InnerException carries credentials) to LogWarning");
                w.Exception.Should().BeNull(
                    "no Exception object should be attached to the warning — only safe " +
                    "metadata (type name + redacted top-level message) should be logged");
            }

            // And the safe metadata IS present.
            warnings.Should().Contain(w =>
                w.FormattedMessage.Contains(nameof(PowerShellDirectChannelException)) &&
                w.FormattedMessage.Contains("***REDACTED***"));
        }
        finally
        {
            try { if (File.Exists(seededZipPath)) File.Delete(seededZipPath); } catch { }
            try { if (Directory.Exists(seedDir)) Directory.Delete(seedDir, recursive: true); } catch { }
            try { if (Directory.Exists(localDestDir)) Directory.Delete(localDestDir, recursive: true); } catch { }
        }
    }
}
