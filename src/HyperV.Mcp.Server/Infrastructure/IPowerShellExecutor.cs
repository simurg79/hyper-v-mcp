namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Abstraction for executing PowerShell scripts out-of-process.
/// See /myplans/remoting/remoting-design.md — REM-D5: Out-of-process pwsh.exe model.
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
    /// See /myplans/operational/script-dump/script-dump-design.md — Decision SD-D4.
    /// </param>
    /// <returns>The execution result with stdout, stderr, exit code, and timing.</returns>
    Task<PowerShellResult> ExecuteAsync(string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true);
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
