using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Outcome of one guest-completion poll for the Ubuntu autoinstall (ISO-D26). Derived solely from
/// the guest KVP <c>hyperv-mcp/os-install</c> — the ONLY completion authority; no heartbeat/uptime
/// fallback (OQ-U2 resolved KVP-only: a liveness signal is not an install-complete signal). Carries
/// no secrets — the grammar is <c>ready</c> / <c>failed:&lt;shortcode&gt;</c> only.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D26 / OQ-U2.
/// </summary>
public enum GuestCompletionSignal
{
    /// <summary>No completion signal observed yet — keep polling until the deadline.</summary>
    Pending,

    /// <summary>Guest published <c>ready</c> — install finished, guest is usable.</summary>
    Ready,

    /// <summary>Guest published <c>failed:*</c> — terminal provisioning failure.</summary>
    Failed,
}

/// <summary>
/// One completion observation: the mapped <see cref="Signal"/> plus a redaction-safe
/// short diagnostic (e.g. the <c>failed:</c> shortcode). Never carries the raw password.
/// </summary>
public readonly record struct GuestCompletionStatus(GuestCompletionSignal Signal, string? Detail);

/// <summary>
/// The guest completion channel's observed availability, surfaced in the timeout error so the caller
/// can tell "channel fine, nothing arrived" from "channel broken" without host access.
/// <see cref="Undetermined"/> is first-class and is never collapsed into <see cref="Unavailable"/>:
/// claiming the channel was down when that was not observed would mislead triage.
/// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
/// — UMD-D6.
/// </summary>
public enum GuestCompletionChannelState
{
    /// <summary>Channel usable; no completion signal arrived.</summary>
    AvailableNoSignal,

    /// <summary>Integration service disabled, or the VM exposes no KVP exchange component.</summary>
    Unavailable,

    /// <summary>Query errored, timed out, produced no recognized token, or the VM was not found.</summary>
    Undetermined,
}

/// <summary>
/// Host-side seam that reads the guest-published Hyper-V KVP completion key without ever
/// opening a guest PSSession (FR-8 / AC-2 / C-3). Modeled as an interface so the Tester can
/// script KVP values and exercise ready / failed / timeout without a real guest.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D26, Appendix A.4.
/// </summary>
public interface IKvpCompletionReader
{
    /// <summary>
    /// Reads the well-known completion KVP (<c>hyperv-mcp/os-install</c>) for
    /// <paramref name="vmName"/> via the Hyper-V data-exchange WMI component and maps it to a
    /// <see cref="GuestCompletionStatus"/>. Absence maps to <see cref="GuestCompletionSignal.Pending"/>.
    /// Read failures are non-fatal and also map to <see cref="GuestCompletionSignal.Pending"/>
    /// so the caller keeps polling until its deadline.
    /// </summary>
    Task<GuestCompletionStatus> ReadCompletionAsync(string vmName, CancellationToken ct = default);

    /// <summary>
    /// Reports the channel's observable availability. Asked once, on the timeout path only — it
    /// answers a different question from <see cref="ReadCompletionAsync"/>, which is why an absent
    /// completion key alone must never be read as "channel unavailable".
    /// Deliberately abstract: a default implementation would let a test double omit the behavior and
    /// still compile, masking the regressions this member exists to catch.
    /// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
    /// — UMD-D6.
    /// </summary>
    Task<GuestCompletionChannelState> ProbeChannelStateAsync(string vmName, CancellationToken ct = default);
}

/// <summary>
/// PowerShell/WMI-backed <see cref="IKvpCompletionReader"/>. Queries the VM's
/// <c>Msvm_KvpExchangeComponent</c> <c>GuestExchangeItems</c> (host-side only) and parses the
/// XML for the well-known key. No PowerShell Direct, no guest session.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D26 / Appendix A.4.
/// </summary>
public sealed class KvpCompletionReader : IKvpCompletionReader
{
    // The well-known guest→host key. Carries no secrets (redaction-safe).
    internal const string CompletionKey = "hyperv-mcp/os-install";
    private const int ReadTimeoutSeconds = 30;

