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
    private readonly bool _casperFound;
    private readonly string? _casperDiagnostic;

    public TestIsoInspector(
        bool found = true,
        string? diagnostic = null,
        bool casperFound = false,
        string? casperDiagnostic = null)
    {
        _found = found;
        _diagnostic = diagnostic;
        _casperFound = casperFound;
        _casperDiagnostic = casperDiagnostic;
    }

    public Task<(bool Found, string? Diagnostic)> ContainsWindowsInstallWimWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default)
        => Task.FromResult((_found, _diagnostic));

    // Issue #208 / ISO-D22: default is "not Ubuntu" so existing Windows-oriented tests
    // (found=true) still classify as Windows; opt-in via casperFound for Ubuntu cases.
    public Task<(bool Found, string? Diagnostic)> ContainsCasperLayoutWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default)
        => Task.FromResult((_casperFound, _casperDiagnostic));

    // Issue #370 / UMD-D2: Ubuntu-target fixtures must look like PREPARED media, otherwise the
    // capability refusal fires and every pre-existing dispatch test would be rejected before it
    // reaches the behavior it actually exercises.
    public Task<UbuntuMediaProbe> ProbeUbuntuMediaAsync(string isoPath, CancellationToken ct = default)
        => Task.FromResult(new UbuntuMediaProbe(
            _casperFound ? IsoMarkerProbeResult.Confirmed : IsoMarkerProbeResult.NotConfirmed,
            _casperFound ? PreparedGrubConfiguration : null));

    internal const string PreparedGrubConfiguration =
        "menuentry \"Install\" {\n\tlinux /casper/vmlinuz autoinstall ds=nocloud;s=/cdrom/ ---\n}\n";
}
