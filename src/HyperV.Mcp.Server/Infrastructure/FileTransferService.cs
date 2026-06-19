using System.Globalization;
using System.IO.Compression;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Manages file transfers between host and guest VMs over <see cref="IPowerShellDirectChannel"/>.
/// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D10, FT-D11, FT-D12, FT-D13.
///
/// Design (Phase 2):
/// - Single-file transfers use <c>Copy-Item -ToSession</c> / <c>Copy-Item -FromSession</c>
///   via the persistent PSSession owned by the channel (FT-D10, FT-D11).
/// - Post-copy verification reads <c>(Get-Item $dest).Length</c> over the same channel
///   and surfaces the byte size in <see cref="FileTransferResult"/> (FT-D12).
/// - Directory transfers are zip-staged: local <c>Compress-Archive</c> + <c>Copy-Item -ToSession</c>
///   + guest <c>Expand-Archive</c> for to-guest, mirrored for from-guest (FT-D13).
/// - The legacy <c>Copy-VMFile</c> / base64 byte-stream / WinRM-TLS-probe paths are removed.
///
/// Timeout policy (Issue #52, Gate 6 re-review):
/// - This service intentionally calls the timeoutless channel methods
///   (<see cref="IPowerShellDirectChannel.InvokeScriptAsync"/>,
///   <see cref="IPowerShellDirectChannel.CopyToSessionAsync"/>,
///   <see cref="IPowerShellDirectChannel.CopyFromSessionAsync"/>) rather than the
///   <c>*WithTimeoutAsync</c> overloads, because file-transfer duration is bounded
///   by file size and network throughput — not a fixed wall-clock budget.
/// - Caller cancellation propagates via the <c>ct</c> parameter on every public
///   method; that is the only deadline applied to a transfer.
/// - If a per-transfer timeout becomes a requirement, add an overload to
///   <see cref="IFileTransferService"/> that accepts <c>timeoutSeconds</c> and routes
///   through the corresponding <c>IPowerShellDirectChannel.*WithTimeoutAsync</c>
///   method (a future-issue concern; do not back-port here).
/// </summary>
public class FileTransferService : IFileTransferService
{
    private readonly IPowerShellDirectChannel _channel;
    private readonly IHostResolver _hostResolver;
    private readonly ILogger<FileTransferService> _logger;

    public FileTransferService(
        IPowerShellDirectChannel channel,
        IHostResolver hostResolver,
        ILogger<FileTransferService> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<FileTransferResult> CopyToGuestAsync(
        string hostId, string vmId, string sourcePath, string destPath,
        bool isDirectory = false,
        string? username = null, string? password = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId, nameof(hostId));
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId, nameof(vmId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath, nameof(sourcePath));
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath, nameof(destPath));

        // Phase 1 carry-over: enforce local-only host before credential resolution
        // so remote hosts get NotSupportedException instead of MissingCredentialsException.
        var profile = _hostResolver.ResolveRequired(hostId);
        if (!profile.IsLocal)
            throw new NotSupportedException(
                $"Remote host '{hostId}' is not supported for file transfer in Phase 1.");

        var (resolvedUsername, resolvedPassword) = CredentialResolver.ResolveCredentials(username, password);

        // Validate vmId is a GUID to prevent injection downstream.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Validate local source up front so missing-source errors surface as FILE_NOT_FOUND.
        var sourceIsFile = File.Exists(sourcePath);
        var sourceIsDir = !sourceIsFile && Directory.Exists(sourcePath);
        if (!sourceIsFile && !sourceIsDir)
        {
            throw new FileNotFoundException(
                $"Source path not found on host: {sourcePath}", sourcePath);
        }

        // Reconcile caller-declared isDirectory against on-disk reality. A mismatch is a usage error.
        if (isDirectory && !sourceIsDir)
        {
            throw new InvalidOperationException(
                $"isDirectory=true was specified but '{sourcePath}' is not a directory.");
        }

        if (sourceIsFile)
        {
            return await CopyFileToGuestAsync(
                hostId, safeVmId, resolvedUsername, resolvedPassword,
                sourcePath, destPath, ct).ConfigureAwait(false);
        }

