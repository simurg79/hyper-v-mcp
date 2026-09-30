namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Abstraction for executing PowerShell scripts out-of-process.
/// See internal documentation — REM-D5: Out-of-process pwsh.exe model.
/// 
/// This is the lowest-level execution interface — it launches a PowerShell
/// process, sends a script, and captures the output. Higher-level components
/// (HyperVManager, CommandExecutor, etc.) compose scripts and call this.
/// </summary>
public interface IPowerShellExecutor
{
    /// <summary>
    /// Execute a PowerShell script and return the result.
    /// </summary>
    /// <param name="script">The PowerShell script to execute.</param>
    /// <param name="timeoutSeconds">Maximum execution time in seconds. 0 = no timeout.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="allowDump">
    /// When false, the script-dump diagnostic (gated by the
    /// <c>HYPERV_MCP_DUMP_PS_SCRIPTS</c> env var) is short-circuited for this call.
    /// Used by callers that emit credentials in a shape the v1 masker cannot redact
    /// (e.g., the OS-install path with variable-backed credentials and unattended-XML
    /// <c>&lt;Password&gt;</c> nodes). Default is <c>true</c> — dumping behavior follows
    /// the env var only.
    /// See internal documentation — Decision SD-D4.
    /// </param>
    /// <returns>The execution result with stdout, stderr, exit code, and timing.</returns>
    Task<PowerShellResult> ExecuteAsync(string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true);

    /// <summary>
    /// Execute a script whose secret inputs are supplied out-of-band as child-process environment
    /// variables instead of being interpolated into the script text.
    ///
    /// <para>Needed because <see cref="ExecuteAsync"/> materializes the whole script in a host temp
    /// <c>.ps1</c> file, so a secret in the script text lands on host disk and survives a crash.
    /// See internal documentation — FR-14 / FR-16.</para>
    ///
    /// <para>Script dumping is always suppressed here. The default implementation throws
    /// deliberately: an earlier silent forward to <see cref="ExecuteAsync"/> discarded the secret
    /// values, so a double implementing only <c>ExecuteAsync</c> could report success without any
    /// secret reaching the child — the masked-mock failure mode that let a defective fix through on
    /// PR #278. Every implementation and fake on the secret path MUST opt in explicitly.</para>
    /// </summary>
    /// <param name="script">The script; it reads each secret via <c>$env:NAME</c>.</param>
    /// <param name="secretEnvironment">Environment variable name/value pairs for the child process.</param>
    /// <param name="timeoutSeconds">Maximum execution time in seconds. 0 = no timeout.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PowerShellResult> ExecuteWithSecretsAsync(
        string script,
        IReadOnlyDictionary<string, string> secretEnvironment,
        int timeoutSeconds = 300,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement ExecuteWithSecretsAsync; secret-bearing execution must be implemented explicitly.");
}

/// <summary>
/// Result of a PowerShell script execution.
/// </summary>
public class PowerShellResult
{
    /// <summary>Process exit code. 0 = success.</summary>
    public int ExitCode { get; init; }

    /// <summary>Standard output from the script.</summary>
    public string Stdout { get; init; } = string.Empty;

    /// <summary>Standard error output from the script.</summary>
    public string Stderr { get; init; } = string.Empty;

    /// <summary>Whether the script was killed due to timeout.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Whether the script was cancelled via CancellationToken.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Total execution time in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>True if the script completed successfully (exit code 0, no timeout, no cancellation).</summary>
    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;
}
