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
/// Issue #204: <c>vm_copy_file</c> (single-file host→guest) must auto-create the
/// destination's parent directory on the guest and classify ensure-parent failure
/// as <c>DEST_DIR_MISSING</c> via a typed exception — distinct from <c>FILE_NOT_FOUND</c>
/// which remains reserved for missing source artifacts (Issue #38 contract).
///
/// Covers Test Plan T1, T2, T3, T4, T5, T6, T7, T10, T11 from
/// /myplans/execution/file-transfer/vm-copy-file-dest-dir-design.md.
/// T12 / T13 are deferred per the design (C6) and intentionally omitted.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue204DestinationParentTests : IDisposable
{
    private const string TestHostId = "local";
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    private readonly Mock<IPowerShellDirectChannel> _mockChannel = new();
    private readonly Mock<IHostResolver> _mockHostResolver = new();
    private readonly FileTransferService _service;

    private readonly string _tempSourceFile;
    private readonly string _tempSourceDir;

    public Issue204DestinationParentTests()
    {
        _mockHostResolver
            .Setup(r => r.ResolveRequired(It.IsAny<string>()))
            .Returns(new HostProfile { HostId = "local", ComputerName = "localhost" });

        _service = new FileTransferService(
            _mockChannel.Object,
            _mockHostResolver.Object,
            NullLogger<FileTransferService>.Instance);

        _tempSourceFile = Path.GetTempFileName();
        File.WriteAllText(_tempSourceFile, "test content");

        _tempSourceDir = Path.Combine(Path.GetTempPath(), $"hvmcp_issue204_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempSourceDir);
        File.WriteAllText(Path.Combine(_tempSourceDir, "a.txt"), "a");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempSourceFile)) File.Delete(_tempSourceFile); } catch { }
        try { if (Directory.Exists(_tempSourceDir)) Directory.Delete(_tempSourceDir, recursive: true); } catch { }
    }

    private static PowerShellHostResult Success(params object?[] output) =>
        new(Success: true, Output: output, Stderr: string.Empty, ExitCode: 0);

    private static PowerShellHostResult Failure(string stderr) =>
        new(Success: false, Output: Array.Empty<object?>(), Stderr: stderr, ExitCode: 1);

    // ───────────────────────────────────────────────────────────────────
    // T1 — happy path: ensure-parent runs first, then file copy succeeds.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T1_FileCopy_EnsureParentRunsBeforeCopy_ThenCopySucceeds()
    {
        var calls = new List<string>();
        string? capturedEnsureScript = null;
        IDictionary<string, object?>? capturedEnsureArgs = null;

        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, IDictionary<string, object?>?, CancellationToken>(
                (_, _, _, _, script, args, _) =>
                {
                    if (capturedEnsureScript is null)
                    {
                        capturedEnsureScript = script;
                        capturedEnsureArgs = args;
                        calls.Add("ensure");
                    }
                    else
                    {
                        calls.Add("verify");
                    }
                })
            .ReturnsAsync(() => calls.Count == 1 ? Success() : Success((long)12));

        _mockChannel
            .Setup(c => c.CopyToSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("copy"))
            .ReturnsAsync(Success());

        var result = await _service.CopyToGuestAsync(
            TestHostId, TestVmId,
            _tempSourceFile, @"C:\guest\new\sub\file.txt",
            isDirectory: false,
            username: "u", password: "p");

        calls.Should().Equal(new[] { "ensure", "copy", "verify" },
            "VC-DEST-D1: ensure-parent must run before Copy-Item -ToSession");
        result.Verified.Should().BeTrue();
        result.IsDirectory.Should().BeFalse();
        // PR #211 / IA-Gate 6 fix: switched from `Split-Path -Parent -LiteralPath`
        // + `New-Item -LiteralPath` (broken on Windows PowerShell 5.1 — see T14
        // and the EnsureParentDirectoryScript XML-doc on FileTransferService) to
        // [System.IO.Path]::GetDirectoryName + [System.IO.Directory]::CreateDirectory.
        capturedEnsureScript.Should().Contain("[System.IO.Path]::GetDirectoryName",
            "ensure-parent script must derive parent via the .NET BCL (VC-DEST-D5; PS 5.1-safe)");
        capturedEnsureScript.Should().Contain("[System.IO.Directory]::CreateDirectory",
            "ensure-parent must create parent via the .NET BCL (VC-DEST-D4; idempotent on existing dirs)");
        capturedEnsureArgs.Should().NotBeNull();
        capturedEnsureArgs!["dest"].Should().Be(@"C:\guest\new\sub\file.txt");
    }

    // ───────────────────────────────────────────────────────────────────
    // T2 — ACL / read-only blocks parent creation → typed exception.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T2_EnsureParentFails_ThrowsDestinationDirectoryUnavailableException()
    {
        const string destPath = @"C:\protected\area\file.txt";

        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failure("New-Item : Access to the path 'C:\\protected\\area' is denied."));

        var act = async () => await _service.CopyToGuestAsync(
            TestHostId, TestVmId,
            _tempSourceFile, destPath,
            isDirectory: false,
            username: "u", password: "p");

        var ex = await act.Should().ThrowAsync<DestinationDirectoryUnavailableException>();
        ex.Which.DestinationPath.Should().Be(destPath,
            "VC-DEST-D2: typed exception must carry the caller-supplied destination path verbatim");

        // Copy must NOT have been attempted after ensure-parent failure.
        _mockChannel.Verify(c => c.CopyToSessionAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ───────────────────────────────────────────────────────────────────
    // T3 (Issue #38 regression) — missing host source still FILE_NOT_FOUND.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T3_MissingHostSource_StillThrowsFileNotFound_NotDestDirMissing()
    {
        var bogusSource = Path.Combine(Path.GetTempPath(), $"definitely-missing-{Guid.NewGuid():N}.bin");

        var act = async () => await _service.CopyToGuestAsync(
            TestHostId, TestVmId,
            bogusSource, @"C:\guest\sub\file.txt",
            isDirectory: false,
            username: "u", password: "p");

        await act.Should().ThrowAsync<FileNotFoundException>(
            "Issue #38: missing host source must remain a FileNotFoundException (→ FILE_NOT_FOUND), " +
            "never DestinationDirectoryUnavailableException (→ DEST_DIR_MISSING)");

        // Mapper sanity: confirm the type still maps to FILE_NOT_FOUND.
        var mapper = new ErrorMapper();
        var resp = mapper.MapException(new FileNotFoundException("missing", bogusSource));
        resp.ErrorCode.Should().Be(ErrorCodes.FileNotFound);
        resp.ErrorCode.Should().NotBe(ErrorCodes.DestDirMissing);
    }

    // ───────────────────────────────────────────────────────────────────
    // T4 — idempotency: repeated successful ensure-parent calls do not error.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T4_RepeatedCalls_IntoSameValidParent_NoError()
    {
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
                // Odd calls are ensure-parent (Success with no output),
                // even calls are verify (returning a long size).
                return invokeCount % 2 == 1 ? Success() : Success((long)12);
            });

        _mockChannel
            .Setup(c => c.CopyToSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success());

        for (var i = 0; i < 3; i++)
        {
            var r = await _service.CopyToGuestAsync(
                TestHostId, TestVmId,
                _tempSourceFile, @"C:\guest\same\dir\file.txt",
                isDirectory: false,
                username: "u", password: "p");
            r.Verified.Should().BeTrue();
        }

        // 3 ensure + 3 verify = 6 InvokeScriptAsync invocations.
        invokeCount.Should().Be(6,
            "VC-DEST-D4: ensure-parent is invoked every call; New-Item -Force makes it a guest-side no-op when present");
    }

    // ───────────────────────────────────────────────────────────────────
    // T5 — root-path guard: destPath without a parent → ensure no-ops, copy proceeds.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T5_DestPathWithoutParent_EnsureNoOps_CopyProceeds()
    {
        var calls = new List<string>();

        _mockChannel
            .Setup(c => c.InvokeScriptAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("invoke"))
            .ReturnsAsync(() => calls.Count == 1 ? Success() : Success((long)12));

        _mockChannel
            .Setup(c => c.CopyToSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("copy"))
            .ReturnsAsync(Success());

        // Bare filename — Split-Path -Parent returns empty; script should return without throwing.
        var r = await _service.CopyToGuestAsync(
            TestHostId, TestVmId,
            _tempSourceFile, "foo.txt",
            isDirectory: false,
            username: "u", password: "p");

        r.Verified.Should().BeTrue();
        // ensure-parent is still invoked but is a no-op on the guest (VC-DEST-D5).
        // The relative ordering — ensure first, copy, then verify — must hold.
        calls.Should().Equal("invoke", "copy", "invoke");
    }

    // ───────────────────────────────────────────────────────────────────
    // T6 — directory transfer path is NOT touched by the new ensure step.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T6_DirectoryTransfer_UsesExistingZipStagedFlow_NotEnsureParent()
    {
        // Directory flow: invoke(BuildGuestTempPath) → copy(zip) → invoke(Expand-Archive)
        // First InvokeScript must return a guest path string (BuildGuestTempPathScript).
        // Second InvokeScript is the expand step which returns a numeric size.
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
                return invokeCount == 1
                    ? Success(@"C:\Users\u\AppData\Local\Temp\hvmcp_x.zip")
                    : Success((long)42);
            });

        _mockChannel
            .Setup(c => c.CopyToSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success());

        var r = await _service.CopyToGuestAsync(
            TestHostId, TestVmId,
            _tempSourceDir, @"C:\guest\dst",
            isDirectory: true,
            username: "u", password: "p");

        r.IsDirectory.Should().BeTrue();
        r.Verified.Should().BeTrue();
        // Exactly two InvokeScript calls: BuildGuestTempPath + ExpandAndCleanup.
        // If the new ensure-parent step had leaked into the directory path, we'd see 3+.
        invokeCount.Should().Be(2,
            "VC-DEST-D1 applies ONLY to the single-file flow; the directory flow's behavior is unchanged");
    }

    // ───────────────────────────────────────────────────────────────────
    // T7 — ErrorMapper integration: bare typed exception → DEST_DIR_MISSING.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public void T7_ErrorMapper_TypedException_MapsToDestDirMissing()
    {
        var mapper = new ErrorMapper();
        var ex = new DestinationDirectoryUnavailableException(
            @"C:\guest\sub\file.txt",
            "ensure-parent failed",
            inner: new InvalidOperationException("ACL"));

        var resp = mapper.MapException(ex);

        resp.Success.Should().BeFalse();
        resp.ErrorCode.Should().Be(ErrorCodes.DestDirMissing);
        resp.Error.Should().Contain(@"C:\guest\sub\file.txt");
        resp.Error.Should().Contain("missing or not creatable");
        resp.Data.Should().BeNull();
    }

    // ───────────────────────────────────────────────────────────────────
    // T10 — anti-spoof: marker substring in other exception does NOT classify
    // as DEST_DIR_MISSING. Proves type-based-only classification (VC-DEST-D8 rule 3).
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public void T10_AntiSpoof_MarkerInArbitraryExceptionMessage_DoesNotMapToDestDirMissing()
    {
        var mapper = new ErrorMapper();
        // An attacker / unrelated code path emits the marker substring in an
        // InvalidOperationException. Classification must NOT use this substring.
        var spoof = new InvalidOperationException(
            $"random failure {FileTransferService.DiagnosticEnsureParentFailedMarker} embedded in message");

        var resp = mapper.MapException(spoof);

        resp.ErrorCode.Should().NotBe(ErrorCodes.DestDirMissing,
            "VC-DEST-D8 rule 3: ErrorMapper must classify by exception type, never by the diagnostic marker substring");
        resp.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "InvalidOperationException without a more specific matcher falls through to COMMAND_FAILED");
    }

    // ───────────────────────────────────────────────────────────────────
    // T11 — wrapper: PowerShellDirectChannelException wrapping the typed exception
    // is unwrapped by ErrorMapper and classified as DEST_DIR_MISSING.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public void T11_PowerShellDirectChannelException_WrappingTyped_IsUnwrappedToDestDirMissing()
    {
        var mapper = new ErrorMapper();
        var inner = new DestinationDirectoryUnavailableException(
            @"C:\guest\sub\file.txt",
            "ensure-parent failed",
            inner: new InvalidOperationException("ACL"));
        var wrapper = new PowerShellDirectChannelException("redacted top-level message", inner);

        var resp = mapper.MapException(wrapper);

        resp.ErrorCode.Should().Be(ErrorCodes.DestDirMissing,
            "ErrorMapper unwraps PowerShellDirectChannelException and classifies by the inner exception's type");
        // The unwrap path overwrites the inner-classified message with the wrapper's
        // already-redacted top-level message (see ErrorMapper PSD-D8 unwrap contract).
        resp.Error.Should().Be("redacted top-level message");
    }

    // ───────────────────────────────────────────────────────────────────
    // T14 — IA-Gate 6 (PR #211) regression: execute the EXACT production
    // EnsureParentDirectoryScript string against the real powershell.exe
    // (Windows PowerShell 5.1) host. The Moq-based tests above stub
    // IPowerShellDirectChannel and therefore never parse or run the script
    // body, so they cannot catch syntax / parameter-set bugs.
    //
    // Background: the v1 script used `Split-Path -Parent -LiteralPath` +
    // `New-Item -LiteralPath`. Both are broken on Windows PowerShell 5.1
    // (AmbiguousParameterSet and NamedParameterNotFound respectively), which
    // converted every single-file copy with a non-empty parent into
    // DEST_DIR_MISSING. The current implementation uses
    // [System.IO.Path]::GetDirectoryName + [System.IO.Directory]::CreateDirectory
    // to sidestep both cmdlet parameter-set hazards.
    //
    // This test executes the script three times to verify:
    //   (a) it parses and runs without errors,
    //   (b) the parent directory is actually created,
    //   (c) re-running is idempotent (no error when parent already exists),
    //   (d) a bare filename (no parent segment) is a no-op (VC-DEST-D5).
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T14_EnsureParentDirectoryScript_ExecutesAgainstRealPowerShell_CreatesParentDir()
    {
        // Verify the production script text is what we expect to execute.
        FileTransferService.EnsureParentDirectoryScript.Should().Contain(
            "[System.IO.Directory]::CreateDirectory",
            "T14 must exercise the .NET-BCL implementation, not the broken cmdlet form");
        FileTransferService.EnsureParentDirectoryScript.Should().NotContain(
            "New-Item -ItemType Directory -Force -LiteralPath",
            "regression guard: the broken Windows-PowerShell-5.1 form must not return");

        var testRoot = Path.Combine(Path.GetTempPath(), $"hvmcp_issue204_t14_{Guid.NewGuid():N}");
        var parentDir = Path.Combine(testRoot, "deeply", "nested", "parent");
        var dest = Path.Combine(parentDir, "file.txt");

        try
        {
            // Run 1 — must create the parent chain.
            var (exit1, stdout1, stderr1) = RunScriptInPowerShell(
                FileTransferService.EnsureParentDirectoryScript, dest);
            exit1.Should().Be(0, $"first invocation must succeed; stderr=<{stderr1}> stdout=<{stdout1}>");
            stderr1.Should().BeEmpty("first invocation must not emit any error stream content");
            Directory.Exists(parentDir).Should().BeTrue(
                "the ensure-parent script must create the parent directory chain");

            // Run 2 — idempotent (parent already exists, must remain a no-op success).
            var (exit2, stdout2, stderr2) = RunScriptInPowerShell(
                FileTransferService.EnsureParentDirectoryScript, dest);
            exit2.Should().Be(0, $"idempotent invocation must succeed; stderr=<{stderr2}> stdout=<{stdout2}>");
            stderr2.Should().BeEmpty("idempotent invocation must not emit any error stream content");
            Directory.Exists(parentDir).Should().BeTrue("directory must still exist after idempotent re-run");

            // Run 3 — bare filename (no parent segment): must be a no-op per VC-DEST-D5.
            var (exit3, _, stderr3) = RunScriptInPowerShell(
                FileTransferService.EnsureParentDirectoryScript, "bare-file.txt");
            exit3.Should().Be(0, $"bare-filename invocation must be a successful no-op; stderr=<{stderr3}>");
            stderr3.Should().BeEmpty("bare-filename path must not emit any error stream content");
        }
        finally
        {
            try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Executes the given PowerShell script body against Windows PowerShell 5.1
    /// (powershell.exe — the runtime that originally tripped the broken script)
    /// with a single positional <c>$dest</c> argument. Returns exit code, stdout,
    /// and stderr.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunScriptInPowerShell(
        string scriptBody, string destArg)
    {
        // Wrap the script body in a scriptblock literal so powershell.exe parses
        // the EXACT production text (param block + body) and binds $destArg to $dest.
        // EncodedCommand sidesteps quoting hazards across the cmd.exe boundary.
        var wrapped = $"& {{ {scriptBody} }} {EscapeForSingleQuoted(destArg)}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrapped));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell.exe");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Wraps a value as a PowerShell single-quoted string literal, escaping any
    /// embedded single quotes per PS literal rules.
    /// </summary>
    private static string EscapeForSingleQuoted(string value) =>
        "'" + value.Replace("'", "''") + "'";
}
