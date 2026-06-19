namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// VC-D4: resolves the deduplicated set of base-VHDX file paths that the
/// startup warm-up (<see cref="IBaseImageHashCache.WarmAsync"/>) should
/// pre-hash. Extracted as an interface so the warm-up path is unit-testable
/// without spinning up <see cref="HyperVManager"/>.
///
/// Resolution order mirrors <c>HyperVManager.ListImagesAsync</c> (ST-D7) and
/// the per-call resolver inside <c>CreateVmAsync</c>:
/// <list type="number">
///   <item><c>HYPERV_MCP_IMAGE_DIR</c> environment variable (directory; non-recursive enumeration).</item>
///   <item>Each host-profile <c>BaseVhdxPath</c>'s parent directory (non-recursive enumeration).</item>
///   <item><c>HYPERV_MCP_BASE_VHDX</c> environment variable (single file path — included directly, not its parent).</item>
///   <item>Each host-profile <c>BaseVhdxPath</c>'s file path (individual files).</item>
/// </list>
///
/// Contract:
/// <list type="bullet">
///   <item>Empty / unconfigured ⇒ returns an empty list (no exception, no fallback paths).</item>
///   <item>Inaccessible directory or file (missing, permission denied, IO) ⇒ logged as a structured warning and SKIPPED; the warm-up records it as a <see cref="WarmUpPathStatus.Failed"/> row when it later tries to hash the path. The resolver itself does not throw on per-path access failures.</item>
///   <item>Resolver-level catastrophic failure (null config collection, etc.) DOES propagate; the caller's <c>Task.Run</c> wrapper catches it and logs the warm-up as failed.</item>
///   <item>Symbolic links and reparse points are followed (the warm-up follows whatever <c>vm_create</c> would follow).</item>
/// </list>
/// </summary>
public interface IImagePathResolver
{
    /// <summary>
    /// Resolves the warm-up path set.
    /// </summary>
    /// <param name="ct">Lifetime-scoped cancellation token (typically
    /// <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping"/>).</param>
    /// <returns>Deduplicated (case-insensitive on Windows) absolute paths to
    /// <c>.vhdx</c> files.</returns>
    Task<IReadOnlyList<string>> ResolveWarmUpPathsAsync(CancellationToken ct);
}
