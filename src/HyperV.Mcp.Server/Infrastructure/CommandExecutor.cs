using System.Text.Json;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Executes commands and scripts on guest VMs via PowerShell Direct.
/// See /myplans/execution/commands/commands-design.md — CMD-D1: Single commands via vm_run_command; scripts via vm_run_script.
///
/// Phase 2 refactor (issue #52, ST-4): All session lifecycle and Invoke-Command wrapping is now
/// delegated to <see cref="IPowerShellDirectChannel"/>. CommandExecutor only constructs the
/// inner user-script that runs inside the guest session and parses the JSON envelope it emits.
///
/// Design decisions:
/// - PSD-D1: Session-based execution — owned by the channel.
/// - CMD-D4: Timed-out / cancelled commands surface as <see cref="OperationCanceledException"/>
///   from the channel; CommandExecutor maps those to <see cref="CommandResult"/> with the
///   appropriate flag set and partial output (when available).
/// - CMD-D6: Session remains open after timeout/cancellation (channel responsibility).
/// - CMD-D3/EX-D3: Output truncation — 512KB stdout, 128KB stderr.
/// </summary>
public class CommandExecutor : ICommandExecutor
{
    private readonly IPowerShellDirectChannel _channel;
    private readonly IHostResolver _hostResolver;
    private readonly ILogger<CommandExecutor> _logger;

    /// <summary>
    /// Maximum stdout size in bytes before truncation.
    /// See /myplans/execution/commands/commands-design.md — CMD-D3/EX-D3.
    /// </summary>
    internal const int MaxStdoutBytes = 512 * 1024; // 512KB

    /// <summary>
    /// Maximum stderr size in bytes before truncation.
    /// See /myplans/execution/commands/commands-design.md — CMD-D3/EX-D3.
    /// </summary>
    internal const int MaxStderrBytes = 128 * 1024; // 128KB

    public CommandExecutor(
        IPowerShellDirectChannel channel,
        IHostResolver hostResolver,
        ILogger<CommandExecutor> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteCommandAsync(
        string hostId, string vmId, string command,
        string shell = "cmd", int timeoutSeconds = 30,
        string? username = null, string? password = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId, nameof(hostId));
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId, nameof(vmId));
        ArgumentException.ThrowIfNullOrWhiteSpace(command, nameof(command));

        // Issue 7: Validate hostId and enforce local-only for Phase 1.
        var profile = _hostResolver.ResolveRequired(hostId);
        if (!profile.IsLocal)
            throw new NotSupportedException(
                $"Remote host '{hostId}' is not supported for command execution in Phase 1.");

        // Issue #20: Resolve credentials for PowerShell Direct.
        var (resolvedUsername, resolvedPassword) = CredentialResolver.ResolveCredentials(username, password);

        // Issue 2: Validate vmId is a GUID and shell is allowed.
        var safeVmId = InputValidation.ValidateVmId(vmId);
        var safeShell = InputValidation.ValidateShell(shell);

        _logger.LogDebug(
            "Executing command on {HostId}:{VmId} via shell={Shell}, timeout={Timeout}s",
            hostId, safeVmId, safeShell, timeoutSeconds);

        var args = new Dictionary<string, object?>
        {
            ["cmd"] = command,
            ["sh"] = safeShell,
        };

        return await ExecuteAndParseAsync(
            hostId, safeVmId, resolvedUsername, resolvedPassword,
            CommandInnerScript, args, resolvedPassword, timeoutSeconds, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteScriptAsync(
        string hostId, string vmId, string script,
        string shell = "powershell", int timeoutSeconds = 60,
        string? username = null, string? password = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId, nameof(hostId));
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId, nameof(vmId));
        ArgumentException.ThrowIfNullOrWhiteSpace(script, nameof(script));

        // Issue 7: Validate hostId and enforce local-only for Phase 1.
        var profile = _hostResolver.ResolveRequired(hostId);
        if (!profile.IsLocal)
            throw new NotSupportedException(
                $"Remote host '{hostId}' is not supported for script execution in Phase 1.");

        // Issue #20: Resolve credentials for PowerShell Direct.
        var (resolvedUsername, resolvedPassword) = CredentialResolver.ResolveCredentials(username, password);

        // Issue 2: Validate vmId is a GUID and shell is allowed.
        var safeVmId = InputValidation.ValidateVmId(vmId);
        var safeShell = InputValidation.ValidateShell(shell);

