using System.Collections.Generic;
using HyperV.Mcp.Server.Infrastructure;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>Case-insensitive IEnvironment fake permits pre-seeded values without process-global mutation.
/// See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md — TI-D6: inject the seam instead of Environment.SetEnvironmentVariable.</summary>
public sealed class FakeEnvironment : IEnvironment
{
    private readonly Dictionary<string, string?> _values =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Start empty; add values with Set or the initializer-friendly overload.</summary>
    public FakeEnvironment()
    {
    }


    public FakeEnvironment(IEnumerable<KeyValuePair<string, string?>> initialValues)
    {
        foreach (var kvp in initialValues)
        {
            _values[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>Null clears a value; return this for fluent chaining.</summary>
    public FakeEnvironment Set(string name, string? value)
    {
        _values[name] = value;
        return this;
    }

    public void SetEnvironmentVariable(string name, string? value) => _values[name] = value;

    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name)
        => _values.TryGetValue(name, out var v) ? v : null;
}