        return await CopyDirectoryToGuestAsync(
            hostId, safeVmId, resolvedUsername, resolvedPassword,
            sourcePath, destPath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FileTransferResult> CopyFromGuestAsync(
        string hostId, string vmId, string sourcePath, string destPath,
        string? username = null, string? password = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId, nameof(hostId));
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId, nameof(vmId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath, nameof(sourcePath));
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath, nameof(destPath));

        var profile = _hostResolver.ResolveRequired(hostId);
        if (!profile.IsLocal)
            throw new NotSupportedException(
                $"Remote host '{hostId}' is not supported for file transfer in Phase 1.");

        var (resolvedUsername, resolvedPassword) = CredentialResolver.ResolveCredentials(username, password);
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Probe whether the guest source is a file or directory and (for files) capture size.
        var probeArgs = new Dictionary<string, object?> { ["path"] = sourcePath };
        var probe = await _channel.InvokeScriptAsync(
            hostId, safeVmId, resolvedUsername, resolvedPassword,
            ProbeGuestPathScript, probeArgs, ct).ConfigureAwait(false);

        if (!probe.Success)
        {
            ThrowForGuestProbeFailure(hostId, safeVmId, sourcePath, probe.Stderr);
        }

        var probeOutput = ParseStringOutput(probe);
        if (string.IsNullOrEmpty(probeOutput))
        {
            throw new InvalidOperationException(
                $"Failed to probe guest source path '{sourcePath}': empty result from guest.");
        }

        if (string.Equals(probeOutput, "dir", StringComparison.OrdinalIgnoreCase))
        {
            return await CopyDirectoryFromGuestAsync(
                hostId, safeVmId, resolvedUsername, resolvedPassword,
                sourcePath, destPath, ct).ConfigureAwait(false);
        }

        if (!long.TryParse(probeOutput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var guestFileSize))
        {
            throw new InvalidOperationException(
                $"Failed to probe guest source path '{sourcePath}': unexpected probe output.");
        }

        return await CopyFileFromGuestAsync(
            hostId, safeVmId, resolvedUsername, resolvedPassword,
            sourcePath, destPath, guestFileSize, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // To-guest helpers
    // ---------------------------------------------------------------------

    private async Task<FileTransferResult> CopyFileToGuestAsync(
        string hostId, string vmId, string username, string password,
        string localSourcePath, string guestDestinationPath, CancellationToken ct)
    {
        _logger.LogInformation(
            "Copying file from host to guest {HostId}:{VmId}: {Source} → {Dest}",
            hostId, vmId, localSourcePath, guestDestinationPath);

        // Issue #204 / VC-DEST-D1: auto-create the destination's parent directory on the
        // guest before Copy-Item -ToSession. Idempotent (-Force, VC-DEST-D4); skipped for
        // root-only paths like "foo.txt" with no parent (VC-DEST-D5). Failure here is
        // classified as DEST_DIR_MISSING via a typed exception so callers can distinguish
        // "destination parent missing/uncreatable" from "source missing" (Issue #38).
        var ensureArgs = new Dictionary<string, object?> { ["dest"] = guestDestinationPath };
        // Issue #204 / PR #211 review feedback: the catch block intentionally does NOT
        // wrap *any* non-cancellation exception. Channel/transport failures
        // (OperationCanceledException, CommandTimeoutException,
        // PowerShellDirectChannelException) must propagate so ErrorMapper can classify
        // them under their correct error codes (CANCELLED / COMMAND_TIMEOUT /
        // AUTH_FAILED / SESSION_FAILED / …). Only explicit ensure-parent script
        // failures — surfaced as a non-Success PowerShellHostResult below — are mapped
        // to DEST_DIR_MISSING.
        var ensure = await _channel.InvokeScriptAsync(
            hostId, vmId, username, password,
            EnsureParentDirectoryScript, ensureArgs, ct).ConfigureAwait(false);

        // The channel contract is non-null; a null here indicates a contract violation
        // (e.g. a malformed test mock). Fail fast rather than silently skipping the
        // ensure-parent check, which would let the copy proceed against a possibly
        // non-existent destination parent and mask the real misconfiguration.
        if (ensure is null)
        {
            throw new InvalidOperationException(
                $"PowerShellDirectChannel.InvokeScriptAsync returned null for the ensure-parent step on {hostId}:{vmId} (contract violation).");
        }

        if (!ensure.Success)
        {
            // VC-DEST-D8 rule 3: marker is for log/diagnostic correlation only.
            _logger.LogWarning(
                "{Marker} ensure-parent script failed on {HostId}:{VmId} for dest '{Dest}': {Stderr}",
                DiagnosticEnsureParentFailedMarker,
                hostId, vmId, guestDestinationPath, ensure.Stderr.Trim());
            throw new DestinationDirectoryUnavailableException(
                guestDestinationPath,
                $"Failed to ensure destination parent directory exists on guest {vmId} for path '{guestDestinationPath}': {ensure.Stderr.Trim()}",
                inner: new InvalidOperationException(ensure.Stderr.Trim()));
        }

        var copy = await _channel.CopyToSessionAsync(
            hostId, vmId, username, password,
            localSourcePath, guestDestinationPath, ct).ConfigureAwait(false);

        if (!copy.Success)
        {
            // Channel already redacts the password from stderr.
            throw new InvalidOperationException(
                $"Failed to copy file to guest {vmId}: {copy.Stderr.Trim()}");
        }

        var verifyArgs = new Dictionary<string, object?> { ["path"] = guestDestinationPath };
        var verify = await _channel.InvokeScriptAsync(
            hostId, vmId, username, password,
            VerifyFileScript, verifyArgs, ct).ConfigureAwait(false);

        if (!verify.Success)
        {
            throw new InvalidOperationException(
                $"File transfer verification failed for {guestDestinationPath}: {verify.Stderr.Trim()}");
        }

        long destSize;
        try
        {
            destSize = ParseLongOutput(verify);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"File transfer verification failed for {guestDestinationPath}: {ex.Message}", ex);
        }

        return new FileTransferResult
        {
            SourcePath = localSourcePath,
            DestPath = guestDestinationPath,
            IsDirectory = false,
            BytesTransferred = destSize,
            FileCount = 1,
            Verified = true,
        };
    }