        // Fix 2: Encode script as base64 to prevent here-string terminator injection.
        var base64Script = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));

        _logger.LogDebug(
            "Executing script on {HostId}:{VmId} via shell={Shell}, timeout={Timeout}s",
            hostId, safeVmId, safeShell, timeoutSeconds);

        var args = new Dictionary<string, object?>
        {
            ["base64Script"] = base64Script,
            ["sh"] = safeShell,
        };

        return await ExecuteAndParseAsync(
            hostId, safeVmId, resolvedUsername, resolvedPassword,
            ScriptInnerScript, args, resolvedPassword, timeoutSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Invokes the inner user-script through the channel and parses the resulting JSON envelope.
    /// Maps <see cref="OperationCanceledException"/> to a <see cref="CommandResult"/> with
    /// the appropriate timed-out / cancelled flag set per CMD-D4 / CMD-D6.
    /// </summary>
    private async Task<CommandResult> ExecuteAndParseAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        string innerScript,
        IDictionary<string, object?> args,
        string redactPassword,
        int timeoutSeconds,
        CancellationToken ct)
    {
        PowerShellHostResult hostResult;
        try
        {
            // Gate 6 Fix #2: pass timeoutSeconds verbatim to the channel (which forwards
            // it to PowerShellHost where it is enforced via a linked CTS + CancelAfter).
            hostResult = await _channel.InvokeScriptWithTimeoutAsync(
                hostId, vmId, username, password, innerScript, args, timeoutSeconds, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Gate 6 Fix #2: command-timeout signal from the host. Surface as TimedOut so
            // the dispatcher / ErrorMapper can map this to COMMAND_TIMEOUT (CMD-D4 / ADR-9).
            return new CommandResult
            {
                ExitCode = -1,
                Stdout = string.Empty,
                Stderr = string.Empty,
                TimedOut = true,
                Cancelled = false,
                Truncated = false,
                DurationMs = timeoutSeconds * 1000L,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CommandResult
            {
                ExitCode = -1,
                Stdout = string.Empty,
                Stderr = string.Empty,
                TimedOut = false,
                Cancelled = true,
                Truncated = false,
                DurationMs = 0,
            };
        }

        return ParseJsonResult(hostResult, hostId, vmId, redactPassword);
    }

    /// <summary>
    /// Parses the JSON output from the in-guest script into a <see cref="CommandResult"/>.
    /// Falls back to raw stderr if JSON parsing fails.
    /// </summary>
    internal CommandResult ParseJsonResult(PowerShellHostResult hostResult, string hostId, string vmId, string? redactPassword = null)
    {
        // The channel returns each pipeline object in Output. Our inner script emits a single
        // ConvertTo-Json -Compress string; join defensively in case the host splits/wraps it.
        var stdout = string.Join(
            "\n",
            hostResult.Output.Select(o => o?.ToString() ?? string.Empty)).Trim();

        var hostStderr = hostResult.Stderr ?? string.Empty;
        var hostExit = hostResult.ExitCode ?? (hostResult.Success ? 0 : 1);

        if (string.IsNullOrEmpty(stdout))
        {
            // No JSON output — likely an error inside the channel before the inner script ran.
            return new CommandResult
            {
                ExitCode = hostExit,
                Stdout = string.Empty,
                Stderr = CredentialResolver.RedactPassword(TruncateOutput(hostStderr, MaxStderrBytes), redactPassword ?? ""),
                TimedOut = false,
                Cancelled = false,
                Truncated = IsOverLimit(hostStderr, MaxStderrBytes),
                DurationMs = 0,
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var resultStdout = root.TryGetProperty("Stdout", out var stdoutProp)
                ? stdoutProp.GetString() ?? string.Empty
                : string.Empty;
            var resultStderr = root.TryGetProperty("Stderr", out var stderrProp)
                ? stderrProp.GetString() ?? string.Empty
                : string.Empty;
            var exitCode = root.TryGetProperty("ExitCode", out var exitProp)
                ? exitProp.GetInt32()
                : hostExit;
            var durationMs = root.TryGetProperty("DurationMs", out var durProp)
                ? durProp.GetInt64()
                : 0L;

            // Defense in depth: redact password from structured stderr too (channel already
            // redacts its own Stderr stream, but the inner script's $stderrLines is not touched).
            resultStderr = CredentialResolver.RedactPassword(resultStderr, redactPassword ?? "");
            var truncatedStdout = TruncateOutput(resultStdout, MaxStdoutBytes);
            var truncatedStderr = TruncateOutput(resultStderr, MaxStderrBytes);
            var isTruncated = IsOverLimit(resultStdout, MaxStdoutBytes) || IsOverLimit(resultStderr, MaxStderrBytes);

            return new CommandResult
            {
                ExitCode = exitCode,
                Stdout = truncatedStdout,
                Stderr = truncatedStderr,
                TimedOut = false,
                Cancelled = false,
                Truncated = isTruncated,
                DurationMs = durationMs,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse JSON result from command on {HostId}:{VmId}. Returning raw output.",
                hostId, vmId);

            // Fallback: return raw stdout/stderr from the host result.
            return new CommandResult
            {
                ExitCode = hostExit,
                Stdout = TruncateOutput(stdout, MaxStdoutBytes),
                Stderr = CredentialResolver.RedactPassword(TruncateOutput(hostStderr, MaxStderrBytes), redactPassword ?? ""),
                TimedOut = false,
                Cancelled = false,
                Truncated = IsOverLimit(stdout, MaxStdoutBytes) || IsOverLimit(hostStderr, MaxStderrBytes),
                DurationMs = 0,
            };
        }
    }

    /// <summary>
    /// Truncates output to the specified maximum byte count.
    /// Appends a truncation marker if the output was truncated.
    /// </summary>
    internal static string TruncateOutput(string output, int maxBytes)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output);
        if (bytes <= maxBytes)
            return output;

        var outputBytes = System.Text.Encoding.UTF8.GetBytes(output);
        var truncated = System.Text.Encoding.UTF8.GetString(outputBytes, 0, maxBytes);

        return truncated + "\n--- OUTPUT TRUNCATED ---";
    }

    /// <summary>
    /// Checks if the output exceeds the byte limit.
    /// </summary>
    internal static bool IsOverLimit(string output, int maxBytes)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        return System.Text.Encoding.UTF8.GetByteCount(output) > maxBytes;
    }

    /// <summary>
    /// Escapes single quotes in a string for safe embedding in PowerShell.
    /// PowerShell uses '' to escape a single quote inside single-quoted strings.
    /// Retained for use by other infrastructure that still composes inline scripts.
    /// </summary>
    internal static string EscapePowerShellString(string input)
    {
        return input.Replace("'", "''");
    }

    /// <summary>
    /// Inner user-script for <see cref="ExecuteCommandAsync"/>. Runs inside the guest PSSession
    /// (the channel wraps it in <c>Invoke-Command -Session $s -ScriptBlock { ... } -ArgumentList ...</c>).
    /// Emits a single compressed JSON envelope: <c>{ Stdout, Stderr, ExitCode, DurationMs }</c>.
    /// Args (positional, insertion order): <c>cmd</c>, <c>sh</c>.
    ///
    /// Issue #205 (VC-QP-D1, D3, D4, D5, D6): the <c>powershell</c> / <c>pwsh</c> / <c>default</c>
    /// arms now invoke the interpreter via a temp <c>hypervmcp-*.ps1</c> file + <c>-File</c>
    /// (with <c>-ExecutionPolicy Bypass</c>) to preserve literal <c>"</c> characters in the
    /// command body — <c>powershell.exe -Command &lt;string&gt;</c> argv-tokenizes the body and
    /// strips bare double quotes. The <c>cmd</c> arm is unchanged (it never had the bug).
    /// </summary>
    internal const string CommandInnerScript = @"
