using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>Which tool authored the ISO image. Host-side diagnostic only — never caller-facing.</summary>
public enum MediaAuthoringRoute
{
    /// <summary>The optional ADK <c>oscdimg.exe</c> was present and used.</summary>
    Oscdimg,

    /// <summary>The in-box IMAPI2FS COM path — the normal route on a stock Windows host.</summary>
    Imapi2,
}

/// <summary>
/// Outcome of one authoring attempt. <see cref="Cause"/> is the RAW underlying text; sanitization
/// and truncation happen at the surfacing boundary, in that order, so a cut cannot bisect a
/// credential pattern.
/// See internal documentation — ISO-D38.
/// </summary>
public sealed record SeedMediaAuthoringResult(MediaAuthoringRoute Route, bool Success, string? Cause);

/// <summary>
/// Authors a NoCloud-style seed ISO from an already-staged directory. Split out of the
/// orchestrator so a regression guard can drive the real IMAPI2 COM path with only the route probe
/// substituted — no <see cref="IPowerShellExecutor"/> stubbing.
/// See internal documentation — ISO-D40.
/// </summary>
public interface ISeedMediaAuthor
{
    Task<SeedMediaAuthoringResult> AuthorAsync(
        string stagingRoot, string volumeLabel, string outputPath, CancellationToken ct = default);
}

/// <summary>
/// Production <see cref="ISeedMediaAuthor"/>. The route is chosen here, in C#, BEFORE the script is
/// built — the script no longer decides its own route, which is what made the IMAPI2 branch
/// unreachable from tests and let it ship dead (issue #292).
/// See internal documentation — ISO-D40.
/// </summary>
public sealed class SeedMediaAuthor : ISeedMediaAuthor
{
    private readonly IPowerShellExecutor _psExecutor;
    private readonly IOscdimgProbe _oscdimgProbe;
    private readonly ILogger<SeedMediaAuthor> _logger;

    public SeedMediaAuthor(
        IPowerShellExecutor psExecutor,
        IOscdimgProbe oscdimgProbe,
        ILogger<SeedMediaAuthor> logger)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _oscdimgProbe = oscdimgProbe ?? throw new ArgumentNullException(nameof(oscdimgProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SeedMediaAuthoringResult> AuthorAsync(
        string stagingRoot, string volumeLabel, string outputPath, CancellationToken ct = default)
    {
        var oscdimgPath = _oscdimgProbe.ResolveOscdimgPath();
        var route = oscdimgPath is null ? MediaAuthoringRoute.Imapi2 : MediaAuthoringRoute.Oscdimg;

        var escapedStagingRoot = InputValidation.EscapePowerShellString(stagingRoot);
        var escapedOutputPath = InputValidation.EscapePowerShellString(outputPath);
        var escapedVolumeLabel = InputValidation.EscapePowerShellString(volumeLabel);

        var authoringBlock = route == MediaAuthoringRoute.Oscdimg
            ? BuildOscdimgBlock(InputValidation.EscapePowerShellString(oscdimgPath!), escapedVolumeLabel)
            : MediaAuthoringScripts.BuildImapi2AuthoringBlock(
                sourceDirExpression: "$staging",
                outputPathExpression: "$outputIso",
                volumeLabel: escapedVolumeLabel,
                fileSystemsToCreate: MediaAuthoringScripts.FileSystemsIso9660Joliet,
                failureMessagePrefix: "Failed to author seed media");

        var routeMarker = RouteToken(route);

        var script = $@"
$ErrorActionPreference = 'Stop'
$staging = '{escapedStagingRoot}'
$outputIso = '{escapedOutputPath}'
{authoringBlock}
if (-not (Test-Path -LiteralPath $outputIso -PathType Leaf)) {{
    throw 'Seed ISO creation failed: output not found.'
}}
Write-Output 'SEED_AUTHOR={routeMarker}'
";

        // allowDump:false — the staged user-data carries a credential hash, so this call stays out
        // of the script-dump diagnostic entirely (SD-D4).
        var result = await _psExecutor
            .ExecuteAsync(script, timeoutSeconds: 120, ct: ct, allowDump: false)
            .ConfigureAwait(false);

        if (result.Success)
        {
            _logger.LogDebug(
                "Seed media authored via route '{Route}' at '{OutputPath}'.", routeMarker, outputPath);
            return new SeedMediaAuthoringResult(route, true, null);
        }

        var cause = !string.IsNullOrWhiteSpace(result.Stderr) ? result.Stderr : result.Stdout;
        if (result.TimedOut && string.IsNullOrWhiteSpace(cause))
        {
            cause = "The media-authoring step timed out.";
        }

        _logger.LogWarning(
            "Seed media authoring failed via route '{Route}': timedOut={TimedOut} exit={ExitCode}",
            routeMarker, result.TimedOut, result.ExitCode);

        return new SeedMediaAuthoringResult(route, false, string.IsNullOrWhiteSpace(cause) ? null : cause);
    }

    /// <summary>
    /// Token is stable and lowercase because it is consumed as a wire value in the failure payload,
    /// not just displayed in logs.
    /// </summary>
    internal static string RouteToken(MediaAuthoringRoute route)
        => route == MediaAuthoringRoute.Oscdimg ? "oscdimg" : "imapi2";

    private static string BuildOscdimgBlock(string escapedOscdimgPath, string escapedVolumeLabel)
        => $@"
& '{escapedOscdimgPath}' -l{escapedVolumeLabel} -n $staging $outputIso | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw ""oscdimg.exe failed with exit code $LASTEXITCODE"" }}
";
}
