using System.Collections.Generic;
using HyperV.Mcp.Server.Infrastructure;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IEnvironment"/> fake for unit/integration tests. Backed by
/// a case-insensitive dictionary so callers can pre-seed values (e.g.
/// <c>HYPERV_MCP_DUMP_PS_SCRIPTS</c>) without mutating the process-wide
/// environment block.
///
/// See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md
/// — Decision TI-D6 (replace direct <c>Environment.SetEnvironmentVariable</c>
/// calls with seam injection).
/// </summary>
public sealed class FakeEnvironment : IEnvironment
{
    private readonly Dictionary<string, string?> _values =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Construct an empty fake. Add values with <see cref="Set"/> or the
    /// initializer-friendly constructor overload.
    /// </summary>
    public FakeEnvironment()
    {
    }

    /// <summary>
    /// Construct a fake pre-seeded with the supplied <paramref name="initialValues"/>.
    /// </summary>
    public FakeEnvironment(IEnumerable<KeyValuePair<string, string?>> initialValues)
    {
        foreach (var kvp in initialValues)
        {
            _values[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Set (or clear, if <paramref name="value"/> is <see langword="null"/>) the
    /// in-memory value for <paramref name="name"/>. Returns <see langword="this"/>
    /// to allow fluent chaining.
    /// </summary>
    public FakeEnvironment Set(string name, string? value)
    {
        _values[name] = value;
        return this;
    }

    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name)
        => _values.TryGetValue(name, out var v) ? v : null;
}
