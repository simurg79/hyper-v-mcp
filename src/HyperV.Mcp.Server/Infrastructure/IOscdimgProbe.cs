namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Resolves the optional ADK <c>oscdimg.exe</c> tool. Exists as a one-member seam so the
/// media-authoring route is a C#-side decision that a test can force, rather than an inline
/// <c>Get-Command</c> buried in an interpolated script where only an
/// <see cref="IPowerShellExecutor"/> stub could influence it.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D40.
/// </summary>
public interface IOscdimgProbe
{
    /// <summary>Full path to <c>oscdimg.exe</c>, or <see langword="null"/> when it is absent.</summary>
    string? ResolveOscdimgPath();
}

/// <summary>
/// Production <see cref="IOscdimgProbe"/>: looks up <c>oscdimg.exe</c> on <c>PATH</c> and in the
/// standard Windows ADK deployment-tools locations. Absence is the normal case on a stock host.
/// </summary>
public sealed class OscdimgProbe : IOscdimgProbe
{
    private static readonly string[] AdkRelativeLocations =
    {
        @"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
        @"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\x86\Oscdimg\oscdimg.exe",
    };

    private readonly IEnvironment _environment;

    public OscdimgProbe(IEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <inheritdoc />
    public string? ResolveOscdimgPath()
    {
        var pathValue = _environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = SafeCombine(directory.Trim().Trim('"'), "oscdimg.exe");
            if (candidate is not null && File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var programFilesVariable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var programFiles = _environment.GetEnvironmentVariable(programFilesVariable);
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                continue;
            }

            foreach (var relative in AdkRelativeLocations)
            {
                var candidate = SafeCombine(programFiles, relative);
                if (candidate is not null && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    // A malformed PATH entry must not abort the probe: an unusable entry is simply "not here".
    private static string? SafeCombine(string directory, string relative)
    {
        try
        {
            return string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, relative);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
