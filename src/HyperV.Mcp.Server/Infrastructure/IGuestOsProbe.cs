using System.Threading;
using System.Threading.Tasks;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Host-side guest-OS determination for routing, from the Hyper-V guest data exchange (KVP).
/// </summary>
/// <remarks>
/// <para>Deliberately NOT <see cref="IGuestOsClassifier"/>: that classifies an <em>ISO</em> for
/// <c>vm_os_install</c> dispatch — a different domain on a different input. See
/// /myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D15, LGR-A6.</para>
/// <para>Host-side by construction: it needs no guest login, no PowerShell Direct session and no
/// SSH session, so it cannot be defeated by the very misroute it exists to prevent.</para>
/// </remarks>
public interface IGuestOsProbe
{
    /// <summary>
    /// Returns a Linux hint, or <c>null</c> when the guest OS is undeterminable.
    /// </summary>
    /// <remarks>
    /// MUST NOT throw for an undeterminable guest — absence is never a Linux claim. A throw is
    /// absorbed by the caller anyway, but relying on that would make the budget decorative.
    /// </remarks>
    Task<GuestRoutingHint?> ProbeAsync(string hostId, string vmId, CancellationToken ct);

    /// <summary>
    /// An SSH endpoint established for this <paramref name="vmId"/> alone, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The secondary banner signal MUST NOT be aimed at a host-wide or statically shared address:
    /// a host-scoped fallback resolves to the Hyper-V host itself, so a banner from the host — or
    /// from one configured endpoint shared by every VM on it — would record a Linux hint for an
    /// unrelated, possibly Windows, guest and send its guest commands to the wrong machine.
    /// Only per-VM evidence may establish the address a banner is allowed to classify.
    /// See /myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D24.
    /// </remarks>
    Task<(string SshHost, int SshPort)?> ResolveVmScopedSshEndpointAsync(
        string hostId,
        string vmId,
        CancellationToken ct)
        => Task.FromResult<(string SshHost, int SshPort)?>(null);
}
