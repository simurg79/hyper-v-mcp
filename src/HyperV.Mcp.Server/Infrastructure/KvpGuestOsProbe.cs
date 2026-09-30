using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Reads the host-side guest data exchange (KVP) intrinsic items for a VM and derives a Linux
/// routing hint from them.
/// See /myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D15, LGR-D23.
/// </summary>
public sealed class KvpGuestOsProbe : IGuestOsProbe
{
    /// <summary>Intrinsic item naming the guest operating system.</summary>
    private const string OsNameItem = "OSName";

    /// <summary>Intrinsic item carrying the numeric platform id; <c>2</c> is the Windows value.</summary>
    private const string OsPlatformIdItem = "OSPlatformId";

    private const string NetworkAddressIPv4Item = "NetworkAddressIPv4";

    /// <summary>The stable, first-party Windows platform id. Everything else is not Windows.</summary>
    private const string WindowsPlatformId = "2";

    private const int DefaultSshPort = 22;

    /// <summary>Separator emitted between intrinsic items so multi-line CIM-XML can be split back apart.</summary>
    private const string ItemSeparator = "<#KVPITEM#>";

    private readonly IPowerShellExecutor _executor;
    private readonly IHostResolver _hostResolver;
    private readonly ILogger<KvpGuestOsProbe> _logger;

    /// <remarks>
    /// Runs over the out-of-process <see cref="IPowerShellExecutor"/> rather than the singleton
    /// <see cref="IPowerShellHost"/> on purpose: the host serializes on a runspace-global lock that
    /// the guest channel also needs, so an uninterruptible CIM call in an abandoned probe would
    /// stall the very first guest command behind the classification budget it was meant to bound.
    /// See /myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D25.
    /// </remarks>
    public KvpGuestOsProbe(
        IPowerShellExecutor executor,
        IHostResolver hostResolver,
        ILogger<KvpGuestOsProbe> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<GuestRoutingHint?> ProbeAsync(string hostId, string vmId, CancellationToken ct)
    {
        var items = await ReadIntrinsicItemsAsync(hostId, vmId, ct).ConfigureAwait(false);
        return items is null ? null : BuildHint(items);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The address comes from this VM's own KVP <c>NetworkAddressIPv4</c> item, so the endpoint a
    /// banner is allowed to classify is per-VM by construction. Crucially this item is published
    /// by the integration services well before the OS-identity items, which is exactly the
    /// post-restart window the banner signal exists to cover.
    /// </remarks>
    public async Task<(string SshHost, int SshPort)?> ResolveVmScopedSshEndpointAsync(
        string hostId,
        string vmId,
        CancellationToken ct)
    {
        var items = await ReadIntrinsicItemsAsync(hostId, vmId, ct).ConfigureAwait(false);
        if (items is null)
        {
            return null;
        }

        items.TryGetValue(NetworkAddressIPv4Item, out var addresses);
        var sshHost = SelectRoutableIPv4(addresses);
        return string.IsNullOrWhiteSpace(sshHost) ? null : (sshHost!, DefaultSshPort);
    }

    /// <summary>
    /// Reads this VM's KVP intrinsic items, or <c>null</c> when they cannot be read.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> ReadIntrinsicItemsAsync(
        string hostId,
        string vmId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vmId))
        {
            return null;
        }

        // OS evidence MUST belong to the requested host. Hyper-V GUIDs are not unique across
        // independent hosts, so reading local KVP for a remote request could classify a
        // same-GUID local VM and select a transport for the wrong machine.
        var profile = _hostResolver.Resolve(hostId);
        if (profile is null || !profile.IsLocal)
        {
            _logger.LogDebug(
                "KvpGuestOsProbe: host {HostId} is not local; KVP probing is unsupported there, undeterminable.",
                hostId);
            return null;
        }

        var escapedVmId = vmId.Replace("'", "''");
        // MUST stay on one physical line. A verbatim C# string cannot express a PowerShell
        // line-continuation backtick: `` emits two literal backticks, the first escaping the
        // second, so the command receives a stray positional argument and the probe never runs.
        var script = $@"
$ErrorActionPreference = 'Stop'
$vm = Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName Msvm_ComputerSystem -Filter ""Name='{escapedVmId}'"" -ErrorAction Stop
$kvp = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_KvpExchangeComponent -ErrorAction Stop
foreach ($item in $kvp.GuestIntrinsicExchangeItems) {{ Write-Output '{ItemSeparator}'; Write-Output $item }}
";