    private async Task<FileTransferResult> CopyDirectoryToGuestAsync(
        string hostId, string vmId, string username, string password,
        string localSourcePath, string guestDestinationPath, CancellationToken ct)
    {
        _logger.LogInformation(
            "Copying directory from host to guest {HostId}:{VmId} (zip-staged): {Source} → {Dest}",
            hostId, vmId, localSourcePath, guestDestinationPath);

        var transferGuid = Guid.NewGuid().ToString("N");
        var localZip = Path.Combine(Path.GetTempPath(), $"hvmcp_{transferGuid}.zip");
        var guestZipName = $"hvmcp_{transferGuid}.zip";

        using var localCleanup = new ZipCleanup(localZip, _logger);

        // Compress locally. Synchronous; acceptable for typical sizes — large
        // directory support is tracked as a follow-up.
        if (File.Exists(localZip))
        {
            File.Delete(localZip);
        }
        ZipFile.CreateFromDirectory(
            localSourcePath, localZip, CompressionLevel.Fastest, includeBaseDirectory: false);

        // Resolve a deterministic guest-side temp path (uses guest's $env:TEMP).
        var guestPathArgs = new Dictionary<string, object?> { ["name"] = guestZipName };
        var guestPathResult = await _channel.InvokeScriptAsync(
            hostId, vmId, username, password,
            BuildGuestTempPathScript, guestPathArgs, ct).ConfigureAwait(false);

        if (!guestPathResult.Success)
        {
            throw new InvalidOperationException(
                $"Failed to resolve guest temp path on guest {vmId}: {guestPathResult.Stderr.Trim()}");
        }

        var guestZip = ParseStringOutput(guestPathResult);
        if (string.IsNullOrWhiteSpace(guestZip))
        {
            throw new InvalidOperationException(
                $"Failed to resolve guest temp path on guest {vmId}: empty result.");
        }

        try
        {
            var copy = await _channel.CopyToSessionAsync(
                hostId, vmId, username, password,
                localZip, guestZip, ct).ConfigureAwait(false);

            if (!copy.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to copy directory archive to guest {vmId}: {copy.Stderr.Trim()}");
            }

            var expandArgs = new Dictionary<string, object?>
            {
                ["zip"] = guestZip,
                ["dest"] = guestDestinationPath,
            };
            var expand = await _channel.InvokeScriptAsync(
                hostId, vmId, username, password,
                ExpandAndCleanupOnGuestScript, expandArgs, ct).ConfigureAwait(false);

            if (!expand.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to expand directory archive on guest {vmId}: {expand.Stderr.Trim()}");
            }

            long totalBytes;
            try
            {
                totalBytes = ParseLongOutput(expand);
            }
            catch (InvalidOperationException)
            {
                // Empty directory legitimately produces no Sum; treat as 0 bytes.
                totalBytes = 0;
            }

            return new FileTransferResult
            {
                SourcePath = localSourcePath,
                DestPath = guestDestinationPath,
                IsDirectory = true,
                BytesTransferred = totalBytes,
                FileCount = 0,
                Verified = true,
            };
        }
        catch
        {
            // Best-effort cleanup of the guest-side zip if we threw between copy and expand.
            await BestEffortGuestCleanupAsync(hostId, vmId, username, password, guestZip, ct)
                .ConfigureAwait(false);
            throw;
        }
    }

