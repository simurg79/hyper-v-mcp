namespace HyperV.Mcp.Server.Configuration;

/// <summary>
/// Host connection profile for multi-host management.
/// See /myplans/remoting/remoting-design.md — Host Connection Configuration.
/// </summary>
public class HostProfile
{
    /// <summary>
    /// Unique identifier for this host connection.
    /// </summary>
    public required string HostId { get; set; }

    /// <summary>
    /// Hostname, FQDN, or IP. Use "localhost" or "." for local.
    /// </summary>
    public required string ComputerName { get; set; }

    /// <summary>
    /// Trust policy: "local", "strict", or "pinned".
    /// See /myplans/security/trust-certificates/trust-certificates-design.md — ADR-7.
    /// </summary>
    public string TrustPolicy { get; set; } = "local";

    /// <summary>
    /// Use HTTPS for WinRM to remote host. Default true.
    /// See /myplans/security/security-design.md — SEC-D6.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Computed: true when computerName resolves to localhost.
    /// </summary>
    public bool IsLocal =>
        string.Equals(ComputerName, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ComputerName, ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Optional override for base VHDX path on this host.
    /// </summary>
    public string? BaseVhdxPath { get; set; }

    /// <summary>
    /// Optional override for storage root on this host.
    /// </summary>
    public string? StorageRoot { get; set; }

    /// <summary>
    /// Optional override for default virtual switch name on this host.
    /// Used by vm_os_install when no explicit switch is specified.
    /// Resolution order: explicit parameter → HYPERV_MCP_DEFAULT_SWITCH env var →
    /// host profile DefaultSwitch → "Default Switch" fallback.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D7.
    /// </summary>
    public string? DefaultSwitch { get; set; }

    /// <summary>
    /// Guest-OS channel hint (REM-D3 / LGS-SSH-D3). Selects the guest command transport:
    /// "windows" → PowerShell Direct (default; existing behavior), "linux" → SSH exec.
    /// Static host-property hint recorded at create/install time; the runtime guest-OS
    /// auto-probe (OQ-LGS-5) is deferred. Any non-"linux" value routes to PSDirect so
    /// every existing profile stays byte-unchanged.
    /// See /myplans/remoting/linux-guest-support/linux-ssh-exec-first-slice-design.md — LGS-SSH-D3.
    /// </summary>
    public string GuestOs { get; set; } = "windows";

    /// <summary>
    /// Computed: true when <see cref="GuestOs"/> selects the SSH (Linux) guest channel.
    /// </summary>
    public bool IsLinuxGuest =>
        string.Equals(GuestOs, "linux", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// TCP port for the guest SSH endpoint when <see cref="IsLinuxGuest"/> is true.
    /// Ubuntu 24.04 default is 22. Only consulted on the SSH path.
    /// </summary>
    public int SshPort { get; set; } = 22;

    /// <summary>
    /// Optional explicit hostname/IP for the guest SSH endpoint. When null, the SSH
    /// channel falls back to <see cref="ComputerName"/>. The first slice assumes an
    /// already-reachable guest (Assumption 1); reachability provisioning is deferred.
    /// </summary>
    public string? SshHost { get; set; }

    /// <summary>
    /// The host-scoped SSH endpoint after the <see cref="ComputerName"/> fallback, or null when
    /// no usable endpoint exists. Routing and session opening MUST agree on this single
    /// definition; "GuestOs == linux" alone is not evidence that an endpoint is reachable.
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D3/LGR-D8.
    /// </summary>
    public (string SshHost, int SshPort)? ResolveSshEndpoint()
    {
        var resolvedHost = string.IsNullOrWhiteSpace(SshHost) ? ComputerName : SshHost!;
        if (string.IsNullOrWhiteSpace(resolvedHost) || SshPort <= 0)
        {
            return null;
        }
        return (resolvedHost, SshPort);
    }
}