        string stdout;
        try
        {
            var result = await _executor
                .ExecuteAsync(script, timeoutSeconds: 2, ct, allowDump: false)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }
            stdout = result.Stdout ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Absence of a determination is the fail-safe direction: routing falls through to
            // today's host-scoped default rather than claiming an OS the host cannot confirm.
            _logger.LogDebug(ex, "KvpGuestOsProbe: KVP read failed for {VmId}; undeterminable.", vmId);
            return null;
        }

        var rawItems = stdout
            .Split(ItemSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(chunk => (object?)chunk.Trim())
            .ToList();

        return ParseIntrinsicItems(rawItems);
    }

    /// <summary>What a single intrinsic item says about the guest OS, including "nothing".</summary>
    internal enum OsEvidence
    {
        /// <summary>The item is absent or blank. Contributes NOTHING in either direction.</summary>
        NoEvidence,
        Windows,
        Linux,
    }

    /// <summary>
    /// Applies the LGR-D29 exact ordinal test to <c>OSPlatformId</c>.
    /// </summary>
    /// <remarks>
    /// The comparison MUST fail toward Linux, never toward Windows: under LGR-D28 it decides
    /// whether to GRANT PowerShell Direct to a guest, so integer-parsing, prefix-matching, or
    /// normalizing a malformed value would silently convert it into a PSDirect grant.
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D29.
    /// </remarks>
    internal static OsEvidence EvaluatePlatformId(IReadOnlyDictionary<string, string> items)
    {
        if (!items.TryGetValue(OsPlatformIdItem, out var platformId)
            || string.IsNullOrWhiteSpace(platformId))
        {
            return OsEvidence.NoEvidence;
        }
        return string.Equals(platformId.Trim(), WindowsPlatformId, StringComparison.Ordinal)
            ? OsEvidence.Windows
            : OsEvidence.Linux;
    }

    /// <summary>
    /// Applies the LGR-D29 word-boundary token test to <c>OSName</c>.
    /// </summary>
    /// <remarks>
    /// Word-boundary matching, never <c>String.Contains</c>: a Linux <c>OSName</c> carrying the
    /// token as a substring would otherwise be granted PowerShell Direct (issue #289 bug class).
    /// </remarks>
    internal static OsEvidence EvaluateOsName(IReadOnlyDictionary<string, string> items)
    {
        if (!items.TryGetValue(OsNameItem, out var osName) || string.IsNullOrWhiteSpace(osName))
        {
            return OsEvidence.NoEvidence;
        }
        return TokenMatcher.ContainsToken(osName, "Windows")
            ? OsEvidence.Windows
            : OsEvidence.Linux;
    }

    /// <summary>
    /// Applies the LGR-D28 three-valued determination rule to already-parsed intrinsic items,
    /// returning <c>null</c> only when the guest OS is genuinely undeterminable.
    /// </summary>
    /// <remarks>
    /// <para>Each item is evaluated independently and a <c>NoEvidence</c> item MUST NOT be read as
    /// agreeing with the other one. Deriving known-Windows from "both Linux predicates are false"
    /// would record known-Windows for an EMPTY KVP pool, which under LGR-D31 then suppresses all
    /// further probing — reinstating the fail-open misroute permanently under a new name.</para>
    /// <para>Linux takes precedence on disagreement, and Linux-ness stays a <em>negative</em>
    /// (not-Windows) test: the set of Linux <c>OSName</c> strings is open-ended, so a positive
    /// enumeration would misclassify an unfamiliar distribution as Windows (LGR-A10).</para>
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D28.
    /// </remarks>
    internal static GuestRoutingHint? BuildHint(IReadOnlyDictionary<string, string> items)
    {
        var platformEvidence = EvaluatePlatformId(items);
        var nameEvidence = EvaluateOsName(items);

        var anyLinux = platformEvidence == OsEvidence.Linux || nameEvidence == OsEvidence.Linux;
        var anyWindows = platformEvidence == OsEvidence.Windows || nameEvidence == OsEvidence.Windows;

        if (!anyLinux)
        {
            // Both NoEvidence ⇒ undeterminable; routing refuses rather than guessing (LGR-D27).
            return anyWindows ? new GuestRoutingHint(GuestOsKind.Windows, null, 0) : null;
        }

        items.TryGetValue(NetworkAddressIPv4Item, out var addresses);
        var sshHost = SelectRoutableIPv4(addresses);

        // A Linux determination with no usable address is still recorded: dropping it would
        // restore the Windows-only PSDirect default for a guest known not to be Windows (LGR-D8).
        return new GuestRoutingHint(GuestOsKind.Linux, sshHost, DefaultSshPort);
    }

    /// <summary>First non-loopback, non-link-local IPv4 address, or <c>null</c>.</summary>
    internal static string? SelectRoutableIPv4(string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return null;
        }

        foreach (var candidate in addresses.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IPAddress.TryParse(candidate.Trim(), out var parsed)
                || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var octets = parsed.GetAddressBytes();
            if (octets[0] == 127)
            {
                continue;
            }
            if (octets[0] == 169 && octets[1] == 254)
            {
                continue;
            }
            return parsed.ToString();
        }

        return null;
    }

    /// <summary>
    /// Each intrinsic item is a CIM-XML <c>INSTANCE</c> fragment whose <c>Name</c> / <c>Data</c>
    /// properties carry the pair.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseIntrinsicItems(IReadOnlyList<object?> rawItems)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rawItems is null)
        {
            return parsed;
        }

        foreach (var rawItem in rawItems)
        {
            var xml = rawItem?.ToString();
            if (string.IsNullOrWhiteSpace(xml))
            {
                continue;
            }

            string? name = null;
            string? data = null;
            try
            {
                var instance = XElement.Parse(xml);
                foreach (var property in instance.Elements("PROPERTY"))
                {
                    var propertyName = property.Attribute("NAME")?.Value;
                    var propertyValue = property.Element("VALUE")?.Value;
                    if (string.Equals(propertyName, "Name", StringComparison.OrdinalIgnoreCase))
                    {
                        name = propertyValue;
                    }
                    else if (string.Equals(propertyName, "Data", StringComparison.OrdinalIgnoreCase))
                    {
                        data = propertyValue;
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                // Unparseable ⇒ undeterminable for that item, never a claim about the OS.
                continue;
            }

            if (!string.IsNullOrWhiteSpace(name) && data is not null)
            {
                parsed[name!] = data;
            }
        }

        return parsed;
    }

    /// <summary>
    /// Secondary signal for the window in which KVP intrinsic data is not yet published — which is
    /// exactly the window a restart creates, so without it restart re-establishment would only
    /// nominally satisfy durability. An SSH banner means "route this guest over SSH" regardless of
    /// what any OS string says; it is NOT a general OS detector.
    /// See /myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D24.
    /// </summary>
    internal static async Task<bool> RespondsWithSshBannerAsync(
        string sshHost,
        int sshPort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sshHost) || sshPort <= 0)
        {
            return false;
        }

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(sshHost, sshPort, ct).ConfigureAwait(false);
            using var stream = tcpClient.GetStream();

            // TCP does not preserve message boundaries, so a valid server may deliver the banner
            // one fragment at a time. Requiring a single read to contain the whole prefix would
            // reject a perfectly good Linux guest and negatively cache it, breaking the declared
            // restart-recovery signal (LGR-D24).
            const string BannerPrefix = "SSH-2.0-";
            var buffer = new byte[64];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), ct)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    return false;
                }
                filled += read;

                var seen = System.Text.Encoding.ASCII.GetString(buffer, 0, filled);
                if (seen.Length >= BannerPrefix.Length)
                {
                    return seen.StartsWith(BannerPrefix, StringComparison.Ordinal);
                }
                // Enough to decide "not SSH" already, or a newline ended the line early.
                if (!BannerPrefix.StartsWith(seen, StringComparison.Ordinal) ||
                    seen.IndexOf('\n') >= 0)
                {
                    return false;
                }
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Refused, timed out, or spoke something else ⇒ still undeterminable.
            return false;
        }
    }
}
