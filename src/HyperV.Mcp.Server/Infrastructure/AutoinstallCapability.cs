namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Whether Ubuntu installation media can install unattended, judged from its GRUB configuration.
/// Tri-state so "token provably absent" stays distinct from "nothing could be judged"; both refuse,
/// but they must not claim the same thing to the caller.
/// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
/// — UMD-D4.
/// </summary>
public enum AutoinstallCapability
{
    /// <summary>A kernel-load line carries the bare <c>autoinstall</c> argument.</summary>
    Capable,

    /// <summary>Kernel-load lines exist and none carries the argument.</summary>
    NotCapable,

    /// <summary>No GRUB configuration, or none carrying a kernel-load line, so nothing was judged.</summary>
    Undeterminable,
}

/// <summary>
/// Decides <see cref="AutoinstallCapability"/> from raw <c>grub.cfg</c> text, mirroring
/// <c>verify_grub_cfg</c> / <c>has_all_tokens</c> in scripts/prepare-ubuntu-autoinstall-iso.py.
/// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
/// — UMD-D3, UMD-D4, UMD-D5.
/// </summary>
public static class AutoinstallCapabilityEvaluator
{
    private const string AutoinstallToken = "autoinstall";

    /// <summary>Arguments after this separator are handed to the booted system, not the installer.</summary>
    private const string ArgumentRegionSeparator = "---";

    private static readonly string[] KernelLoadCommands = { "linux", "linux16", "linuxefi" };

    // '\r' is a separator so CRLF-terminated media does not yield an 'autoinstall\r' argument that
    // fails equality.
    private static readonly char[] ArgumentSeparators = { ' ', '\t', '\r' };

    public const string PreparationHelperPath = "scripts/prepare-ubuntu-autoinstall-iso.py";

    /// <summary>
    /// Media is capable iff some kernel-load line carries a whitespace-delimited argument
    /// STRING-EQUAL to <c>autoinstall</c>. Equality — never <c>Contains</c>, never
    /// <see cref="TokenMatcher"/>, never a <c>key=value</c> form — is the whole defence against the
    /// issue #289 substring bug class, so <c>noautoinstall</c>, <c>autoinstall=1</c> and
    /// <c>foo=autoinstall</c> all fail. Requiring it on EVERY line would be wrong: non-installer
    /// entries such as the memtest <c>linux16</c> line legitimately lack it.
    /// </summary>
    public static AutoinstallCapability Evaluate(string? grubConfiguration)
    {
        if (string.IsNullOrWhiteSpace(grubConfiguration))
        {
            return AutoinstallCapability.Undeterminable;
        }

        var sawKernelLoadLine = false;

        foreach (var rawLine in grubConfiguration.Split('\n'))
        {
            var words = rawLine.Split(ArgumentSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }

            // A GRUB comment is not a kernel line, so 'autoinstall' written in a comment or a menu
            // title never reaches the argument scan below.
            if (!IsKernelLoadCommand(words[0]))
            {
                continue;
            }

            sawKernelLoadLine = true;

            // words[1] is the kernel image path; arguments follow it.
            for (var iter = 2; iter < words.Length; iter++)
            {
                var argument = words[iter].Trim();
                if (argument == ArgumentRegionSeparator)
                {
                    break;
                }
                if (string.Equals(argument, AutoinstallToken, StringComparison.Ordinal))
                {
                    return AutoinstallCapability.Capable;
                }
            }
        }

        return sawKernelLoadLine ? AutoinstallCapability.NotCapable : AutoinstallCapability.Undeterminable;
    }

    private static bool IsKernelLoadCommand(string word)
    {
        foreach (var command in KernelLoadCommands)
        {
            if (string.Equals(word, command, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The mandated three-part refusal text: what is wrong, which ISO, and the remedy. The
    /// Undeterminable wording never asserts the configuration is absent — only that it could not be
    /// confirmed — so the error claims no more than was observed.
    /// See myplans/vm-management/iso-installation/ubuntu-media-capability-and-timeout-diagnostics-design.md
    /// — UMD-D4, UMD-D5.
    /// </summary>
    public static string BuildRefusalMessage(AutoinstallCapability capability, string isoPath)
    {
        var problem = capability == AutoinstallCapability.Undeterminable
            ? "the required autoinstall boot configuration could not be confirmed on the supplied " +
              "Ubuntu installation media"
            : "the supplied Ubuntu installation media lacks the required autoinstall boot configuration";

        return $"Ubuntu Server 24.04 cannot be installed unattended because {problem}. " +
               $"Supplied ISO: '{isoPath}'. " +
               $"Prepare the media with the project's Ubuntu autoinstall preparation helper " +
               $"({PreparationHelperPath}) and pass its output ISO instead.";
    }
}
