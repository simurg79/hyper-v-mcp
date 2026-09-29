using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// The absence of a record means UNKNOWN, never Windows. Overloading absence as Windows is the
/// host-OS inference that misroutes an unclassified guest onto the Windows-only transport, so
/// Windows must be an explicitly recorded classification for the unknown case to be representable.
/// See internal documentation — LGR-D27.
/// </summary>
public enum GuestOsKind
{
    Linux,
    Windows,
}

/// <summary>
/// Credentials MUST NOT be stored here — they flow per-call.
/// See internal documentation — LGR-D2/D3.
/// </summary>
public sealed record GuestRoutingHint(GuestOsKind GuestOs, string? SshHost, int SshPort)
{
    public bool HasUsableSshEndpoint => !string.IsNullOrWhiteSpace(SshHost) && SshPort > 0;
}

/// <summary>
/// Per-VM seam so routing does not depend solely on the static host property
/// <see cref="Configuration.HostProfile.GuestOs"/>.
/// See internal documentation — LGR-D2.
/// </summary>
/// <remarks>
/// Identity is the compound <c>(hostId, vmId)</c>. A VM GUID alone is NOT unique across
/// independent hosts — imports and clones reproduce one — so a VM-only key would let a Linux
/// result on one host drive routing (and deletion) for a Windows VM on another.
/// See internal documentation — LGR-D7.
/// </remarks>
public interface IGuestRoutingHintStore
{
    void Record(string hostId, string vmId, GuestRoutingHint hint);
    bool TryGet(string hostId, string vmId, [NotNullWhen(true)] out GuestRoutingHint? hint);
    void Remove(string hostId, string vmId);
}

public sealed class GuestRoutingHintStore : IGuestRoutingHintStore
{
    private readonly ConcurrentDictionary<string, GuestRoutingHint> _hints = new();

    /// <summary>
    /// Compound key. The separator cannot occur in a Hyper-V GUID, so no two distinct
    /// (host, vm) pairs can collide onto one key.
    /// </summary>
    internal static string BuildKey(string hostId, string vmId)
        => $"{hostId ?? string.Empty}::{vmId}";

    public void Record(string hostId, string vmId, GuestRoutingHint hint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        ArgumentNullException.ThrowIfNull(hint);
        _hints[BuildKey(hostId, vmId)] = hint;
    }

    public bool TryGet(string hostId, string vmId, [NotNullWhen(true)] out GuestRoutingHint? hint)
    {
        // A blank key is a clean miss; throwing here would fault unrelated routing calls.
        if (string.IsNullOrWhiteSpace(vmId))
        {
            hint = null;
            return false;
        }
        return _hints.TryGetValue(BuildKey(hostId, vmId), out hint);
    }

    public void Remove(string hostId, string vmId)
    {
        if (!string.IsNullOrWhiteSpace(vmId))
        {
            _hints.TryRemove(BuildKey(hostId, vmId), out _);
        }
    }
}
