using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// The guest OS an ISO is classified as for <c>vm_os_install</c> dispatch (ISO-D21).
/// Computed once, up front, from the ISO; <c>vm_os_install</c> branches on it.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D21.
/// </summary>
public enum InstallTarget
{
    /// <summary>ISO carries <c>sources\install.wim</c> — the existing Windows path.</summary>
    Windows,

    /// <summary>ISO carries a <c>casper/</c> live-installer layout AND asserts 24.04.</summary>
    UbuntuServer2404,

    /// <summary>Neither supported target — rejected with <c>OS_NOT_SUPPORTED</c> (ISO-D16/D23).</summary>
    Unsupported,
}

/// <summary>
/// Single up-front OS-classification seam for <c>vm_os_install</c> (ISO-D21). Wraps
/// <see cref="IIsoInspector"/> and returns the <see cref="InstallTarget"/> so the manager branches
/// into the untouched Windows orchestrator or the Ubuntu one. An interface so the Tester can fake
/// every recognition case without real media.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D21, Appendix A.1.
/// </summary>
public interface IGuestOsClassifier
{
    /// <summary>
    /// Classifies the ISO at <paramref name="isoPath"/>. Order is fixed (ISO-D22):
    /// (1) <c>sources\install.wim</c> present → <see cref="InstallTarget.Windows"/>;
    /// (2) else <c>casper/</c> + 24.04 assertion → <see cref="InstallTarget.UbuntuServer2404"/>;
    /// (3) else <see cref="InstallTarget.Unsupported"/>.
    /// </summary>
    Task<InstallTarget> ClassifyAsync(string isoPath, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="ClassifyAsync"/>, but also carries out the GRUB configuration the Ubuntu probe
    /// read from the SAME mount, so autoinstall capability costs no additional mount. Null for every
    /// non-Ubuntu outcome.
    /// Deliberately abstract: a default implementation would let a test double silently answer with
    /// <c>default</c> — the masked-mock failure mode this repo has been bitten by repeatedly — where
    /// an abstract member forces every double to state its answer.
    /// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
    /// — UMD-D2, UMD-D9.
    /// </summary>
    Task<GuestOsClassification> ClassifyMediaAsync(string isoPath, CancellationToken ct = default);
}

/// <summary>
/// One classification observation: the dispatch target plus the Ubuntu media's GRUB configuration
/// when one was read.
/// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
/// — UMD-D2.
/// </summary>
public readonly record struct GuestOsClassification(
    InstallTarget Target,
    string? GrubConfiguration);

/// <summary>
/// <see cref="IIsoInspector"/>-backed <see cref="IGuestOsClassifier"/>. The ONLY new place
/// OS sniffing occurs (ISO-D21) — a single classification point keeps the Windows path
/// byte-for-byte unchanged and gives the Tester one mockable seam.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D21/D22.
/// </summary>
public sealed class GuestOsClassifier : IGuestOsClassifier
{
    private readonly IIsoInspector _isoInspector;
    private readonly ILogger<GuestOsClassifier> _logger;

    public GuestOsClassifier(IIsoInspector isoInspector, ILogger<GuestOsClassifier> logger)
    {
        _isoInspector = isoInspector ?? throw new ArgumentNullException(nameof(isoInspector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<InstallTarget> ClassifyAsync(string isoPath, CancellationToken ct = default)
        => (await ClassifyMediaAsync(isoPath, ct).ConfigureAwait(false)).Target;

    /// <inheritdoc />
    public async Task<GuestOsClassification> ClassifyMediaAsync(string isoPath, CancellationToken ct = default)
    {
        // FAIL-CLOSED: a supported target resolves ONLY on a Confirmed probe. Both NotConfirmed and
        // InspectionFailed fall through, so an unprobeable ISO resolves Unsupported → OS_NOT_SUPPORTED
        // and can never be mis-driven into a target orchestrator (issue #208 TC-L01 — the wrong
        // LINUX_PROVISION_FAILED code).
        // See myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D16.1 / ISO-D32.

        // Windows wins first, preserving the pre-existing install.wim recognition ordering (ISO-D23).
        var windowsProbe = await _isoInspector
            .ProbeWindowsInstallWimAsync(isoPath, ct)
            .ConfigureAwait(false);
        if (windowsProbe == IsoMarkerProbeResult.Confirmed)
        {
            return new GuestOsClassification(InstallTarget.Windows, null);
        }

        var ubuntuProbe = await _isoInspector
            .ProbeUbuntuMediaAsync(isoPath, ct)
            .ConfigureAwait(false);
        if (ubuntuProbe.Result == IsoMarkerProbeResult.Confirmed)
        {
            return new GuestOsClassification(
                InstallTarget.UbuntuServer2404, ubuntuProbe.GrubConfiguration);
        }

        _logger.LogDebug(
            "ISO '{IsoPath}' classified Unsupported (windows probe: {WindowsProbe}; ubuntu probe: {UbuntuProbe}).",
            isoPath, windowsProbe, ubuntuProbe.Result);

        return new GuestOsClassification(InstallTarget.Unsupported, null);
    }
}
