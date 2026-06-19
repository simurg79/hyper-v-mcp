using System.Text.RegularExpressions;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Centralized input validation and sanitization for values used in PowerShell scripts.
/// Prevents injection attacks by validating format and content of all user-supplied values
/// before they are interpolated into PowerShell script strings.
///
/// Security note: All values that are interpolated into PowerShell scripts MUST pass through
/// validation here. Single-quote escaping alone is NOT sufficient — structured values like
/// GUIDs and shell names must be validated for format correctness first.
///
/// See /myplans/security/security-design.md — Input Validation.
/// </summary>
public static class InputValidation
{
    /// <summary>
    /// Maximum allowed length for a VM name. NTFS limits individual path components to 255 characters.
    /// Since VM names are used as directory names and VHDX filenames, they must stay within this limit.
    /// See /myplans/security/security-design.md — SEC-D7: VM name path traversal validation.
    /// </summary>
    private const int MaxVmNameLength = 255;

    /// <summary>
    /// Characters that are illegal in Windows filenames (NTFS) or dangerous in PowerShell contexts.
    /// Path separators (/ and \) are checked separately since they have distinct security implications.
    /// Square brackets [ ] are included because PowerShell treats them as wildcard patterns
    /// in cmdlets like Get-VM -Name, which could match unintended VMs during uniqueness checks,
    /// monitoring, rollback, or final lookup. A crafted name like "[a-z]*" could match wrong VMs.
    /// See /myplans/security/security-design.md — SEC-D7: VM name path traversal validation.
    /// </summary>
    private static readonly char[] IllegalWindowsFileNameChars = { '<', '>', ':', '"', '|', '?', '*', '[', ']' };

