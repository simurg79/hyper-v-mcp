using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// SSH-exec implementation of the guest channel facade for Linux guests (issue #209,
/// LGS-SSH-D1/D2). Mirrors the 5-method <see cref="IPowerShellDirectChannel"/> shape and the
/// per-(hostId,vmId) serialized session lifecycle of <see cref="PowerShellDirectChannel"/>,
/// but runs the caller's actual command under the guest's shell over an SSH exec channel
/// against an already-reachable, already-running Ubuntu 24.04 guest.
///
/// <para>The <see cref="IPowerShellDirectChannel"/> facade forwards the Windows PowerShell JSON
/// wrapper as <c>script</c> and the real request in <c>args</c> (<c>cmd</c>/<c>base64Script</c> +
/// <c>sh</c>). On the Linux path the PowerShell wrapper is meaningless, so this channel IGNORES
/// <c>script</c> and instead executes the actual command/script under the guest shell, then
/// re-emits a <c>{Stdout,Stderr,ExitCode,DurationMs}</c> JSON envelope in
/// <see cref="PowerShellHostResult.Output"/> so <see cref="CommandExecutor.ParseJsonResult"/>
/// produces an unchanged <see cref="Models.CommandResult"/> (LGS-SSH-D1).</para>
///
/// <para>File transfer runs over SFTP on the same session store entry as exec, and the transfer
/// service's PowerShell script constants are translated into POSIX equivalents by the identity of
/// the constant. A translated transfer intent emits a BARE value in
/// <see cref="PowerShellHostResult.Output"/> — the size, path, or token the service parses —
/// rather than the exec path's JSON envelope.
/// See /myplans/remoting/linux-guest-support/linux-guest-file-transfer-design.md — LGS-XFER-D0/D2.</para>
///
/// <para>Redaction (LGS-SSH-D6): SSH stderr AND every SSH error/exception string are routed
/// through <see cref="StderrSpillHelper.RedactDefensively"/> plus literal-password stripping,
/// so passwords and any SSH private-key/PEM material are removed before reaching a
/// <see cref="Models.CommandResult"/>, error string, or log.</para>
/// See /myplans/remoting/linux-guest-support/linux-ssh-exec-first-slice-design.md.
/// </summary>
public sealed class SshGuestChannel : IPowerShellDirectChannel
{
    private readonly ISshSessionStore _sessionStore;
    private readonly ILogger<SshGuestChannel> _logger;

    // LGS-SSH (🟡 concurrency gate): serialize InvokeScript* calls per (hostId,vmId) so
    // concurrent operations on the same guest share the single cached SSH client safely.
    // SshSessionStore.GetOrCreateAsync only serializes get-or-create, not the exec that
    // follows. Mirrors the per-key semaphore pattern in SessionStore/SshSessionStore.
    //
    // Each gate is reference-counted (OperationGate) so eviction can remove AND dispose the
    // per-key semaphore once no operation is using it, matching the gate lifecycle to the
    // session lifecycle without leaking one SemaphoreSlim per unique (hostId,vmId). A call
    // arriving concurrently with eviction either acquires a ref before removal (its release
    // performs the disposal) or re-loops to a fresh gate — it never waits on or disposes a
    // semaphore twice, and never uses a disposed one.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OperationGate>
        OperationGates = new();

    /// <summary>
    /// Reference-counted holder for a per-key <see cref="SemaphoreSlim"/>. The count tracks
    /// live acquirers plus the one implicit reference held by dictionary membership; the last
    /// reference to drop after the gate has been removed from the dictionary disposes the
    /// semaphore. This prevents both the leak (never-disposed gates) and the race (disposing a
    /// semaphore another call is about to wait on).
    /// </summary>
    private sealed class OperationGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        private int _referenceCount = 1;
        private int _removedFromMap;

        /// <summary>Tries to add a live reference; false if the gate is being torn down.</summary>
        internal bool TryAcquireReference()
        {
            while (true)
            {
                var current = Volatile.Read(ref _referenceCount);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
                    return true;
            }
        }

