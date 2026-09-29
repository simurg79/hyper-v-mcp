namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>Environment seam for deterministic PowerShellExecutor script-dump tests without process-global mutation.
/// See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md — TI-D1, TI-D9, TI-D10.
/// Public for Program.cs DI registration; "internal-only" means not MCP-user-facing.</summary>
public interface IEnvironment
{
    /// <summary>Returns the process environment value, or null if undefined.</summary>
    string? GetEnvironmentVariable(string name);
    void SetEnvironmentVariable(string name, string? value);
}

/// <summary>TI-D9: stateless pass-through to System.Environment.GetEnvironmentVariable, without logging.</summary>
public sealed class SystemEnvironment : IEnvironment
{
    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name)
        => System.Environment.GetEnvironmentVariable(name);

    public void SetEnvironmentVariable(string name, string? value)
        => System.Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
}
