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
}