    private readonly IPowerShellExecutor _psExecutor;
    private readonly ILogger<KvpCompletionReader> _logger;

    public KvpCompletionReader(IPowerShellExecutor psExecutor, ILogger<KvpCompletionReader> logger)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<GuestCompletionStatus> ReadCompletionAsync(string vmName, CancellationToken ct = default)
    {
        var escapedName = InputValidation.EscapePowerShellString(vmName);

        // Host-side WMI read of GuestExchangeItems, emitting raw <INSTANCE>..</INSTANCE> XML for
        // the C# side to parse Name/Data pairs. No guest PSSession is opened.
        // See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — Appendix A.4.
        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
    $vm = Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ComputerSystem -Filter ""ElementName='{escapedName}'"" -ErrorAction Stop
    if (-not $vm) {{ Write-Output 'KVP_NO_VM'; return }}
    $kvp = Get-CimAssociatedInstance $vm -ResultClassName Msvm_KvpExchangeComponent -ErrorAction Stop
    if (-not $kvp) {{ Write-Output 'KVP_NONE'; return }}
    $items = $kvp.GuestExchangeItems
    if (-not $items) {{ Write-Output 'KVP_NONE'; return }}
    foreach ($item in $items) {{ Write-Output $item }}
}} catch {{
    Write-Output ('KVP_ERROR:' + ($_.Exception.Message -replace '[\r\n]+',' '))
}}
";
        PowerShellResult result;
        try
        {
            result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: ReadTimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Non-fatal: a failed read is Pending so the caller keeps polling until its deadline
            // (→ LINUX_INSTALL_TIMEOUT). KVP is the sole completion authority (OQ-U2 KVP-only).
            _logger.LogDebug(ex, "KVP completion read threw for VM '{VmName}'; treating as pending.", vmName);
            return new GuestCompletionStatus(GuestCompletionSignal.Pending, null);
        }

        if (result.TimedOut || !string.IsNullOrEmpty(result.Stderr))
        {
            _logger.LogDebug(
                "KVP completion read inconclusive for VM '{VmName}' (timedOut={TimedOut}); treating as pending.",
                vmName, result.TimedOut);
            return new GuestCompletionStatus(GuestCompletionSignal.Pending, null);
        }

        var value = ExtractCompletionValue(result.Stdout ?? string.Empty);
        return MapValue(value);
    }

    /// <inheritdoc />
    public async Task<GuestCompletionChannelState> ProbeChannelStateAsync(
        string vmName, CancellationToken ct = default)
    {
        var escapedName = InputValidation.EscapePowerShellString(vmName);

        // Availability needs BOTH facts: the integration service being enabled, and the VM actually
        // exposing the exchange component. Either one alone can be true while the channel is unusable.
        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
    $vm = Get-VM -Name '{escapedName}' -ComputerName localhost -ErrorAction Stop
    if (-not $vm) {{ Write-Output 'CHANNEL_NO_VM'; return }}
    $service = Get-VMIntegrationService -VMName '{escapedName}' -Name 'Key-Value Pair Exchange' -ComputerName localhost -ErrorAction Stop
    if (-not $service) {{ Write-Output 'CHANNEL_UNAVAILABLE'; return }}
    if (-not $service.Enabled) {{ Write-Output 'CHANNEL_UNAVAILABLE'; return }}
    $system = Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ComputerSystem -Filter ""ElementName='{escapedName}'"" -ErrorAction Stop
    if (-not $system) {{ Write-Output 'CHANNEL_NO_VM'; return }}
    $kvp = Get-CimAssociatedInstance $system -ResultClassName Msvm_KvpExchangeComponent -ErrorAction Stop
    if (-not $kvp) {{ Write-Output 'CHANNEL_UNAVAILABLE'; return }}
    Write-Output 'CHANNEL_AVAILABLE'
}} catch {{
    Write-Output ('KVP_ERROR:' + ($_.Exception.Message -replace '[\r\n]+',' '))
}}
";
        PowerShellResult result;
        try
        {
            result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: ReadTimeoutSeconds, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KVP channel probe threw for VM '{VmName}'; state undetermined.", vmName);
            return GuestCompletionChannelState.Undetermined;
        }

        if (result.TimedOut)
        {
            return GuestCompletionChannelState.Undetermined;
        }

        return MapChannelToken(LastNonEmptyLine(result.Stdout));
    }

    /// <summary>
    /// A VM that cannot be found, a host-side error, and unrecognized output are all Undetermined —
    /// only an explicit disabled/absent component proves Unavailable.
    /// </summary>
    private static GuestCompletionChannelState MapChannelToken(string? token) => token switch
    {
        "CHANNEL_AVAILABLE" => GuestCompletionChannelState.AvailableNoSignal,
        "CHANNEL_UNAVAILABLE" => GuestCompletionChannelState.Unavailable,
        _ => GuestCompletionChannelState.Undetermined,
    };

    private static string? LastNonEmptyLine(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        string? lastLine = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) lastLine = trimmed;
        }
        return lastLine;
    }

    /// <summary>
    /// Parses the emitted <c>Msvm_KvpExchangeDataItem</c> XML instances and returns the
    /// <c>Data</c> value whose <c>Name</c> equals <see cref="CompletionKey"/>, or null.
    /// Tolerant of the host also emitting sentinel tokens (<c>KVP_NONE</c> etc.).
    /// </summary>
    internal static string? ExtractCompletionValue(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        // Each GuestExchangeItem is a CIM-serialized <INSTANCE> with <PROPERTY NAME="Name">
        // and <PROPERTY NAME="Data"> children. Split on INSTANCE boundaries and parse each.
        var startIndex = 0;
        while (true)
        {
            var open = stdout.IndexOf("<INSTANCE", startIndex, StringComparison.OrdinalIgnoreCase);
            if (open < 0) break;
            var closeTag = stdout.IndexOf("</INSTANCE>", open, StringComparison.OrdinalIgnoreCase);
            if (closeTag < 0) break;
            var end = closeTag + "</INSTANCE>".Length;
            var fragment = stdout.Substring(open, end - open);
            startIndex = end;

            var (name, data) = ParseNameData(fragment);
            if (string.Equals(name, CompletionKey, StringComparison.Ordinal))
            {
                return data;
            }
        }
        return null;
    }

    private static (string? Name, string? Data) ParseNameData(string instanceXml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(instanceXml);
            string? name = null;
            string? data = null;
            foreach (XmlNode property in doc.GetElementsByTagName("PROPERTY"))
            {
                var propName = property.Attributes?["NAME"]?.Value;
                var valueNode = property["VALUE"];
                if (valueNode == null) continue;
                if (string.Equals(propName, "Name", StringComparison.OrdinalIgnoreCase))
                    name = valueNode.InnerText;
                else if (string.Equals(propName, "Data", StringComparison.OrdinalIgnoreCase))
                    data = valueNode.InnerText;
            }
            return (name, data);
        }
        catch (XmlException)
        {
            // Malformed fragment — ignore; the poller treats it as pending.
            return (null, null);
        }
    }

    private static GuestCompletionStatus MapValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new GuestCompletionStatus(GuestCompletionSignal.Pending, null);

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "ready", StringComparison.OrdinalIgnoreCase))
            return new GuestCompletionStatus(GuestCompletionSignal.Ready, null);

        if (trimmed.StartsWith("failed", StringComparison.OrdinalIgnoreCase))
        {
            // Grammar is failed:<shortcode>; the shortcode is a safe diagnostic (no secrets).
            var colon = trimmed.IndexOf(':');
            var shortcode = colon >= 0 && colon + 1 < trimmed.Length
                ? trimmed.Substring(colon + 1).Trim()
                : "unspecified";
            return new GuestCompletionStatus(GuestCompletionSignal.Failed, shortcode);
        }

        // Unrecognized value — keep polling rather than mis-map.
        return new GuestCompletionStatus(GuestCompletionSignal.Pending, null);
    }
}
