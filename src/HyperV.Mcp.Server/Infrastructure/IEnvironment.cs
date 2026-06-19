namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Thin seam over <see cref="System.Environment"/> for reading process environment
/// variables. Introduced to make the script-dump diagnostic in
/// <see cref="PowerShellExecutor"/> deterministically testable without mutating
/// process-global state.
///
/// See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md
/// — Decisions TI-D1, TI-D9, TI-D10. "Internal-only" in the design refers to
/// "not MCP-user-facing"; the interface is <see langword="public"/> so DI
/// registration in <c>Program.cs</c> can resolve it.
/// </summary>
public interface IEnvironment
{
    /// <summary>
    /// Returns the value of the named environment variable, or <see langword="null"/>
    /// if it is not defined. Pass-through to
    /// <see cref="System.Environment.GetEnvironmentVariable(string)"/>.
    /// </summary>
    string? GetEnvironmentVariable(string name);
}

/// <summary>
/// Default <see cref="IEnvironment"/> implementation that delegates directly to
/// <see cref="System.Environment.GetEnvironmentVariable(string)"/>. Stateless,
/// no logging, no fields — a pure pass-through (TI-D9).
/// </summary>
public sealed class SystemEnvironment : IEnvironment
{
    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name)
        => System.Environment.GetEnvironmentVariable(name);
}