    /// <summary>
    /// Windows reserved device names that cannot be used as file or directory names.
    /// These are matched case-insensitively, and also match when followed by a dot extension
    /// (e.g., "CON.vhdx" is also reserved).
    /// See /myplans/security/security-design.md — SEC-D7: VM name path traversal validation.
    /// </summary>
    private static readonly Regex ReservedWindowsNames = new(
        @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..+)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Allowed shell values for command execution. Only these shells are permitted
    /// to prevent injection via the shell parameter.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1: Supported shells.
    /// </summary>
    private static readonly HashSet<string> AllowedShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh"
    };

    /// <summary>
    /// Validates that a vmId is a well-formed GUID and returns the canonical string form.
    /// This prevents PowerShell injection via vmId parameters, since GUIDs are safe for
    /// interpolation in PowerShell scripts.
    /// </summary>
    /// <param name="vmId">The VM identifier to validate.</param>
    /// <returns>The canonical GUID string (lowercase, hyphenated, 36 chars).</returns>
    /// <exception cref="ArgumentException">Thrown when vmId is not a valid GUID.</exception>
    public static string ValidateVmId(string vmId)
    {
        if (string.IsNullOrWhiteSpace(vmId))
            throw new ArgumentException("vmId must not be null or empty.", nameof(vmId));

        if (!Guid.TryParse(vmId, out var guid))
            throw new ArgumentException(
                $"vmId must be a valid GUID, got: '{vmId}'", nameof(vmId));

        // Return canonical format "D" (00000000-0000-0000-0000-000000000000)
        // which is safe for direct interpolation in PowerShell scripts.
        return guid.ToString("D");
    }

    /// <summary>
    /// Validates that a shell parameter is one of the allowed values.
    /// Prevents injection by ensuring only known-safe shell executables are used.
    /// </summary>
    /// <param name="shell">The shell name to validate.</param>
    /// <returns>The validated shell name (lowercase).</returns>
    /// <exception cref="ArgumentException">Thrown when shell is not in the allowed set.</exception>
    public static string ValidateShell(string shell)
    {
        if (string.IsNullOrWhiteSpace(shell))
            throw new ArgumentException("shell must not be null or empty.", nameof(shell));

        if (!AllowedShells.Contains(shell))
            throw new ArgumentException(
                $"Invalid shell '{shell}'. Allowed values: cmd, powershell, pwsh", nameof(shell));

        return shell.ToLowerInvariant();
    }

    /// <summary>
    /// Validates that a VM name is safe for use in filesystem paths.
    /// Prevents path traversal attacks by rejecting names containing path separators,
    /// parent directory references, control characters, and Windows-reserved names.
    ///
    /// Security context: VM names are used in <c>Join-Path $storageRoot $name</c> to construct
    /// host filesystem paths for differencing VHDX and VM directories. Without validation,
    /// a crafted name like "../../evil" can escape the storage root and create files in
    /// arbitrary host-side locations.
    ///
    /// See /myplans/security/security-design.md — SEC-D7: VM name path traversal validation.
    /// See /myplans/vm-management/vm-management-design.md — VMM-D3: Differencing VHDX for all VM creation.
    /// </summary>
    /// <param name="name">The VM name to validate.</param>
    /// <returns>The validated VM name (unchanged) on success.</returns>
    /// <exception cref="ArgumentException">Thrown when the name is invalid or unsafe.</exception>
    public static string ValidateVmName(string? name)
    {
        // Reject null, empty, or whitespace-only names.
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("VM name must not be null, empty, or whitespace.", nameof(name));

        // Reject names with leading or trailing whitespace.
        // Whitespace can cause inconsistencies between Hyper-V lookups (trimmed) and directory names.
        if (name != name.Trim())
            throw new ArgumentException(
                "VM name must not have leading or trailing whitespace.", nameof(name));

        // Reject names exceeding NTFS path component limit.
        if (name.Length > MaxVmNameLength)
            throw new ArgumentException(
                $"VM name must not exceed {MaxVmNameLength} characters, got: {name.Length}.", nameof(name));

        // Reject names containing path separators (forward slash or backslash).
        // These form path components in Join-Path and enable directory traversal.
        if (name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException(
                "VM name must not contain path separators (/ or \\).", nameof(name));

        // Reject names containing ".." (parent directory traversal).
        // This sequence has special meaning in filesystem paths even without separators.
        if (name.Contains(".."))
            throw new ArgumentException(
                "VM name must not contain '..' (parent directory traversal).", nameof(name));

        // Reject names containing non-printable-ASCII characters.
        // This includes null bytes (0x00), control characters (0x01–0x1F), DEL (0x7F),
        // and all characters above 0x7E. Null bytes can truncate strings in C-based APIs;
        // control chars are invalid in NTFS filenames; non-ASCII characters avoid encoding
        // ambiguities and potential filesystem normalization attacks.
        foreach (var c in name)
        {
            if (c < 0x20 || c > 0x7E) // Only printable ASCII (0x20–0x7E) allowed
                throw new ArgumentException(
                    "VM name must only contain printable ASCII characters.", nameof(name));
        }

        // Reject names containing characters illegal in Windows filenames or dangerous in PowerShell.
        if (name.IndexOfAny(IllegalWindowsFileNameChars) >= 0)
            throw new ArgumentException(
                "VM name must not contain illegal Windows filename characters (< > : \" | ? * [ ]).", nameof(name));

        // Reject names ending with a dot or space (NTFS silently strips these).
        if (name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException(
                "VM name must not end with a dot or space (NTFS strips trailing dots/spaces).", nameof(name));

        // Reject Windows reserved device names (with or without extension).
        if (ReservedWindowsNames.IsMatch(name))
            throw new ArgumentException(
                $"VM name must not be a Windows reserved name: '{name}'.", nameof(name));

        return name;
    }

    /// <summary>
    /// Escapes single quotes in a string for safe embedding in PowerShell single-quoted strings.
    /// PowerShell uses '' to escape a single quote inside single-quoted strings.
    /// </summary>
    /// <param name="value">The string to escape.</param>
    /// <returns>The escaped string safe for embedding in single-quoted PowerShell strings.</returns>
    public static string EscapePowerShellString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Replace("'", "''");
    }

    /// <summary>
    /// Validates that an ISO path looks like a valid ISO file path.
    /// Checks for .iso extension and rejects path traversal attempts.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — Security Considerations.
    /// </summary>
    /// <param name="path">The ISO file path to validate.</param>
    /// <returns>The validated path (unchanged) on success.</returns>
    /// <exception cref="ArgumentException">Thrown when the path is invalid or unsafe.</exception>
    public static string ValidateIsoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("ISO path must not be null or empty.", nameof(path));

        // Reject path traversal
        if (path.Contains(".."))
            throw new ArgumentException(
                "ISO path must not contain '..' (path traversal).", nameof(path));

        // Must end with .iso (case-insensitive)
        if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "ISO path must end with '.iso' extension.", nameof(path));

        return path;
    }

    /// <summary>
    /// Validates that an administrator password meets minimum requirements.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — Security Considerations.
    /// </summary>
    /// <param name="password">The admin password to validate.</param>
    /// <returns>The validated password (unchanged) on success.</returns>
    /// <exception cref="ArgumentException">Thrown when the password is invalid.</exception>
    public static string ValidateAdminPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Admin password must not be null or empty.", nameof(password));

        if (password.Length < 8)
            throw new ArgumentException(
                "Admin password must be at least 8 characters long.", nameof(password));

        return password;
    }
}