param($cmd, $sh)
$ErrorActionPreference = 'Continue'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$stdoutLines = @()
$stderrLines = @()
$exit = 0
try {
    switch ($sh) {
        'cmd'        { $output = & cmd.exe /c $cmd 2>&1 }
        'powershell' {
            $tempBase = [System.IO.Path]::GetTempFileName()
            $tempDir  = [System.IO.Path]::GetDirectoryName($tempBase)
            $tempName = [System.IO.Path]::GetFileNameWithoutExtension($tempBase)
            $tempPs1  = [System.IO.Path]::Combine($tempDir, 'hypervmcp-' + $tempName + '.ps1')
            try {
                Move-Item -LiteralPath $tempBase -Destination $tempPs1 -ErrorAction Stop
            } catch {
                Remove-Item -LiteralPath $tempBase -Force -ErrorAction SilentlyContinue
                $tempPs1 = [System.IO.Path]::Combine(
                    [System.IO.Path]::GetTempPath(),
                    ('hypervmcp-' + [System.Guid]::NewGuid().ToString('N') + '.ps1'))
            }
            try {
                [System.IO.File]::WriteAllText(
                    $tempPs1,
                    $cmd,
                    [System.Text.UTF8Encoding]::new($true))
                $output = powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1 2>&1
            } finally {
                Remove-Item -LiteralPath $tempPs1 -Force -ErrorAction SilentlyContinue
            }
        }
        'pwsh' {
            $tempBase = [System.IO.Path]::GetTempFileName()
            $tempDir  = [System.IO.Path]::GetDirectoryName($tempBase)
            $tempName = [System.IO.Path]::GetFileNameWithoutExtension($tempBase)
            $tempPs1  = [System.IO.Path]::Combine($tempDir, 'hypervmcp-' + $tempName + '.ps1')
            try {
                Move-Item -LiteralPath $tempBase -Destination $tempPs1 -ErrorAction Stop
            } catch {
                Remove-Item -LiteralPath $tempBase -Force -ErrorAction SilentlyContinue
                $tempPs1 = [System.IO.Path]::Combine(
                    [System.IO.Path]::GetTempPath(),
                    ('hypervmcp-' + [System.Guid]::NewGuid().ToString('N') + '.ps1'))
            }
            try {
                [System.IO.File]::WriteAllText(
                    $tempPs1,
                    $cmd,
                    [System.Text.UTF8Encoding]::new($true))
                $output = pwsh.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1 2>&1
            } finally {
                Remove-Item -LiteralPath $tempPs1 -Force -ErrorAction SilentlyContinue
            }
        }
        default {
            $tempBase = [System.IO.Path]::GetTempFileName()
            $tempDir  = [System.IO.Path]::GetDirectoryName($tempBase)
            $tempName = [System.IO.Path]::GetFileNameWithoutExtension($tempBase)
            $tempPs1  = [System.IO.Path]::Combine($tempDir, 'hypervmcp-' + $tempName + '.ps1')
            try {
                Move-Item -LiteralPath $tempBase -Destination $tempPs1 -ErrorAction Stop
            } catch {
                Remove-Item -LiteralPath $tempBase -Force -ErrorAction SilentlyContinue
                $tempPs1 = [System.IO.Path]::Combine(
                    [System.IO.Path]::GetTempPath(),
                    ('hypervmcp-' + [System.Guid]::NewGuid().ToString('N') + '.ps1'))
            }
            try {
                [System.IO.File]::WriteAllText(
                    $tempPs1,
                    $cmd,
                    [System.Text.UTF8Encoding]::new($true))
                $output = powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1 2>&1
            } finally {
                Remove-Item -LiteralPath $tempPs1 -Force -ErrorAction SilentlyContinue
            }
        }
    }
    foreach ($line in $output) {
        if ($line -is [System.Management.Automation.ErrorRecord]) {
            $stderrLines += $line.ToString()
        } else {
            $stdoutLines += $line.ToString()
        }
    }
    if ($LASTEXITCODE -ne $null) { $exit = $LASTEXITCODE }
} catch {
    $stderrLines += $_.Exception.Message
    $exit = 1
}
$sw.Stop()
[PSCustomObject]@{
    Stdout = ($stdoutLines -join ""`n"")
    Stderr = ($stderrLines -join ""`n"")
    ExitCode = $exit
    DurationMs = $sw.ElapsedMilliseconds
} | ConvertTo-Json -Compress
";

    /// <summary>
    /// Inner user-script for <see cref="ExecuteScriptAsync"/>. Decodes a base64 script body
    /// and executes it in the chosen shell. Same JSON envelope shape as
    /// <see cref="CommandInnerScript"/>.
    /// Args (positional, insertion order): <c>base64Script</c>, <c>sh</c>.
    ///
    /// Issue #205 (VC-QP-D1, D4, D5, D6): the <c>powershell</c> / <c>pwsh</c> / <c>default</c>
    /// arms write the (UTF-8 decoded) body to a temp <c>hypervmcp-*.ps1</c> file with a UTF-8
    /// BOM and invoke <c>powershell.exe -NoProfile -ExecutionPolicy Bypass -File &lt;path&gt;</c>.
    /// This preserves literal <c>"</c> characters that were previously stripped by the
    /// <c>-Command &lt;string&gt;</c> argv parser. Temp file is cleaned up in <c>finally</c>
    /// on both success and failure. The <c>cmd</c> arm is unchanged (already correct).
    /// </summary>
    internal const string ScriptInnerScript = @"
