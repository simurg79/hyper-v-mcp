using HyperV.Mcp.Server.Infrastructure;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Shared hand-rolled stub of <see cref="IIsoInspector"/> used by tests that
/// construct a <see cref="HyperVManager"/> directly. Returns
/// <c>(true, null)</c> from <see cref="ContainsWindowsInstallWimWithDiagnosticAsync"/>
/// by default so existing tests (which exercise OS-agnostic paths) are not
/// rejected as non-Windows by the new ISO-D16 preflight.
///
/// Issue #97 / Gate 5 fixture: introduced when <see cref="HyperVManager"/>'s
/// constructor gained a required <see cref="IIsoInspector"/> dependency.
/// Tests targeting OS-family / preflight behavior should use a
/// <see cref="Moq.Mock{IIsoInspector}"/> instead and override the result.
/// </summary>
internal sealed class TestIsoInspector : IIsoInspector
{
    private readonly bool _found;
    private readonly string? _diagnostic;

    public TestIsoInspector(bool found = true, string? diagnostic = null)
    {
        _found = found;
        _diagnostic = diagnostic;
    }

    public Task<(bool Found, string? Diagnostic)> ContainsWindowsInstallWimWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default)
        => Task.FromResult((_found, _diagnostic));
}