    // ---------------------------------------------------------------------
    // From-guest helpers
    // ---------------------------------------------------------------------

    private async Task<FileTransferResult> CopyFileFromGuestAsync(
        string hostId, string vmId, string username, string password,
        string guestSourcePath, string localDestinationPath, long expectedSize,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Copying file from guest {HostId}:{VmId} to host: {Source} → {Dest}",
            hostId, vmId, guestSourcePath, localDestinationPath);

        // Ensure destination directory exists locally.
        var destDir = Path.GetDirectoryName(localDestinationPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        var copy = await _channel.CopyFromSessionAsync(
            hostId, vmId, username, password,
            guestSourcePath, localDestinationPath, ct).ConfigureAwait(false);

        if (!copy.Success)
        {
            throw new InvalidOperationException(
                $"Failed to copy file from guest {vmId}: {copy.Stderr.Trim()}");
        }

        if (!File.Exists(localDestinationPath))
        {
            throw new InvalidOperationException(
                $"File transfer verification failed for {localDestinationPath}: destination does not exist.");
        }

        var actualSize = new FileInfo(localDestinationPath).Length;
        if (actualSize != expectedSize)
        {
            throw new InvalidOperationException(
                $"File transfer verification failed for {localDestinationPath}: " +
                $"expected {expectedSize} bytes, got {actualSize} bytes.");
        }

        return new FileTransferResult
        {
            SourcePath = guestSourcePath,
            DestPath = localDestinationPath,
            IsDirectory = false,
            BytesTransferred = actualSize,
            FileCount = 1,
            Verified = true,
        };
    }