param($base64Script, $sh)
$ErrorActionPreference = 'Continue'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$stdoutLines = @()
$stderrLines = @()
$exit = 0
try {
    $bytes = [System.Convert]::FromBase64String($base64Script)
    $scriptText = [System.Text.Encoding]::UTF8.GetString($bytes)
    switch ($sh) {
        'cmd' {
            $tempFile = [System.IO.Path]::GetTempFileName() + '.cmd'
            [System.IO.File]::WriteAllText($tempFile, $scriptText)
            try {
                $output = & cmd.exe /c $tempFile 2>&1
            } finally {
                Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
            }
        }
        'powershell' {
            $tempBase = [System.IO.Path]::GetTempFileName()
            $tempDir  = [System.IO.Path]::GetDirectoryName($tempBase)
            $tempName = [System.IO.Path]::GetFileNameWithoutExtension($tempBase)
            $tempPs1  = [System.IO.Path]::Combine($tempDir, 'hypervmcp-' + $tempName + '.ps1')
            try {
                Move-Item -LiteralPath $tempBase -Destination $tempPs1 -ErrorAction Stop
            } catch {
                Remove-Item -LiteralPath $tempBase -Force -ErrorAction SilentlyContinue
                $tempPs1 = [System.IO.Path]::Combine(
                    [System.IO.Path]::GetTempPath(),
                    ('hypervmcp-' + [System.Guid]::NewGuid().ToString('N') + '.ps1'))
            }
            try {
                [System.IO.File]::WriteAllText(
                    $tempPs1,
                    $scriptText,
                    [System.Text.UTF8Encoding]::new($true))
                $output = powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1 2>&1
            } finally {
                Remove-Item -LiteralPath $tempPs1 -Force -ErrorAction SilentlyContinue
            }
        }
        'pwsh' {
            $tempBase = [System.IO.Path]::GetTempFileName()
            $tempDir  = [System.IO.Path]::GetDirectoryName($tempBase)
            $tempName = [System.IO.Path]::GetFileNameWithoutExtension($tempBase)
            $tempPs1  = [System.IO.Path]::Combine($tempDir, 'hypervmcp-' + $tempName + '.ps1')
            try {
                Move-Item -LiteralPath $tempBase -Destination $tempPs1 -ErrorAction Stop
            } catch {
                Remove-Item -LiteralPath $tempBase -Force -ErrorAction SilentlyContinue
                $tempPs1 = [System.IO.Path]::Combine(
                    [System.IO.Path]::GetTempPath(),
                    ('hypervmcp-' + [System.Guid]::NewGuid().ToString('N') + '.ps1'))
            }
            try {
                [System.IO.File]::WriteAllText(
                    $tempPs1,
                    $scriptText,
                    [System.Text.UTF8Encoding]::new($true))
                $output = pwsh.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1 2>&1
            } finally {
                Remove-Item -LiteralPath $tempPs1 -Force -ErrorAction SilentlyContinue
            }
        }
        default {
            $tempBase = [System.IO.Path]::GetTempFileName()
            $tempDir  = [System.IO.Path]::GetDirectoryName($tempBase)
            $tempName = [System.IO.Path]::GetFileNameWithoutExtension($tempBase)
            $tempPs1  = [System.IO.Path]::Combine($tempDir, 'hypervmcp-' + $tempName + '.ps1')
            try {
                Move-Item -LiteralPath $tempBase -Destination $tempPs1 -ErrorAction Stop
            } catch {
                Remove-Item -LiteralPath $tempBase -Force -ErrorAction SilentlyContinue
                $tempPs1 = [System.IO.Path]::Combine(
                    [System.IO.Path]::GetTempPath(),
                    ('hypervmcp-' + [System.Guid]::NewGuid().ToString('N') + '.ps1'))
            }
            try {
                [System.IO.File]::WriteAllText(
                    $tempPs1,
                    $scriptText,
                    [System.Text.UTF8Encoding]::new($true))
                $output = powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1 2>&1
            } finally {
                Remove-Item -LiteralPath $tempPs1 -Force -ErrorAction SilentlyContinue
            }
        }
    }
    foreach ($line in $output) {
        if ($line -is [System.Management.Automation.ErrorRecord]) {
            $stderrLines += $line.ToString()
        } else {
            $stdoutLines += $line.ToString()
        }
    }
    if ($LASTEXITCODE -ne $null) { $exit = $LASTEXITCODE }
} catch {
    $stderrLines += $_.Exception.Message
    $exit = 1
}
$sw.Stop()
[PSCustomObject]@{
    Stdout = ($stdoutLines -join ""`n"")
    Stderr = ($stderrLines -join ""`n"")
    ExitCode = $exit
    DurationMs = $sw.ElapsedMilliseconds
} | ConvertTo-Json -Compress
";
}
