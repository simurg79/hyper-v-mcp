using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Creates connected <see cref="ISshExecClient"/> instances. A seam so the SSH session
/// store is unit-testable without a live guest, and so all SSH.NET usage stays confined
/// to a single file. See internal documentation
/// </summary>
public interface ISshExecClientFactory
{
    /// <summary>
    /// Opens and connects an SSH session to <paramref name="host"/>:<paramref name="port"/>
    /// using password auth. Throws on connect/authentication failure — the store translates
    /// that into <see cref="SshSessionOpenException"/> (→ SESSION_FAILED, LGS-SSH-D5).
    /// </summary>
    Task<ISshExecClient> ConnectAsync(
        string host,
        int port,
        string username,
        string password,
        CancellationToken ct = default);
}

/// <summary>
/// SSH.NET-backed <see cref="ISshExecClientFactory"/>. Password auth only for this slice;
/// key-based auth and host-key pinning are deferred (OQ-LGS-2).
/// </summary>
public sealed class SshExecClientFactory : ISshExecClientFactory
{
    public async Task<ISshExecClient> ConnectAsync(
        string host,
        int port,
        string username,
        string password,
        CancellationToken ct = default)
    {
        // ConnectionInfo owns the credential; SSH.NET holds it internally. We never surface
        // the PasswordAuthenticationMethod or ConnectionInfo, so the password/key material
        // cannot escape via this object (LGS-SSH-D6).
        var passwordAuth = new PasswordAuthenticationMethod(username, password);
        var connectionInfo = new ConnectionInfo(host, port, username, passwordAuth);
        var client = new SshClient(connectionInfo);
        // The SFTP client is built from this same ConnectionInfo so transfer reuses one credential
        // surface and one lifetime. A live spike against Ubuntu 24.04 established that SSH.NET's
        // SftpClient does NOT ride the SshClient's session and performs its own authentication
        // handshake, so "one connection" is not achievable with this library — but no additional
        // credential is prompted for or stored.
        // See internal documentation — LGS-XFER-D1.
        try
        {
            // LGS-SSH-D5: honor the token on connect. SSH.NET's Connect() is blocking with no
            // native cancellation, so run it on a worker and race it against the token; when the
            // token fires we abort by disposing the client (which interrupts the pending connect)
            // and surface OperationCanceledException so the timeout contract cannot hang.
            var connectTask = Task.Run(() => client.Connect(), CancellationToken.None);
            var completed = await Task.WhenAny(
                connectTask,
                Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (completed != connectTask)
            {
                // Token fired first: interrupt the blocked connect and propagate cancellation.
                try { client.Dispose(); } catch { /* best-effort abort */ }
                // Connect() may still unblock by throwing after Dispose(); observe that faulted
                // completion so it cannot resurface as an UnobservedTaskException.
                _ = connectTask.ContinueWith(
                    static faulted => _ = faulted.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
            }
            await connectTask.ConfigureAwait(false); // observe connect exceptions
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new SshNetExecClient(client, connectionInfo);
    }

    /// <summary>
    /// Builds the production SSH/SFTP adapter over an UNCONNECTED client, so a test can exercise
    /// its real connect-and-transfer path against an unreachable address.
    /// </summary>
    /// <remarks>
    /// Exists because the adapter is private and the SFTP connect is the very behaviour under
    /// test: routing that test through a stand-in client left it passing while the production
    /// connection handling was broken.
    /// See internal documentation
    /// </remarks>
    internal static ISshExecClient CreateClientForTesting(ConnectionInfo connectionInfo)
        => new SshNetExecClient(new SshClient(connectionInfo), connectionInfo);

    /// <summary>
    /// Adapts SSH.NET's <see cref="SshClient"/> to <see cref="ISshExecClient"/>. Runs each
    /// command via a fresh exec channel and captures stdout/stderr/exit status.
    /// </summary>
    private sealed class SshNetExecClient : ISshExecClient
    {
        private readonly SshClient _client;
        private readonly ConnectionInfo _connectionInfo;
        private readonly SemaphoreSlim _sftpGate = new(1, 1);
        private SftpClient? _sftpClient;

        public SshNetExecClient(SshClient client, ConnectionInfo connectionInfo)
        {
            _client = client;
            _connectionInfo = connectionInfo;
        }

        public bool IsConnected => _client.IsConnected;

        /// <summary>
        /// Returns the connected SFTP client, opening it on first use. Serialized because a
        /// transfer and an eviction can race for the same lazily-created client.
        /// </summary>
        /// <remarks>
        /// SFTP opens a SECOND connection (a live spike established SSH.NET's SftpClient does not
        /// ride the SshClient session), so this object owns a real connection whose lifetime must
        /// be closed on every exit. Cancellation therefore aborts the blocking Connect() by
        /// disposing the client and observes the pending work, mirroring the SSH connect path —
        /// releasing the gate while a connect continued on an object about to be disposed would
        /// leak that second connection.
        /// </remarks>
        private async Task<SftpClient> GetSftpAsync(CancellationToken ct)
        {
            await _sftpGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_sftpClient is { IsConnected: true })
                {
                    return _sftpClient;
                }

                if (_sftpClient is not null)
                {
                    try { _sftpClient.Dispose(); } catch { /* best-effort replace */ }
                    _sftpClient = null;
                }

                var created = new SftpClient(_connectionInfo);
                try
                {
                    var connectTask = Task.Run(() => created.Connect(), CancellationToken.None);
                    var completed = await Task.WhenAny(
                        connectTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                    if (completed != connectTask)
                    {
                        try { created.Dispose(); } catch { /* best-effort abort */ }
                        _ = connectTask.ContinueWith(
                            static faulted => _ = faulted.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        ct.ThrowIfCancellationRequested();
                    }
                    await connectTask.ConfigureAwait(false);
                }
                catch
                {
                    try { created.Dispose(); } catch { /* already aborted above */ }
                    throw;
                }
                _sftpClient = created;
                return created;
            }
            finally
            {
                _sftpGate.Release();
            }
        }

        public async Task UploadAsync(
            string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
        {
            var sftp = await GetSftpConnectionAsync(ct, guestDestinationPath).ConfigureAwait(false);
            try
            {
                using var source = File.OpenRead(localSourcePath);
                // Stream copy rather than UploadFile: the latter blocks with no cancellation, so a
                // caller that cancelled mid-transfer kept waiting until the network returned.
                // The stream is disposed before this method returns, which a live spike showed is
                // what makes an immediate guest-side size read observe the complete file — no retry
                // or settle delay is required.
                // See internal documentation — LGS-XFER-D3.
                await using var destination = await sftp
                    .OpenAsync(guestDestinationPath, FileMode.Create, FileAccess.Write, ct)
                    .ConfigureAwait(false);
                await source.CopyToAsync(destination, TransferBufferBytes, ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw TranslateSftpFailure(ex, guestDestinationPath);
            }
        }

        public async Task DownloadAsync(
            string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
        {
            var sftp = await GetSftpConnectionAsync(ct, guestSourcePath).ConfigureAwait(false);
            try
            {
                await using var source = await sftp
                    .OpenAsync(guestSourcePath, FileMode.Open, FileAccess.Read, ct)
                    .ConfigureAwait(false);
                using var destination = File.Create(localDestinationPath);
                await source.CopyToAsync(destination, TransferBufferBytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw TranslateSftpFailure(ex, guestSourcePath);
            }
        }

        /// <summary>32 KiB: comfortably above the SFTP packet payload, small enough to stay responsive.</summary>
        private const int TransferBufferBytes = 32 * 1024;

        /// <summary>
        /// Obtains the SFTP connection, translating a failure to OPEN it into a session fault.
        /// </summary>
        /// <remarks>
        /// Opening SFTP is a connect, not a transfer: reporting it as a transfer failure told the
        /// caller to inspect a file that was never reached instead of to reopen the session. The
        /// translation must happen here because the connect precedes the per-transfer guards.
        /// See internal documentation
        /// </remarks>
        private async Task<SftpClient> GetSftpConnectionAsync(CancellationToken ct, string guestPath)
        {
            try
            {
                return await GetSftpAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GuestConnectionLostException(
                    $"The transfer connection to the guest could not be opened for '{guestPath}'.", ex);
            }
        }

        /// <summary>
        /// Maps an SFTP failure onto the exception type the error mapper already classifies.
        /// A live spike confirmed SSH.NET raises distinct types for denial and absence, so the
        /// two are separable without inspecting message text (issue #289 class).
        /// See internal documentation — LGS-XFER-D4.
        /// </summary>
        private static Exception TranslateSftpFailure(Exception failure, string guestPath) => failure switch
        {
            OperationCanceledException => failure,
            // Already classified by the connect guard; re-wrapping would restate it as a transfer
            // fault and lose the "reopen the session" verdict.
            GuestConnectionLostException => failure,
            SftpPermissionDeniedException => new UnauthorizedAccessException(
                $"The guest denied access to '{guestPath}'.", failure),
            SftpPathNotFoundException => new FileNotFoundException(
                $"Path not found on guest: {guestPath}", guestPath, failure),
            // A connection that drops mid-transfer is a session fault: reporting it as an I/O
            // failure would tell the caller to inspect the file rather than reopen the session,
            // and would leave the dead client cached. SshConnectionException covers the transport
            // drop; ObjectDisposedException is how SSH.NET surfaces use of a torn-down channel.
            SshConnectionException or ObjectDisposedException => new GuestConnectionLostException(
                $"The connection to the guest was lost during the transfer of '{guestPath}'.", failure),
            _ => new GuestTransferFailedException(
                $"SFTP transfer failed for guest path '{guestPath}'.", failure),
        };

        public async Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default)
        {
            try
            {
                return await ExecuteCoreAsync(commandText, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SshConnectionException or ObjectDisposedException)
            {
                // A drop while running a transfer's verification command is a session fault, the
                // same as a drop during the bytes. Untranslated it reached the generic wrap and was
                // reported as a command failure, telling the caller to inspect output that never
                // arrived rather than to reopen the session.
                // See internal documentation
                throw new GuestConnectionLostException(
                    "The connection to the guest was lost while running a command.", ex);
            }
        }

        private async Task<SshCommandResult> ExecuteCoreAsync(string commandText, CancellationToken ct)
        {
            using var command = _client.CreateCommand(commandText, Encoding.UTF8);

            // LGS-SSH-D5: use the async BeginExecute/EndExecute pair so a timeout/cancellation
            // actually aborts the in-flight remote command. command.CancelAsync() interrupts the
            // running channel; without it a blocking Execute() would hang until the remote exits,
            // defeating the command-timeout contract (TimeoutBehaviorTests / Issue170).
            var asyncResult = command.BeginExecute();
            using (ct.Register(() =>
            {
                try { command.CancelAsync(); }
                catch { /* best-effort abort — channel may already be closing */ }
            }))
            {
                // Wait on both the exec handle and the token's wait handle so cancellation always
                // breaks the wait even if command.CancelAsync() does not promptly signal the exec
                // handle; otherwise the await could hang past the timeout/cancellation contract.
                await Task.Run(
                    () => WaitHandle.WaitAny(new[] { asyncResult.AsyncWaitHandle, ct.WaitHandle }),
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                // Ensure EndExecute is observed so no channel resource leaks, then propagate.
                try { command.EndExecute(asyncResult); } catch { /* aborted */ }
                ct.ThrowIfCancellationRequested();
            }

            command.EndExecute(asyncResult);
            var stderr = command.Error ?? string.Empty;
            var stdout = command.Result ?? string.Empty;
            // SSH.NET exposes ExitStatus as int? (null when the remote sent no exit-status
            // message). Treat a missing status as success (0) — stdout/stderr still flow.
            return new SshCommandResult(stdout, stderr, command.ExitStatus ?? 0);
        }

        public void Dispose()
        {
            var sftp = _sftpClient;
            if (sftp is not null)
            {
                try { if (sftp.IsConnected) sftp.Disconnect(); }
                catch (SshException) { /* best-effort teardown */ }
                try { sftp.Dispose(); } catch (SshException) { /* best-effort teardown */ }
                _sftpClient = null;
            }
            _sftpGate.Dispose();

            try { if (_client.IsConnected) _client.Disconnect(); }
            catch (SshException) { /* best-effort teardown */ }
            _client.Dispose();
        }
    }
}
