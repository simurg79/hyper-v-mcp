namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Issue #73 — seam over <see cref="System.IO.Directory"/> enumeration used by
/// <see cref="HyperVManager.ListImagesAsync"/>'s pre-execution access probe.
///
/// The seam exists solely to enable deterministic, cross-platform test coverage
/// of the probe-time exception classification contract (ST-D7 / PR #67 review):
/// <list type="bullet">
///   <item><see cref="System.IO.DirectoryNotFoundException"/> ⇒ INVALID_PARAMETER</item>
///   <item><see cref="System.UnauthorizedAccessException"/> ⇒ IO_ERROR</item>
///   <item><see cref="System.IO.IOException"/> ⇒ IO_ERROR</item>
/// </list>
///
/// Implementations MUST surface these native exceptions unchanged — do NOT
/// catch, wrap, or translate. <c>ListImagesAsync</c>'s existing try/catch and
/// <c>ErrorMapper</c> remain the sole classifier so the envelope shape is
/// unchanged for production callers.
/// </summary>
public interface IFileSystemProbe
{
    /// <summary>
    /// Performs the cursor-advance equivalent of
    /// <c>Directory.EnumerateFileSystemEntries(path).GetEnumerator().MoveNext()</c>.
    /// Returns silently on success; on failure, propagates the underlying
    /// <see cref="System.IO.DirectoryNotFoundException"/>,
    /// <see cref="System.UnauthorizedAccessException"/>, or
    /// <see cref="System.IO.IOException"/> exactly as the runtime raises it.
    /// </summary>
    /// <param name="path">Directory path to probe.</param>
    void ProbeDirectory(string path);
}
