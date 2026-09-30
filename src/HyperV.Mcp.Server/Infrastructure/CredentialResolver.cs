namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Resolves VM credentials from tool parameters or environment variables.
/// See internal documentation — Phase 1: Minimal Credential Resolution.
/// See GitHub Issue #20.
///
/// Resolution order:
/// 1. Tool parameters (username/password) — highest priority.
/// 2. Environment variables (HYPERV_MCP_VM_USERNAME / HYPERV_MCP_VM_PASSWORD).
/// 3. Throw <see cref="MissingCredentialsException"/> if either is missing.
///
/// Security: Credential values are never logged. The generated PowerShell script
/// containing credentials must never be logged either.
/// </summary>
public static class CredentialResolver
{
    /// <summary>
    /// Environment variable name for the VM username.
    /// </summary>
    internal const string EnvVarUsername = "HYPERV_MCP_VM_USERNAME";

    /// <summary>
    /// Environment variable name for the VM password.
    /// </summary>
    internal const string EnvVarPassword = "HYPERV_MCP_VM_PASSWORD";

    /// <summary>
    /// Fixed-width replacement for every redacted representation. Its width is independent of
    /// the password length, which is what keeps redacted output length-invariant (FR-9 item 5).
    /// </summary>
    internal const string RedactionMarker = "***REDACTED***";

    /// <summary>
    /// Resolves credentials from tool parameters or environment variables.
    /// Both username AND password must be resolved; if either is missing, throws.
    /// </summary>
    /// <param name="username">Optional username from tool parameters.</param>
    /// <param name="password">Optional password from tool parameters.</param>
    /// <returns>A tuple of (username, password).</returns>
    /// <exception cref="MissingCredentialsException">
    /// Thrown when credentials cannot be resolved from any source.
    /// </exception>
    public static (string username, string password) ResolveCredentials(string? username, string? password)
    {
        var resolvedUsername = !string.IsNullOrWhiteSpace(username)
            ? username
            : Environment.GetEnvironmentVariable(EnvVarUsername);

        var resolvedPassword = !string.IsNullOrWhiteSpace(password)
            ? password
            : Environment.GetEnvironmentVariable(EnvVarPassword);

        if (string.IsNullOrWhiteSpace(resolvedUsername) || string.IsNullOrWhiteSpace(resolvedPassword))
        {
            throw new MissingCredentialsException();
        }

        return (resolvedUsername, resolvedPassword);
    }

    /// <summary>
    /// Builds the PowerShell credential block that creates a PSCredential object.
    /// The returned string should be prepended to PowerShell scripts that need
    /// credentials for New-PSSession -VMId -Credential $cred.
    ///
    /// Security: Uses <see cref="InputValidation.EscapePowerShellString"/> for both
    /// username and password to prevent PowerShell injection via single-quote escaping.
    /// The output of this method must NEVER be logged.
    /// </summary>
    /// <param name="username">The resolved username.</param>
    /// <param name="password">The resolved password.</param>
    /// <returns>A PowerShell script block that creates $cred variable.</returns>
    public static string BuildCredentialBlock(string username, string password)
    {
        var escapedUsername = InputValidation.EscapePowerShellString(username);
        var escapedPassword = InputValidation.EscapePowerShellString(password);

        return $@"$secPass = ConvertTo-SecureString '{escapedPassword}' -AsPlainText -Force
$cred = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList '{escapedUsername}', $secPass";
    }

    /// <summary>
    /// Redacts the literal password from text to prevent credential leakage in error messages.
    /// This is a defense-in-depth measure applied at the service layer before any error text
    /// leaves the service boundary.
    /// See GitHub Issue #20 — Credential redaction on live error paths.
    /// </summary>
    /// <param name="text">The text that may contain the password.</param>
    /// <param name="password">The password to redact.</param>
    /// <returns>The text with the password replaced by ***REDACTED***.</returns>
    public static string RedactPassword(string text, string password)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(password))
            return text;

        return text.Replace(password, RedactionMarker);
    }

    /// <summary>
    /// Removes every prohibited representation of <paramref name="password"/> — literal,
    /// reversed, Base64, and URL-encoded. MUST run before the text is logged, spilled, or
    /// embedded in an exception: the downstream passes match script-shaped patterns only
    /// and cannot recognize an encoded secret.
    /// See internal documentation — GCR-D4.
    /// </summary>
    /// <param name="text">The text that may contain a password representation.</param>
    /// <param name="password">The password whose representations must be removed.</param>
    /// <returns>The text with every prohibited representation replaced.</returns>
    public static string RedactPasswordRepresentations(string text, string password)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(password))
            return text;

        var result = text;
        foreach (var representation in ProhibitedRepresentations(password))
        {
            if (!string.IsNullOrEmpty(representation))
            {
                result = result.Replace(representation, RedactionMarker, StringComparison.Ordinal);
            }
        }
        return result;
    }

    /// <summary>
    /// The FR-9 prohibited renderings of <paramref name="password"/>. Literal first, so a
    /// rendering identical to it (e.g. URL-encoding an alphanumeric password) is already gone.
    /// </summary>
    internal static IEnumerable<string> ProhibitedRepresentations(string password)
    {
        yield return password;
        yield return new string(password.Reverse().ToArray());
        yield return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password));
        yield return Uri.EscapeDataString(password);
    }
}
