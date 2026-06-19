using HyperV.Mcp.Server.Configuration;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Default <see cref="IImagePathResolver"/> implementation. Reuses
/// <see cref="IFileSystemProbe"/> for the same probe-time exception
/// classification that <c>HyperVManager.ListImagesAsync</c> uses (Issue #73),
/// so warm-up path enumeration is testable and behaviorally consistent with
/// <c>vm_list_images</c>.
///
/// See /myplans/vm-management/vm-create/vm-create-unblocking-design.md — VC-D4.
/// </summary>
public sealed class ImagePathResolver : IImagePathResolver
{
    private const string ImageDirEnvVar = "HYPERV_MCP_IMAGE_DIR";
    private const string BaseVhdxEnvVar = "HYPERV_MCP_BASE_VHDX";
    private const string VhdxSearchPattern = "*.vhdx";

    private readonly ServerOptions _options;
    private readonly IFileSystemProbe _fileSystemProbe;
    private readonly ILogger<ImagePathResolver> _logger;

    public ImagePathResolver(
        ServerOptions options,
        IFileSystemProbe fileSystemProbe,
        ILogger<ImagePathResolver> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fileSystemProbe = fileSystemProbe ?? throw new ArgumentNullException(nameof(fileSystemProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ResolveWarmUpPathsAsync(CancellationToken ct)
    {
        // Synchronous work wrapped in Task.FromResult — the resolver does no
        // I/O beyond directory enumeration, and that is bounded + cheap.
        // Honors lifetime cancellation between sources but does not preempt
        // mid-enumeration (Directory.EnumerateFiles is not cancellation-aware).
        ct.ThrowIfCancellationRequested();

        var paths = new List<string>();

        // (1) HYPERV_MCP_IMAGE_DIR.
        var envImageDir = Environment.GetEnvironmentVariable(ImageDirEnvVar);
        if (!string.IsNullOrWhiteSpace(envImageDir))
        {
            EnumerateDirectory(envImageDir, paths, sourceLabel: $"env:{ImageDirEnvVar}");
        }

        // (2) Per-host-profile BaseVhdxPath parent directories.
        if (_options.Hosts is not null)
        {
            foreach (var kv in _options.Hosts)
            {
                ct.ThrowIfCancellationRequested();
                var profile = kv.Value;
                if (string.IsNullOrWhiteSpace(profile?.BaseVhdxPath)) continue;

                var parent = System.IO.Path.GetDirectoryName(profile.BaseVhdxPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    EnumerateDirectory(parent, paths, sourceLabel: $"host:{kv.Key}:parent");
                }
            }
        }

        // (3) HYPERV_MCP_BASE_VHDX — single file path (NOT its parent).
        var envBaseVhdx = Environment.GetEnvironmentVariable(BaseVhdxEnvVar);
        if (!string.IsNullOrWhiteSpace(envBaseVhdx))
        {
            TryAddFile(envBaseVhdx, paths, sourceLabel: $"env:{BaseVhdxEnvVar}");
        }

        // (4) Per-host-profile BaseVhdxPath file paths.
        if (_options.Hosts is not null)
        {
            foreach (var kv in _options.Hosts)
            {
                ct.ThrowIfCancellationRequested();
                var profile = kv.Value;
                if (string.IsNullOrWhiteSpace(profile?.BaseVhdxPath)) continue;
                TryAddFile(profile.BaseVhdxPath, paths, sourceLabel: $"host:{kv.Key}:file");
            }
        }

        var deduped = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeOrSelf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "ImagePathResolver resolved {Count} unique warm-up path(s).", deduped.Count);

        return Task.FromResult<IReadOnlyList<string>>(deduped);
    }

    private void EnumerateDirectory(string directory, List<string> sink, string sourceLabel)
    {
        try
        {
            // Probe first — preserves the seamed exception fidelity (Issue #73).
            _fileSystemProbe.ProbeDirectory(directory);

            foreach (var file in System.IO.Directory.EnumerateFiles(
                         directory, VhdxSearchPattern, System.IO.SearchOption.TopDirectoryOnly))
            {
                sink.Add(file);
            }
        }
        catch (System.IO.DirectoryNotFoundException ex)
        {
            _logger.LogWarning(
                "ImagePathResolver ({Source}): directory '{Dir}' not found — skipping. {Msg}",
                sourceLabel, directory, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "ImagePathResolver ({Source}): directory '{Dir}' is not accessible — skipping. {Msg}",
                sourceLabel, directory, ex.Message);
        }
        catch (System.IO.IOException ex)
        {
            _logger.LogWarning(
                "ImagePathResolver ({Source}): IO error enumerating '{Dir}' — skipping. {Msg}",
                sourceLabel, directory, ex.Message);
        }
    }

    private void TryAddFile(string filePath, List<string> sink, string sourceLabel)
    {
        // VC-D4: inaccessible file paths produce a Failed row downstream via
        // WarmAsync's per-path try/catch; we still add the path here so the
        // failure is recorded with a structured ErrorCode rather than silently
        // dropped. Only skip if the path string itself is unusable.
        try
        {
            var full = System.IO.Path.GetFullPath(filePath);
            sink.Add(full);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "ImagePathResolver ({Source}): path '{Path}' is not a valid filesystem path — skipping. {Msg}",
                sourceLabel, filePath, ex.Message);
        }
    }

    private static string NormalizeOrSelf(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch { return path; }
    }
}
