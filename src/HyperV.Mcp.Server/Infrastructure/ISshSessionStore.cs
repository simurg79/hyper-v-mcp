namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Owns the lifecycle of persistent SSH connections targeting Linux guest VMs.
/// Mirrors <see cref="ISessionStore"/> (per-(hostId,vmId) serialized get-or-create)
/// so the SSH guest channel matches the PSDirect seam shape (LGS-SSH-D2).
/// See /myplans/remoting/linux-guest-support/linux-ssh-exec-first-slice-design.md.
/// </summary>
public interface ISshSessionStore
{
    /// <summary>
    /// Returns a connected <see cref="ISshExecClient"/> for (hostId, vmId), opening a new
    /// SSH connection if none is cached or the cached one is no longer connected.
    /// Serialized per-(hostId,vmId). A connect/open failure surfaces as
    /// <see cref="SshSessionOpenException"/> (→ SESSION_FAILED, LGS-SSH-D5).
    /// </summary>
    Task<ISshExecClient> GetOrCreateAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        CancellationToken ct = default);

    /// <summary>
    /// Disconnects and disposes any cached SSH connection for (hostId, vmId). Idempotent.
    /// </summary>
    Task EvictAsync(string hostId, string vmId, CancellationToken ct = default);
}

/// <summary>
/// Minimal abstraction over a connected SSH client used by the SSH guest channel.
/// Wraps SSH.NET's <c>SshClient</c> so the exec path is unit-testable without a live guest
/// and so no private-key/credential material is ever exposed by the surface (LGS-SSH-D6).
/// </summary>
public interface ISshExecClient : IDisposable
{
    /// <summary>True while the underlying transport is connected.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Runs <paramref name="commandText"/> to completion over an SSH exec channel and returns
    /// its stdout, stderr, and exit status. A non-zero exit status is NOT a failure of this
    /// call — it is returned in the result (LGS-SSH-D5 / acceptance criterion 2).
    /// </summary>
    Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="localSourcePath"/> to <paramref name="guestDestinationPath"/> over
    /// SFTP, closing the remote handle before returning so a size read-back taken immediately
    /// afterwards observes the complete file.
    /// </summary>
    /// <remarks>
    /// A denial observed after the connection is up surfaces as
    /// <see cref="UnauthorizedAccessException"/> and an absent path as
    /// <see cref="FileNotFoundException"/>, because those are the types the error mapper
    /// classifies as AUTH_FAILED and FILE_NOT_FOUND respectively.
    /// See /myplans/remoting/linux-guest-support/linux-guest-file-transfer-design.md — LGS-XFER-D4.
    /// </remarks>
    Task UploadAsync(string localSourcePath, string guestDestinationPath, CancellationToken ct = default);

    /// <summary>
    /// Reads <paramref name="guestSourcePath"/> into <paramref name="localDestinationPath"/> over
    /// SFTP. Failure typing matches <see cref="UploadAsync"/>.
    /// </summary>
    Task DownloadAsync(string guestSourcePath, string localDestinationPath, CancellationToken ct = default);
}

/// <summary>
/// Captured outcome of a single SSH exec invocation. Shapes into the standard
/// <see cref="Models.CommandResult"/> envelope by the channel/executor (LGS-SSH-D1).
/// </summary>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
/// <param name="ExitStatus">Remote process exit status (0 == success).</param>
public sealed record SshCommandResult(string Stdout, string Stderr, int ExitStatus);
