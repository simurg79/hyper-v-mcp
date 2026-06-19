namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Thin seam over <see cref="System.IO.Path.GetTempPath"/> so tests can redirect
/// the directory under which <see cref="PowerShellExecutor"/> stages its
/// <c>hvmcp-*.ps1</c> temp script into a per-test directory.
///
/// See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md
/// — Decisions TI-D2, TI-D9, TI-D10. "Internal-only" in the design refers to
/// "not MCP-user-facing"; the interface is <see langword="public"/> so DI
/// registration in <c>Program.cs</c> can resolve it.
/// </summary>
public interface ITempPathProvider
{
    /// <summary>
    /// Returns the path to the current user's temporary folder. Pass-through to
    /// <see cref="System.IO.Path.GetTempPath"/>.
    /// </summary>
    string GetTempPath();
}

/// <summary>
/// Default <see cref="ITempPathProvider"/> implementation that delegates directly
/// to <see cref="System.IO.Path.GetTempPath"/>. Stateless, no logging, no fields
/// — a pure pass-through (TI-D9).
/// </summary>
public sealed class SystemTempPathProvider : ITempPathProvider
{
    /// <inheritdoc />
    public string GetTempPath() => System.IO.Path.GetTempPath();
}