    private async Task<FileTransferResult> CopyDirectoryFromGuestAsync(
        string hostId, string vmId, string username, string password,
        string guestSourcePath, string localDestinationPath, CancellationToken ct)
    {
        _logger.LogInformation(
            "Copying directory from guest {HostId}:{VmId} to host (zip-staged): {Source} → {Dest}",
            hostId, vmId, guestSourcePath, localDestinationPath);

        var transferGuid = Guid.NewGuid().ToString("N");
        var localZip = Path.Combine(Path.GetTempPath(), $"hvmcp_{transferGuid}.zip");
        var guestZipName = $"hvmcp_{transferGuid}.zip";

        // Resolve guest-side temp path.
        var guestPathArgs = new Dictionary<string, object?> { ["name"] = guestZipName };
        var guestPathResult = await _channel.InvokeScriptAsync(
            hostId, vmId, username, password,
            BuildGuestTempPathScript, guestPathArgs, ct).ConfigureAwait(false);

        if (!guestPathResult.Success)
        {
            throw new InvalidOperationException(
                $"Failed to resolve guest temp path on guest {vmId}: {guestPathResult.Stderr.Trim()}");
        }

        var guestZip = ParseStringOutput(guestPathResult);
        if (string.IsNullOrWhiteSpace(guestZip))
        {
            throw new InvalidOperationException(
                $"Failed to resolve guest temp path on guest {vmId}: empty result.");
        }

        using var localCleanup = new ZipCleanup(localZip, _logger);

        try
        {
            var compressArgs = new Dictionary<string, object?>
            {
                ["src"] = guestSourcePath,
                ["zip"] = guestZip,
            };
            var compress = await _channel.InvokeScriptAsync(
                hostId, vmId, username, password,
                CompressOnGuestScript, compressArgs, ct).ConfigureAwait(false);

            if (!compress.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to compress directory on guest {vmId}: {compress.Stderr.Trim()}");
            }

            var copy = await _channel.CopyFromSessionAsync(
                hostId, vmId, username, password,
                guestZip, localZip, ct).ConfigureAwait(false);

            if (!copy.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to copy directory archive from guest {vmId}: {copy.Stderr.Trim()}");
            }
        }
        finally
        {
            // Best-effort guest-side zip cleanup regardless of outcome.
            await BestEffortGuestCleanupAsync(hostId, vmId, username, password, guestZip, ct)
                .ConfigureAwait(false);
        }

        // Ensure local destination exists.
        Directory.CreateDirectory(localDestinationPath);

        ZipFile.ExtractToDirectory(localZip, localDestinationPath, overwriteFiles: true);

        long totalBytes = 0;
        foreach (var f in Directory.EnumerateFiles(localDestinationPath, "*", SearchOption.AllDirectories))
        {
            totalBytes += new FileInfo(f).Length;
        }

        return new FileTransferResult
        {
            SourcePath = guestSourcePath,
            DestPath = localDestinationPath,
            IsDirectory = true,
            BytesTransferred = totalBytes,
            FileCount = 0,
            Verified = true,
        };
    }

