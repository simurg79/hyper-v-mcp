using HyperV.Mcp.Server.Infrastructure;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>
/// <see cref="ITempPathProvider"/> fake that returns a single fixed directory.
/// Tests pass a per-test directory (e.g.
/// <c>Path.Combine(Path.GetTempPath(), "hvmcp-tests", Guid.NewGuid().ToString("N"))</c>)
/// so dump-file enumeration touches only that scope and never the process-wide
/// <c>%TEMP%</c>.
///
/// See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md
/// — Decisions TI-D2 and TI-D7.
/// </summary>
public sealed class FixedTempPathProvider : ITempPathProvider
{
    private readonly string _tempPath;

    public FixedTempPathProvider(string tempPath)
    {
        _tempPath = tempPath ?? throw new System.ArgumentNullException(nameof(tempPath));
    }

    /// <inheritdoc />
    public string GetTempPath() => _tempPath;
}