        /// <summary>Drops a reference; disposes the semaphore when the last reference is gone.</summary>
        internal void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                Semaphore.Dispose();
            }
        }

        /// <summary>Drops the dictionary's implicit reference exactly once (on eviction).</summary>
        internal void ReleaseMapReference()
        {
            if (Interlocked.Exchange(ref _removedFromMap, 1) == 0)
            {
                ReleaseReference();
            }
        }
    }

    public SshGuestChannel(ISshSessionStore sessionStore, ILogger<SshGuestChannel> logger)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<PowerShellHostResult> InvokeScriptAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string script,
        IDictionary<string, object?>? args = null,
        CancellationToken ct = default)
        => InvokeScriptWithTimeoutAsync(hostId, vmId, username, password, script, args, timeoutSeconds: 0, ct);

    /// <inheritdoc />
    public async Task<PowerShellHostResult> InvokeScriptWithTimeoutAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string script,
        IDictionary<string, object?>? args,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        var transferIntent = TransferIntentTranslator.Recognize(script);

        // Reachable only when a transfer script constant is renamed or re-bodied without updating
        // the translator. Falling through to the exec path would run an empty command and report
        // success while moving nothing, so refuse loudly instead.
        // See myplans/remoting/linux-guest-support/linux-guest-file-transfer-design.md.
        if (transferIntent is null && !HasExplicitCommand(args) && HasTransferShapedArgs(args))
        {
            throw new PowerShellDirectChannelException(
                $"SshGuestChannel received a file-transfer-shaped request for guest '{vmId}' whose " +
                "script matches no known transfer intent, so it cannot be translated or executed.");
        }

        var remoteCommand = BuildRemoteCommand(args);

        using var timeoutCts = timeoutSeconds > 0 ? new CancellationTokenSource() : null;
        using var linkedCts = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        if (timeoutCts is not null)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }
        var effectiveCt = linkedCts?.Token ?? ct;

        var gateKey = $"{hostId}::{vmId}";
        var gate = AcquireGate(gateKey);
        var semaphoreHeld = false;
        try
        {
            await gate.Semaphore.WaitAsync(effectiveCt).ConfigureAwait(false);
            semaphoreHeld = true;
            var startedAt = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // SessionStore already maps connect failures to SshSessionOpenException; letting it
                // propagate here (via the outer catch's rewrap) preserves the typed inner so
                // ErrorMapper classifies it as SESSION_FAILED (LGS-SSH-D5).
                var client = await _sessionStore
                    .GetOrCreateAsync(hostId, vmId, username, password, effectiveCt)
                    .ConfigureAwait(false);

                if (transferIntent is not null)
                {
                    return await ExecuteTransferIntentAsync(
                        client, transferIntent.Value, args, vmId, password, effectiveCt).ConfigureAwait(false);
                }

                var sshResult = await client.ExecuteAsync(remoteCommand, effectiveCt).ConfigureAwait(false);
                startedAt.Stop();

                // A non-zero exit status is NOT a session failure — surface it as a normal result
                // whose JSON envelope carries the non-zero exit (LGS-SSH-D5 / AC 2). Redact both
                // stdout and stderr defensively (password + SSH private-key/PEM material).
                var redactedStdout = RedactAll(sshResult.Stdout ?? string.Empty, password);
                var redactedStderr = RedactAll(sshResult.Stderr ?? string.Empty, password);

                var envelope = JsonSerializer.Serialize(new
                {
                    Stdout = redactedStdout,
                    Stderr = redactedStderr,
                    ExitCode = sshResult.ExitStatus,
                    DurationMs = startedAt.ElapsedMilliseconds,
                });

                return new PowerShellHostResult(
                    Success: sshResult.ExitStatus == 0,
                    Output: new object?[] { envelope },
                    Stderr: redactedStderr,
                    ExitCode: sshResult.ExitStatus);
            }
            catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
            {
                // Per-invocation timeout: surface TimeoutException to match the PSDirect contract
                // (CommandExecutor maps it to COMMAND_TIMEOUT). Carries no credentials.
                throw new TimeoutException(
                    $"SSH command on guest '{vmId}' exceeded the {timeoutSeconds}s timeout.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SshSessionOpenException)
            {
                // Already typed + redacted by the store; rethrow so ErrorMapper's typed arm
                // classifies it as SESSION_FAILED without an extra wrap. ErrorMapper applies a
                // final defensive redaction pass on the message (LGS-SSH-D6).
                throw;
            }
            catch (Exception typed) when (
                typed is GuestTransferFailedException or GuestConnectionLostException
                    or GuestTransferAccessDeniedException or GuestTransferPathNotFoundException)
            {
                // These already carry the direction/VM/path context and are already redacted.
                // Wrapping them would replace that message with the inner text alone.
                // See myplans/remoting/linux-guest-support/linux-guest-file-transfer-spec.md — FR-ERR-5.
                throw;
            }
            catch (Exception ex)
            {
                // Mirror PowerShellDirectChannel's outer redaction guard: wrap with a fully
                // redacted top-level message (password + private-key/PEM) while preserving the
                // inner's concrete type for classification.
                throw new PowerShellDirectChannelException(
                    RedactAll(ex.Message ?? string.Empty, password),
                    ex);
            }
        }
        finally
        {
            if (semaphoreHeld)
            {
                gate.Semaphore.Release();
            }
            // Drop this call's reference. If eviction already removed the gate from the map and
            // this was the last live reference, the semaphore is disposed here (never while a
            // caller is waiting on it, since waiters hold their own reference).
            gate.ReleaseReference();
        }
    }

    /// <summary>
    /// Runs one translated transfer intent and returns its result as a BARE value in
    /// <see cref="PowerShellHostResult.Output"/>. The exec path's JSON envelope MUST NOT be used
    /// here: the transfer service scans Output for a plain numeric or string element, and an
    /// enveloped value makes it parse the envelope text instead.
    /// See /myplans/remoting/linux-guest-support/linux-guest-file-transfer-design.md — LGS-XFER-D2.
    /// </summary>
    private static async Task<PowerShellHostResult> ExecuteTransferIntentAsync(
        ISshExecClient client,
        TransferIntent intent,
        IDictionary<string, object?>? args,
        string vmId,
        string password,
        CancellationToken ct)
    {
        try
        {
            return await RunTransferIntentAsync(client, intent, args, vmId, password, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Direction, VM and failing path must travel with EVERY transfer failure, not only the
            // SFTP ones, or the caller has to re-run the operation to learn what failed.
            throw RewrapTransferFailure(
                failure,
                DirectionOf(intent),
                vmId,
                DescribeIntentPath(intent, args),
                password);
        }
    }

    /// <summary>Which way content moves for an intent, as the caller-facing message states it.</summary>
    private static string DirectionOf(TransferIntent intent) => intent switch
    {
        TransferIntent.ProbeGuestPath or TransferIntent.CompressGuestDirectory => "from",
        _ => "to",
    };

    /// <summary>Best available path for a failure message; the intent's own argument names differ.</summary>
    private static string DescribeIntentPath(TransferIntent intent, IDictionary<string, object?>? args)
    {
        foreach (var key in IntentPathArgumentOrder(intent))
        {
            if (args is not null && args.TryGetValue(key, out var value) &&
                value is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return "(unknown)";
    }

    private static string[] IntentPathArgumentOrder(TransferIntent intent) => intent switch
    {
        TransferIntent.ProbeGuestPath or TransferIntent.VerifyGuestFile => ["path"],
        TransferIntent.EnsureParentDirectory or TransferIntent.ExpandGuestArchive => ["dest", "zip"],
        TransferIntent.CompressGuestDirectory => ["src", "zip"],
        TransferIntent.BestEffortRemove => ["p"],
        _ => ["path", "dest", "src", "zip", "p"],
    };

    private static async Task<PowerShellHostResult> RunTransferIntentAsync(
        ISshExecClient client,
        TransferIntent intent,
        IDictionary<string, object?>? args,
        string vmId,
        string password,
        CancellationToken ct)
    {
        switch (intent)
        {
            case TransferIntent.ProbeGuestPath:
            {
                var path = TransferIntentTranslator.RequiredArg(args, "path");
                var quoted = TransferIntentTranslator.SingleQuote(path);
                // -L measures the target SFTP downloads; measuring the link causes a false mismatch.
                var probe = await RunGuestCommandAsync(
                    client, $"stat -L -c %F {quoted} && stat -L -c %s {quoted}", path, password, ct)
                    .ConfigureAwait(false);

                var lines = probe.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    throw new FileNotFoundException($"Path not found on guest: {path}", path);
                }

                // 'directory' is the exact %F token GNU stat emits for a directory; the service
                // only distinguishes container from file here.
                var isDirectory = TokenMatcher.ContainsToken(lines[0], "directory");
                object? bare = isDirectory
                    ? "dir"
                    : TransferIntentTranslator.ParseGuestSize(
                        lines.Length > 1 ? lines[1] : null, path, "a byte size");
                return BareResult(bare);
            }

            case TransferIntent.EnsureParentDirectory:
            {
                var destination = TransferIntentTranslator.RequiredArg(args, "dest");
                var parent = GuestParentDirectory(destination);
                if (string.IsNullOrEmpty(parent))
                {
                    return BareResult(null);
                }
                await RunGuestCommandAsync(
                    client, $"mkdir -p {TransferIntentTranslator.SingleQuote(parent)}",
                    parent, password, ct).ConfigureAwait(false);
                return BareResult(null);
            }

            case TransferIntent.VerifyGuestFile:
            {
                var path = TransferIntentTranslator.RequiredArg(args, "path");
                // -L verifies the target written through the link, avoiding a false size mismatch.
                var sizeText = await RunGuestCommandAsync(
                    client, $"stat -L -c %s {TransferIntentTranslator.SingleQuote(path)}",
                    path, password, ct).ConfigureAwait(false);
                var size = TransferIntentTranslator.ParseGuestSize(
                    sizeText, path, "the delivered file's size");
                return BareResult(size);
            }

            case TransferIntent.BuildGuestTempPath:
            {
                var name = TransferIntentTranslator.RequiredArg(args, "name");
                // TMPDIR honours a guest that redirects temp storage; /tmp is the POSIX default.
                var tempRoot = (await RunGuestCommandAsync(
                    client, "echo ${TMPDIR:-/tmp}", "TMPDIR", password, ct)
                    .ConfigureAwait(false)).Trim();
                if (string.IsNullOrEmpty(tempRoot)) tempRoot = "/tmp";
                return BareResult($"{tempRoot.TrimEnd('/')}/{name}");
            }

            case TransferIntent.CompressGuestDirectory:
            {
                var source = TransferIntentTranslator.RequiredArg(args, "src");
                var archivePath = TransferIntentTranslator.RequiredArg(args, "zip");
                var archiveSize = await ArchiveGuestDirectoryAsync(
                    client, source, archivePath, password, ct).ConfigureAwait(false);
                return BareResult(archiveSize);
            }

            case TransferIntent.ExpandGuestArchive:
            {
                var archivePath = TransferIntentTranslator.RequiredArg(args, "zip");
                var destination = TransferIntentTranslator.RequiredArg(args, "dest");
                var extractedBytes = await ExpandGuestArchiveAsync(
                    client, archivePath, destination, password, ct).ConfigureAwait(false);
                return BareResult(extractedBytes);
            }

            case TransferIntent.BestEffortRemove:
            {
                var path = TransferIntentTranslator.RequiredArg(args, "p");
                // Best-effort by contract: a removal failure must not fail the caller's transfer.
                try
                {
                    await client.ExecuteAsync(
                        $"rm -rf -- {TransferIntentTranslator.SingleQuote(path)}", ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Intentionally swallowed — see above.
                }
                return BareResult(true);
            }

            default:
                throw new GuestTransferFailedException(
                    $"Unhandled file-transfer intent for guest '{vmId}'.");
        }
    }

    /// <summary>Wraps a bare value in the result shape the transfer service parses.</summary>
    private static PowerShellHostResult BareResult(object? value)
        => new(
            Success: true,
            Output: value is null ? Array.Empty<object?>() : new[] { value },
            Stderr: string.Empty,
            ExitCode: 0);

    /// <summary>
    /// Runs a guest command for a transfer intent and returns stdout, converting a non-zero exit
    /// into the exception type that yields the right error code. Classification uses
    /// <see cref="TokenMatcher"/> rather than raw substring search (issue #289 class).
    /// </summary>
    private static async Task<string> RunGuestCommandAsync(
        ISshExecClient client, string command, string guestPath, string password, CancellationToken ct)
    {
        var result = await client.ExecuteAsync(command, ct).ConfigureAwait(false);
        if (result.ExitStatus == 0)
        {
            return result.Stdout ?? string.Empty;
        }

        var diagnostic = RedactAll(
            $"{result.Stdout ?? string.Empty}\n{result.Stderr ?? string.Empty}", password);
        if (TokenMatcher.ContainsPhrase(diagnostic, "Permission denied"))
        {
            throw new UnauthorizedAccessException(
                $"The guest denied access to '{guestPath}'.");
        }
        if (TokenMatcher.ContainsPhrase(diagnostic, "No such file or directory"))
        {
            throw new FileNotFoundException($"Path not found on guest: {guestPath}", guestPath);
        }
        throw new GuestTransferFailedException(
            $"Guest command for path '{guestPath}' exited {result.ExitStatus}: {diagnostic.Trim()}");
    }

    /// <summary>
    /// Builds the ZIP the transfer service expects from a guest directory. The archive is composed
    /// on the host and written back to the guest temp path, because stock Ubuntu Server ships
    /// neither <c>zip</c> nor <c>unzip</c> — a live spike confirmed this — while the service's host
    /// side requires a real ZIP. Only <c>find</c> is needed on the guest.
    /// See /myplans/remoting/linux-guest-support/linux-guest-file-transfer-design.md — LGS-XFER-D2.
    /// </summary>
    private static async Task<long> ArchiveGuestDirectoryAsync(
        ISshExecClient client, string guestDirectory, string guestArchivePath,
        string password, CancellationToken ct)
    {
        var listing = await ListGuestRelativeFilesAsync(
            client, guestDirectory, password, ct).ConfigureAwait(false);

        var stagingDirectory = Directory.CreateTempSubdirectory("hvmcp_xfer_").FullName;
        var stagingArchive = Path.Combine(
            Path.GetTempPath(), $"hvmcp_stage_{Guid.NewGuid():N}.zip");
        try
        {
            // The staging tree is Windows, which is case-insensitive, treats '\' as a separator and
            // normalizes away trailing dots and spaces, while the guest does none of that: 'A' and
            // 'a' (or 'x' and 'x.') would land on ONE staged entry and the archive would be
            // reported as a complete copy while missing one. EVERY listed entry is registered —
            // directories as well as files, and both in the same registry — because a directory
            // silently absorbed by another, or by a file of the aliased name, loses content just as
            // a collapsed file pair does, and directories were previously created with no check.
            var stagedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void RegisterStagedPath(string localPath, string relativePath)
            {
                if (stagedPaths.TryGetValue(localPath, out var alreadyStaged))
                {
                    throw new GuestTransferFailedException(
                        $"Transfer from guest failed for path '{guestDirectory}': guest entries " +
                        $"'{alreadyStaged}' and '{relativePath}' are distinct on the guest but " +
                        "collide as one entry on the host, so the copy would silently lose one.");
                }
                stagedPaths[localPath] = relativePath;
            }

            // An empty guest directory has no file to carry it, so stage it explicitly or the
            // archive omits it and the copy silently loses structure.
            foreach (var relativeDirectory in listing.Directories)
            {
                var localDirectory = ResolveContainedStagingPath(stagingDirectory, relativeDirectory);
                RegisterStagedPath(localDirectory, relativeDirectory);
                Directory.CreateDirectory(localDirectory);
            }

            long expectedBytes = 0;
            foreach (var relativePath in listing.Files)
            {
                var localPath = ResolveContainedStagingPath(stagingDirectory, relativePath);
                RegisterStagedPath(localPath, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                var guestFile = $"{guestDirectory.TrimEnd('/')}/{relativePath}";
                await client.DownloadAsync(guestFile, localPath, ct).ConfigureAwait(false);

                var guestSize = TransferIntentTranslator.ParseGuestSize(
                    await RunGuestCommandAsync(
                        client, $"stat -c %s {TransferIntentTranslator.SingleQuote(guestFile)}",
                        guestFile, password, ct).ConfigureAwait(false),
                    guestFile, "the source file's size");
                var stagedSize = new FileInfo(localPath).Length;
                if (stagedSize != guestSize)
                {
                    throw new GuestTransferFailedException(
                        $"reading '{guestFile}': the guest reported {guestSize} bytes but " +
                        $"{stagedSize} bytes were received.");
                }
                expectedBytes += stagedSize;
            }

            // The staged tree is what gets archived, so a byte that went missing between the
            // per-file check and here (a concurrent staging write, a collision the comparer did not
            // model) must fail rather than ship a short archive as complete.
            long stagedTotal = 0;
            foreach (var stagedFile in Directory.EnumerateFiles(
                         stagingDirectory, "*", SearchOption.AllDirectories))
            {
                stagedTotal += new FileInfo(stagedFile).Length;
            }
            if (stagedTotal != expectedBytes)
            {
                throw new GuestTransferFailedException(
                    $"Transfer from guest failed for path '{guestDirectory}': {expectedBytes} bytes " +
                    $"were read from the guest but the staged copy holds {stagedTotal} bytes.");
            }

            ZipFile.CreateFromDirectory(stagingDirectory, stagingArchive);
            await client.UploadAsync(stagingArchive, guestArchivePath, ct).ConfigureAwait(false);

            // The service reads this value back as the archive size, so it must be what the GUEST
            // now holds — the host staging length would report success for a short upload.
            var deliveredArchiveSize = TransferIntentTranslator.ParseGuestSize(
                await RunGuestCommandAsync(
                    client, $"stat -c %s {TransferIntentTranslator.SingleQuote(guestArchivePath)}",
                    guestArchivePath, password, ct).ConfigureAwait(false),
                guestArchivePath, "the staged archive's size");
            var stagingArchiveSize = new FileInfo(stagingArchive).Length;
            if (deliveredArchiveSize != stagingArchiveSize)
            {
                throw new GuestTransferFailedException(
                    $"staging '{guestArchivePath}': {stagingArchiveSize} bytes were sent but the " +
                    $"guest holds {deliveredArchiveSize} bytes.");
            }
            return deliveredArchiveSize;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            TryDeleteFile(stagingArchive);
        }
    }

    /// <summary>
    /// Relative paths of every file under a guest directory.
    /// </summary>
    /// <remarks>
    /// The listing is NUL-delimited and base64-wrapped rather than newline-delimited because a
    /// POSIX filename may itself contain a newline, which a line-based listing silently splits
    /// into two nonexistent paths.
    /// </remarks>
    private static async Task<GuestDirectoryListing> ListGuestRelativeFilesAsync(
        ISshExecClient client, string guestDirectory, string password, CancellationToken ct)
    {
        var quotedDirectory = TransferIntentTranslator.SingleQuote(guestDirectory);
        // 'set -o pipefail' is required, not stylistic: a pipeline reports only its LAST command's
        // status, so an unreadable subtree made find fail while base64 exited 0 and the truncated
        // listing was archived and reported as a successful directory transfer.
        // Directories are listed alongside files ('-type d') so an empty one still reaches the
        // destination; a directory-only copy would otherwise silently drop it.
        // See myplans/remoting/linux-guest-support/linux-guest-file-transfer-design.md.
        var encoded = await RunGuestCommandAsync(
            client,
            // -H avoids an empty archive for a symlinked root without following nested links.
            "set -o pipefail; find -H " + quotedDirectory +
            " -mindepth 1 \\( -type f -printf 'f/%P\\0' -o -type d -printf 'd/%P\\0' \\) | base64 -w 0",
            guestDirectory, password, ct).ConfigureAwait(false);

        var trimmed = encoded.Trim();
        if (trimmed.Length == 0)
        {
            return new GuestDirectoryListing(Array.Empty<string>(), Array.Empty<string>());
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(trimmed);
        }
        catch (FormatException ex)
        {
            throw new GuestTransferFailedException(
                $"listing '{guestDirectory}': the directory listing could not be decoded.", ex);
        }

        var files = new List<string>();
        var directories = new List<string>();
        foreach (var entry in Encoding.UTF8.GetString(decoded)
                     .Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.StartsWith("f/", StringComparison.Ordinal)) files.Add(entry[2..]);
            else if (entry.StartsWith("d/", StringComparison.Ordinal)) directories.Add(entry[2..]);
        }
        return new GuestDirectoryListing(files, directories);
    }

    /// <summary>Files and directories under a guest directory, kept apart so empty ones survive.</summary>
    private readonly record struct GuestDirectoryListing(
        IReadOnlyList<string> Files, IReadOnlyList<string> Directories);

    /// <summary>
    /// Maps a guest-supplied relative path beneath the host staging directory, refusing any that
    /// would land outside it.
    /// </summary>
    /// <remarks>
    /// The guest chooses these strings, and this process is privileged: without the containment
    /// check a traversal component in a filename would write anywhere on the host.
    /// </remarks>
    private static string ResolveContainedStagingPath(string stagingDirectory, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(
            stagingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.Ordinal))
        {
            throw new GuestTransferFailedException(
                $"Transfer from guest refused: the guest listed an entry ('{relativePath}') that " +
                "resolves outside the host staging directory.");
        }
        return candidate;
    }

    /// <summary>
    /// Expands a guest-side ZIP into a guest destination and removes the archive. Extraction runs
    /// on the host for the same reason archiving does (no <c>unzip</c> on the guest); the delivered
    /// files are written back over SFTP and the returned total is the sum of the sizes the GUEST
    /// reports for them, so a short write cannot be counted as delivered.
    /// </summary>
    private static async Task<long> ExpandGuestArchiveAsync(
        ISshExecClient client, string guestArchivePath, string guestDestination,
        string password, CancellationToken ct)
    {
        var stagingArchive = Path.Combine(Path.GetTempPath(), $"hvmcp_expand_{Guid.NewGuid():N}.zip");
        var stagingDirectory = Directory.CreateTempSubdirectory("hvmcp_expand_").FullName;
        try
        {
            await client.DownloadAsync(guestArchivePath, stagingArchive, ct).ConfigureAwait(false);
            try
            {
                ZipFile.ExtractToDirectory(stagingArchive, stagingDirectory, overwriteFiles: true);
            }
            catch (InvalidDataException ex)
            {
                // The staged archive did not arrive intact. Surfacing the raw zip error would name
                // a corrupt file and hide that the TRANSFER was short. See
                // myplans/remoting/linux-guest-support/linux-guest-file-transfer-spec.md — FR-VER-5.
                throw new GuestTransferFailedException(
                    $"Transfer to guest failed for path '{guestArchivePath}': the guest holds " +
                    $"{new FileInfo(stagingArchive).Length} bytes, which is not a readable archive.",
                    ex);
            }

            await RunGuestCommandAsync(
                client, $"mkdir -p {TransferIntentTranslator.SingleQuote(guestDestination)}",
                guestDestination, password, ct).ConfigureAwait(false);

            // Empty directories carry no file, so create them explicitly or a copy that is only
            // structure arrives as nothing. Mirrors the archive side's directory staging.
            foreach (var localDirectory in Directory.EnumerateDirectories(
                         stagingDirectory, "*", SearchOption.AllDirectories))
            {
                var relativeDirectory = Path.GetRelativePath(stagingDirectory, localDirectory)
                    .Replace('\\', '/');
                await RunGuestCommandAsync(
                    client,
                    "mkdir -p " + TransferIntentTranslator.SingleQuote(
                        $"{guestDestination.TrimEnd('/')}/{relativeDirectory}"),
                    relativeDirectory, password, ct).ConfigureAwait(false);
            }

            long totalBytes = 0;
            foreach (var localFile in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagingDirectory, localFile).Replace('\\', '/');
                var guestPath = $"{guestDestination.TrimEnd('/')}/{relativePath}";
                var guestParent = GuestParentDirectory(guestPath);
                if (!string.IsNullOrEmpty(guestParent))
                {
                    await RunGuestCommandAsync(
                        client, $"mkdir -p {TransferIntentTranslator.SingleQuote(guestParent)}",
                        guestParent, password, ct).ConfigureAwait(false);
                }
                await client.UploadAsync(localFile, guestPath, ct).ConfigureAwait(false);

                var sentSize = new FileInfo(localFile).Length;
                var deliveredSize = TransferIntentTranslator.ParseGuestSize(
                    await RunGuestCommandAsync(
                        client, $"stat -c %s {TransferIntentTranslator.SingleQuote(guestPath)}",
                        guestPath, password, ct).ConfigureAwait(false),
                    guestPath, "the delivered file's size");
                if (deliveredSize != sentSize)
                {
                    throw new GuestTransferFailedException(
                        $"writing '{guestPath}': {sentSize} bytes were sent but the guest holds " +
                        $"{deliveredSize} bytes.");
                }
                totalBytes += deliveredSize;
            }

            try
            {
                await client.ExecuteAsync(
                    $"rm -f -- {TransferIntentTranslator.SingleQuote(guestArchivePath)}", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The staged archive is disposable; failing to remove it must not fail the copy.
            }

            return totalBytes;
        }
        finally
        {
            TryDeleteFile(stagingArchive);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    /// <summary>POSIX parent directory of a guest path ('/' separated, unlike the host).</summary>
    private static string GuestParentDirectory(string guestPath)
    {
        var normalized = guestPath.Replace('\\', '/').TrimEnd('/');
        var lastSeparator = normalized.LastIndexOf('/');
        return lastSeparator switch
        {
            < 0 => string.Empty,
            0 => "/",
            _ => normalized[..lastSeparator],
        };
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* staging cleanup is best-effort */ }
        catch (UnauthorizedAccessException) { /* staging cleanup is best-effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { /* staging cleanup is best-effort */ }
        catch (UnauthorizedAccessException) { /* staging cleanup is best-effort */ }
    }

    /// <summary>True when the executor supplied a real guest command to run.</summary>
    private static bool HasExplicitCommand(IDictionary<string, object?>? args)
    {
        if (args is null) return false;
        if (args.TryGetValue("base64Script", out var encoded) &&
            encoded is string encodedText && !string.IsNullOrEmpty(encodedText))
        {
            return true;
        }
        return args.TryGetValue("cmd", out var command) &&
               command is string commandText && !string.IsNullOrEmpty(commandText);
    }

    /// <summary>
    /// True when the arguments carry a parameter name that only <see cref="FileTransferService"/>
    /// binds, which marks the call as a transfer intent rather than a guest command.
    /// </summary>
    private static bool HasTransferShapedArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return false;
        foreach (var parameterName in TransferParameterNames)
        {
            if (args.ContainsKey(parameterName)) return true;
        }
        return false;
    }

    private static readonly string[] TransferParameterNames =
        ["path", "dest", "name", "src", "zip", "p"];

    /// <summary>
    /// Loads (or creates) the reference-counted gate for <paramref name="gateKey"/> and adds a
    /// live reference to it. Retries if the gate is concurrently evicted between lookup and the
    /// reference acquisition, so a call arriving during eviction gets a fresh gate rather than a
    /// disposed one — no deadlock, no use-after-dispose.
    /// </summary>
    private static OperationGate AcquireGate(string gateKey)
    {
        while (true)
        {
            var gate = OperationGates.GetOrAdd(gateKey, _ => new OperationGate());
            if (gate.TryAcquireReference())
            {
                return gate;
            }
            // Gate is being torn down; drop the stale mapping (only if it is still this
            // instance) and retry so GetOrAdd installs a fresh one.
            OperationGates.TryRemove(new KeyValuePair<string, OperationGate>(gateKey, gate));
        }
    }

    /// <summary>
    /// Builds the concrete shell command to run on the Ubuntu guest from the executor's
    /// <c>args</c> contract. <c>cmd</c> (single command) runs as <c>&lt;shell&gt; -c '&lt;command&gt;'</c>
    /// with the command single-quoted (each embedded <c>'</c> becomes <c>'\''</c>) so no
    /// metacharacter is interpreted by the outer shell — no shell injection. <c>base64Script</c>
    /// is decoded on the guest (<c>base64 -d</c>) and piped into the shell, so the script body
    /// never touches the outer command line and cannot break quoting.
    /// </summary>
    private static string BuildRemoteCommand(IDictionary<string, object?>? args)
    {
        var shell = ResolveShellPath(args is not null && args.TryGetValue("sh", out var shellValue)
            ? shellValue as string
            : null);

        if (args is not null && args.TryGetValue("base64Script", out var b64Value) &&
            b64Value is string base64Script && !string.IsNullOrEmpty(base64Script))
        {
            // The base64 token is produced by Convert.ToBase64String (A–Z a–z 0–9 + / =), so it is
            // shell-safe verbatim. Decode on the guest and pipe into the shell — the script body
            // is never on the outer command line.
            return $"echo {base64Script} | base64 -d | {shell}";
        }

        if (args is not null && args.TryGetValue("cmd", out var cmdValue) &&
            cmdValue is string command && !string.IsNullOrEmpty(command))
        {
            return $"{shell} -c {SingleQuote(command)}";
        }

        // Neither arg present: run an empty (no-op) command that exits 0 under the shell so the
        // envelope shape is preserved. Should not happen in practice (executor always sets one).
        return $"{shell} -c ''";
    }

    /// <summary>Maps the validated Linux shell token (bash/sh/default) to an absolute guest path.</summary>
    private static string ResolveShellPath(string? shell) => shell switch
    {
        "sh" => "/bin/sh",
        // "bash" and "default" (the guest login shell) both resolve to bash on Ubuntu 24.04.
        _ => "/bin/bash",
    };

    /// <summary>
    /// POSIX single-quote wrapping: wrap in single quotes and replace each embedded single
    /// quote with the <c>'\''</c> sequence. The result is a single, injection-safe shell word.
    /// </summary>
    private static string SingleQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (var character in value)
        {
            if (character == '\'')
            {
                builder.Append("'\\''");
            }
            else
            {
                builder.Append(character);
            }
        }
        builder.Append('\'');
        return builder.ToString();
    }

    /// <summary>
    /// LGS-SSH-D6 defensive redaction: strip the literal password AND route through
    /// <see cref="StderrSpillHelper.RedactDefensively"/> so any SSH private-key / PEM material,
    /// secure-string, or credential parameter is removed before the text becomes command data,
    /// an error string, or a log entry.
    /// </summary>
    private static string RedactAll(string text, string password)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var passwordStripped = CredentialResolver.RedactPassword(text, password);
        return StderrSpillHelper.RedactDefensively(passwordStripped);
    }

    /// <inheritdoc />
    public Task<PowerShellHostResult> CopyToSessionAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string localSourcePath,
        string guestDestinationPath,
        CancellationToken ct = default)
        => TransferAsync(
            hostId, vmId, username, password, ct,
            (client, token) => client.UploadAsync(localSourcePath, guestDestinationPath, token),
            direction: "to",
            failingPath: guestDestinationPath);

    /// <inheritdoc />
    public Task<PowerShellHostResult> CopyFromSessionAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string guestSourcePath,
        string localDestinationPath,
        CancellationToken ct = default)
        => TransferAsync(
            hostId, vmId, username, password, ct,
            (client, token) => client.DownloadAsync(guestSourcePath, localDestinationPath, token),
            direction: "from",
            failingPath: guestSourcePath);

    /// <summary>
    /// Runs one SFTP transfer under the same per-(hostId,vmId) gate that serializes command
    /// execution, so a transfer and an exec against one VM cannot interleave on a shared client.
    /// Applies no wall-clock deadline: transfer duration follows content size, and caller
    /// cancellation is the only bound.
    /// See /myplans/remoting/linux-guest-support/linux-guest-file-transfer-design.md — LGS-XFER-D7.
    /// </summary>
    private async Task<PowerShellHostResult> TransferAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        CancellationToken ct,
        Func<ISshExecClient, CancellationToken, Task> transfer,
        string direction,
        string failingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var gateKey = $"{hostId}::{vmId}";
        var gate = AcquireGate(gateKey);
        var semaphoreHeld = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            semaphoreHeld = true;

            try
            {
                // Acquisition is inside the transfer catch on purpose: a failure to open the
                // initial session is still a failure OF THIS TRANSFER, and outside it the caller
                // received a session error naming neither direction nor the path it was for.
                var client = await _sessionStore
                    .GetOrCreateAsync(hostId, vmId, username, password, ct)
                    .ConfigureAwait(false);

                await transfer(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Direction, VM, and path travel with every transfer failure so the caller need not
                // re-run the operation to learn what failed. The concrete inner type is preserved
                // because it is what selects the error code.
                throw RewrapTransferFailure(ex, direction, vmId, failingPath, password);
            }

            return new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0);
        }
        finally
        {
            if (semaphoreHeld)
            {
                gate.Semaphore.Release();
            }
            gate.ReleaseReference();
        }
    }

    /// <summary>
    /// Re-wraps a transfer failure with a diagnostic message while preserving the exception type
    /// the error mapper classifies: denial stays AUTH_FAILED, an absent path stays FILE_NOT_FOUND,
    /// a lost or unopenable session stays SESSION_FAILED, and anything else reaches
    /// TRANSFER_FAILED. A bare <see cref="InvalidOperationException"/> would map to COMMAND_FAILED
    /// instead, which is why it is never used here.
    /// See /myplans/remoting/linux-guest-support/linux-guest-file-transfer-design.md — LGS-XFER-D4.
    /// </summary>
    private static Exception RewrapTransferFailure(
        Exception failure, string direction, string vmId, string failingPath, string password)
    {
        var context = $"Transfer {direction} guest '{vmId}' failed for path '{failingPath}'";
        var detail = RedactAll(failure.Message ?? string.Empty, password);

        // Only a failure that DECLARES it already names direction, VM and path is forwarded intact.
        // Matching on the message prefix instead let inner failures that omit the VM suppress the
        // wrap that would have supplied it.
        if (failure is GuestTransferFailedException { CarriesCallerContext: true } or
                       GuestConnectionLostException { CarriesCallerContext: true })
        {
            return failure;
        }

        return failure switch
        {
            // Re-thrown rather than forwarded: the store composes this before a transfer exists,
            // so its message names neither direction nor path. The typed facets are carried over
            // because the mapper's SESSION_FAILED arm composes the envelope from them.
            SshSessionOpenException sessionOpenEx => new SshSessionOpenException(
                sessionOpenEx.VmId,
                $"{context}: {detail}",
                sessionOpenEx,
                sessionOpenEx.SpillSummary,
                sessionOpenEx.RedactedCauseChain,
                sessionOpenEx.DecisiveCause),
            // A connection lost mid-transfer stays a session fault so it reaches SESSION_FAILED,
            // telling the caller to retry rather than to inspect the file.
            GuestConnectionLostException => new GuestConnectionLostException(
                $"{context}: {detail}", failure) { CarriesCallerContext = true },
            // The transfer-specific denial and missing-path types exist so the mapper forwards
            // this composed message; the plain framework types are replaced by fixed text there.
            UnauthorizedAccessException => new GuestTransferAccessDeniedException(
                $"{context}: access was denied by the guest.", failure),
            // DirectoryNotFoundException extends IOException but classifies as FILE_NOT_FOUND, so
            // it must be matched above the IOException arm.
            DirectoryNotFoundException => new GuestTransferPathNotFoundException(
                $"{context}: the path was not found.", failingPath, failure),
            FileNotFoundException => new GuestTransferPathNotFoundException(
                $"{context}: the path was not found.", failingPath, failure),
            _ => new GuestTransferFailedException($"{context}: {detail}", failure)
            {
                CarriesCallerContext = true,
            },
        };
    }

    /// <inheritdoc />
    public async Task EvictSessionAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        _logger.LogDebug("SshGuestChannel: evicting SSH session for {HostId}/{VmId}", hostId, vmId);

        // Eviction runs under the SAME per-key operation gate that guards command execution, so
        // for a given (hostId,vmId) "run a command" and "dispose the session" are mutually
        // exclusive. Ordering is critical: dispose the cached client (EvictAsync removes it from
        // the store's client map and disposes it) FIRST while holding the semaphore, and only
        // AFTER disposal completes remove the gate from the OperationGates map. Because the client
        // is torn down before the gate is removable, no fresh gate can form a second concurrent
        // critical section over the same still-cached client, so no invocation can execute against
        // a client that is being (or about to be) disposed. A waiter already queued on this gate,
        // and any invocation arriving after removal, will find the client gone from the store and
        // open a brand-new one via GetOrCreateAsync — never the disposed one.
        var gateKey = $"{hostId}::{vmId}";
        var gate = AcquireGate(gateKey);
        var semaphoreHeld = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            semaphoreHeld = true;

            await _sessionStore.EvictAsync(hostId, vmId, ct).ConfigureAwait(false);

            // Client is fully disposed; now drop the map's implicit reference so the semaphore is
            // disposed once no operation is using it. Removing only after disposal guarantees no
            // fresh gate could have bound to the just-disposed client.
            if (OperationGates.TryRemove(new KeyValuePair<string, OperationGate>(gateKey, gate)))
            {
                gate.ReleaseMapReference();
            }
        }
        finally
        {
            if (semaphoreHeld)
            {
                gate.Semaphore.Release();
            }
            // Release exactly once on every path (including the WaitAsync-cancelled path where the
            // gate was never removed). If we were the last reference after removal, the semaphore
            // is disposed here — never while another caller waits on it, since waiters hold their
            // own reference.
            gate.ReleaseReference();
        }
    }
}