    private async Task BestEffortGuestCleanupAsync(
        string hostId, string vmId, string username, string password,
        string guestPath, CancellationToken ct)
    {
        try
        {
            var args = new Dictionary<string, object?> { ["p"] = guestPath };
            await _channel.InvokeScriptAsync(
                hostId, vmId, username, password,
                BestEffortRemoveGuestPathScript, args, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Do NOT pass `ex` directly to LogWarning — PowerShellDirectChannelException
            // carries the original inner exception with potentially-credentialled message
            // text, and common logging providers serialize the full inner-exception chain.
            // Log only safe surface metadata: the exception type name + the already-redacted
            // top-level message. The channel's wrapper guarantees ex.Message is redacted;
            // non-channel exceptions caught here (e.g. OperationCanceledException) have
            // benign top-level messages.
            _logger.LogWarning(
                "Best-effort cleanup of guest path on {HostId}:{VmId} failed: {ExceptionType}: {Message}",
                hostId, vmId, ex.GetType().Name, ex.Message);
        }
    }

    private static void ThrowForGuestProbeFailure(
        string hostId, string vmId, string guestSourcePath, string stderr)
    {
        var msg = (stderr ?? string.Empty).Trim();

        if (msg.Contains("VM not found", StringComparison.OrdinalIgnoreCase) ||
            (msg.Contains("virtual machine", StringComparison.OrdinalIgnoreCase) &&
             (msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
              msg.Contains("could not find", StringComparison.OrdinalIgnoreCase) ||
              msg.Contains("not found", StringComparison.OrdinalIgnoreCase))))
        {
            throw new VmNotFoundException(hostId, vmId);
        }

        if (msg.Contains("Cannot find path", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("not find", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                $"Source file not found on guest: {guestSourcePath}", guestSourcePath);
        }

        throw new InvalidOperationException(
            $"Failed to probe guest source path '{guestSourcePath}' on {hostId}:{vmId}: {msg}");
    }

    // ---------------------------------------------------------------------
    // Output parsing helpers
    // ---------------------------------------------------------------------

    private static long ParseLongOutput(PowerShellHostResult r)
    {
        foreach (var o in r.Output)
        {
            if (o is null) continue;
            var s = o.ToString();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
        }
        throw new InvalidOperationException("Expected numeric output not found in PowerShell result.");
    }

    private static string? ParseStringOutput(PowerShellHostResult r)
    {
        foreach (var o in r.Output)
        {
            if (o is null) continue;
            var s = o.ToString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Inner script bodies
    // ---------------------------------------------------------------------

    /// <summary>
    /// Issue #204 / VC-DEST-D8: diagnostic marker emitted on the ensure-parent
    /// catch path for log correlation. <b>ErrorMapper MUST NOT reference this
    /// constant</b> — classification of <see cref="DestinationDirectoryUnavailableException"/>
    /// is type-based only (anti-spoof: an arbitrary exception whose message merely
    /// contains this substring must NOT be classified as <c>DEST_DIR_MISSING</c>).
    /// </summary>
    internal const string DiagnosticEnsureParentFailedMarker = "[VM_COPY_FILE_ENSURE_PARENT_FAILED]";

    /// <summary>
    /// Issue #204 / VC-DEST-D1, D4, D5: idempotently ensure the parent directory of
    /// the guest destination path exists. No-op when the destination has no parent
    /// segment (e.g. <c>foo.txt</c>, VC-DEST-D5). <see cref="System.IO.Directory.CreateDirectory(string)"/>
    /// is a no-op when the directory already exists (VC-DEST-D4). Failures throw
    /// inside the guest PSSession and are surfaced via stderr / a non-Success result.
    /// <para>
    /// Implementation note (PR #211 / IA-Gate 6 fix-loop): the previous incarnation
    /// used <c>Split-Path -Parent -LiteralPath</c> + <c>New-Item -LiteralPath</c>.
    /// Both are broken on Windows PowerShell 5.1 — the former triggers
    /// <c>AmbiguousParameterSet</c> (the <c>-Parent</c> and <c>-LiteralPath</c>
    /// switches collide), and the latter fails with <c>NamedParameterNotFound</c>
    /// because <c>New-Item</c> in the 5.1 Management module only exposes <c>-Path</c>.
    /// Falling through to the .NET BCL <see cref="System.IO.Path.GetDirectoryName(string)"/>
    /// + <see cref="System.IO.Directory.CreateDirectory(string)"/> sidesteps both
    /// parameter-set hazards entirely and handles literal paths naturally (no globbing).
    /// Regression: <c>T14_EnsureParentDirectoryScript_ExecutesAgainstRealPowerShell_CreatesParentDir</c>.
    /// </para>
    /// </summary>
    // Internal (was private) so the IA-Gate 6 regression test
    // (Issue204DestinationParentTests.T14) can execute the EXACT production
    // script text against a real in-process PowerShell instance.
    internal const string EnsureParentDirectoryScript = @"
param($dest)
$parent = [System.IO.Path]::GetDirectoryName($dest)
if ([string]::IsNullOrEmpty($parent)) { return }
[void][System.IO.Directory]::CreateDirectory($parent)
";

    private const string VerifyFileScript = @"
param($path)
(Get-Item -LiteralPath $path -ErrorAction Stop).Length
";

    private const string ProbeGuestPathScript = @"
param($path)
$i = Get-Item -LiteralPath $path -ErrorAction Stop
if ($i.PSIsContainer) { 'dir' } else { $i.Length }
";

    private const string BuildGuestTempPathScript = @"
param($name)
[System.IO.Path]::Combine($env:TEMP, $name)
";

    private const string CompressOnGuestScript = @"
param($src, $zip)
Compress-Archive -Path (Join-Path $src '*') -DestinationPath $zip -CompressionLevel Fastest -Force
(Get-Item -LiteralPath $zip).Length
";

    private const string ExpandAndCleanupOnGuestScript = @"
param($zip, $dest)
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Expand-Archive -Path $zip -DestinationPath $dest -Force
try { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue } catch {}
(Get-ChildItem -LiteralPath $dest -Recurse -File | Measure-Object -Property Length -Sum).Sum
";

    private const string BestEffortRemoveGuestPathScript = @"
param($p)
Remove-Item -LiteralPath $p -Force -Recurse -ErrorAction SilentlyContinue
$true
";

    // ---------------------------------------------------------------------
    // Local zip cleanup helper
    // ---------------------------------------------------------------------

    private sealed class ZipCleanup : IDisposable
    {
        private readonly string _path;
        private readonly ILogger _logger;

        public ZipCleanup(string path, ILogger logger)
        {
            _path = path;
            _logger = logger;
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temporary zip {Path}.", _path);
            }
        }
    }
}
