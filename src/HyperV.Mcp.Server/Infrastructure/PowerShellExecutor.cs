using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Executes PowerShell scripts out-of-process via pwsh.exe (or powershell.exe fallback).
/// See /myplans/remoting/remoting-design.md — REM-D5: Out-of-process pwsh.exe execution model.
///
/// Design decisions:
/// - Scripts are written to a temp .ps1 file and executed via -File to avoid stdin
///   piping issues where multi-line scripts with Hyper-V cmdlets only partially execute.
///   (v7: switched from stdin/-Command - which produced empty stdout for multi-line scripts.)
/// - stdout and stderr are captured asynchronously to prevent deadlocks.
/// - Timeout enforcement kills the process; partial output is preserved.
/// - CancellationToken support allows callers to abort long-running operations.
/// - PowerShell executable is detected once at construction time.
/// - Temp files are cleaned up in a finally block after execution completes.
/// </summary>
public class PowerShellExecutor : IPowerShellExecutor
{
    private readonly string _psExecutable;
    private readonly ILogger<PowerShellExecutor> _logger;
    private readonly IEnvironment _environment;
    private readonly ITempPathProvider _tempPathProvider;

    /// <summary>
    /// Constructor used in production via DI. Both seams are required by the
    /// container; tests may use the overload below to inject fakes directly.
    /// See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md
    /// — TI-D3 (constructor injection of seams), TI-D4 (seams scoped to this executor only).
    /// </summary>
    public PowerShellExecutor(
        ILogger<PowerShellExecutor> logger,
        IEnvironment environment,
        ITempPathProvider tempPathProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _tempPathProvider = tempPathProvider ?? throw new ArgumentNullException(nameof(tempPathProvider));
        _psExecutable = DetectPowerShell();
        _logger.LogInformation("PowerShell executor initialized with: {Executable}", _psExecutable);
    }

    /// <summary>
    /// Back-compat constructor for callers (notably integration tests) that do not
    /// supply the new <see cref="IEnvironment"/> / <see cref="ITempPathProvider"/>
    /// seams. Falls back to the system-backed defaults so production behavior is
    /// unchanged. New code should prefer the three-argument constructor.
    /// </summary>
    public PowerShellExecutor(ILogger<PowerShellExecutor> logger)
        : this(logger, new SystemEnvironment(), new SystemTempPathProvider())
    {
    }

    /// <summary>
    /// The resolved PowerShell executable path. Exposed for testing/diagnostics.
    /// </summary>
    internal string ExecutablePath => _psExecutable;

    /// <inheritdoc />
    public async Task<PowerShellResult> ExecuteAsync(string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        if (string.IsNullOrWhiteSpace(script))
            throw new ArgumentException("Script cannot be null or empty.", nameof(script));

        // Fast-path: if the token is already cancelled, return immediately
        // without touching the file system or spawning a process.
        if (ct.IsCancellationRequested)
        {
            return new PowerShellResult
            {
                ExitCode = -1,
                Stdout = string.Empty,
                Stderr = string.Empty,
                TimedOut = false,
                Cancelled = true,
                DurationMs = 0,
            };
        }

        var sw = Stopwatch.StartNew();

        // ── Script-dump diagnostic (opt-in via env var) ──
        // See /myplans/operational/script-dump/script-dump-design.md §1 (Activation Contract) and §3 (Decision Flow).
        // Read env var per-call (SD-D2) so operators can toggle without restart.
        // allowDump=false short-circuits the entire dump path (SD-D4 — OS-install exclusion).
        // Resolution AND prepare share the same warning/degrade path: any failure
        // (path normalization throwing, CreateDirectory throwing) degrades to disabled.
        string? dumpDir = null;
        if (allowDump)
        {
            var rawDumpVar = _environment.GetEnvironmentVariable(DumpDirEnvVar);
            try
            {
                dumpDir = ResolveDumpDir(rawDumpVar);
                if (dumpDir != null)
                {
                    Directory.CreateDirectory(dumpDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[PS-DUMP] failed to prepare dump dir from env var value '{RawValue}': {Message}",
                    rawDumpVar, ex.Message);
                dumpDir = null;
            }
        }

        // Write script to a temp .ps1 file to avoid stdin piping issues.
        // Stdin piping with `-Command -` fails for multi-line scripts that use
        // Hyper-V cmdlets — stdout is empty and execution stops after Import-Module.
        var tempFile = Path.Combine(_tempPathProvider.GetTempPath(), $"hvmcp-{Guid.NewGuid():N}.ps1");
        bool dumpWritten = false;
        string? dumpPath = null;

        try
        {
            await File.WriteAllTextAsync(tempFile, script, Encoding.UTF8, ct);

            // ── Write the masked dump file (after temp written, before pwsh invoked) ──
            // §3 / §6: masker exception aborts the dump (no file written) but call continues.
            //          write exception logs Warning but call continues; temp file preserved.
            if (dumpDir != null)
            {
                string? masked = null;
                try
                {
                    masked = MaskCredentials(script);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[PS-DUMP] masker failed; skipping dump for this call: {Message}", ex.Message);
                }

                if (masked != null)
                {
                    var fileName = $"hvmcp-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.ps1";
                    var candidatePath = Path.Combine(dumpDir, fileName);
                    try
                    {
                        // UTF-8 without BOM (SD-D8) — matches the on-disk form pwsh consumed.
                        File.WriteAllText(candidatePath, masked, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        dumpWritten = true;
                        dumpPath = candidatePath;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[PS-DUMP] failed to write '{DumpPath}': {Message}", candidatePath, ex.Message);
                    }
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = _psExecutable,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempFile}\"",
                UseShellExecute = false,
                RedirectStandardInput = false,  // Not needed with -File
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Suppress ANSI/VT100 escape sequences in PowerShell output.
            // pwsh.exe (PS 7+) emits ANSI escape codes (0x1B) for progress bars and
            // colored output even in non-interactive mode, which corrupts JSON parsing
            // downstream (JsonDocument.Parse fails on 0x1B as first byte).
            // NO_COLOR is the cross-platform convention: https://no-color.org/
            // TERM=dumb tells terminal-aware programs to disable formatting.
            // See also: StripAnsiEscapeCodes() for defense-in-depth output sanitization.
            psi.Environment["NO_COLOR"] = "1";
            psi.Environment["TERM"] = "dumb";

            using var process = new Process { StartInfo = psi };

            try
            {
                process.Start();

                // No stdin writing needed — script is in the temp file.

                // Read stdout and stderr asynchronously to prevent deadlocks.
                // These tasks begin immediately and buffer internally.
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);

                // Build a linked cancellation source that fires on caller cancellation OR timeout.
                using var timeoutCts = timeoutSeconds > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                    : new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);

                    // Process exited normally — read remaining output.
                    var stdout = await stdoutTask;
                    var stderr = await stderrTask;

                    sw.Stop();
                    return new PowerShellResult
                    {
                        ExitCode = process.ExitCode,
                        Stdout = StripAnsiEscapeCodes(stdout),
                        Stderr = StripAnsiEscapeCodes(stderr),
                        TimedOut = false,
                        Cancelled = false,
                        DurationMs = sw.ElapsedMilliseconds,
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Caller cancelled via the provided CancellationToken.
                    KillProcess(process);
                    sw.Stop();

                    var stdout = await TryReadOutput(stdoutTask);
                    var stderr = await TryReadOutput(stderrTask);

                    _logger.LogWarning("PowerShell execution cancelled after {DurationMs}ms", sw.ElapsedMilliseconds);

                    return new PowerShellResult
                    {
                        ExitCode = -1,
                        Stdout = StripAnsiEscapeCodes(stdout),
                        Stderr = StripAnsiEscapeCodes(stderr),
                        TimedOut = false,
                        Cancelled = true,
                        DurationMs = sw.ElapsedMilliseconds,
                    };
                }
                catch (OperationCanceledException)
                {
                    // Timeout — the timeoutCts fired.
                    KillProcess(process);
                    sw.Stop();

                    var stdout = await TryReadOutput(stdoutTask);
                    var stderr = await TryReadOutput(stderrTask);

                    _logger.LogWarning(
                        "PowerShell execution timed out after {TimeoutSeconds}s (elapsed: {DurationMs}ms)",
                        timeoutSeconds, sw.ElapsedMilliseconds);

                    return new PowerShellResult
                    {
                        ExitCode = -1,
                        Stdout = StripAnsiEscapeCodes(stdout),
                        Stderr = StripAnsiEscapeCodes(stderr),
                        TimedOut = true,
                        Cancelled = false,
                        DurationMs = sw.ElapsedMilliseconds,
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                _logger.LogError(ex, "PowerShell execution failed after {DurationMs}ms", sw.ElapsedMilliseconds);

                return new PowerShellResult
                {
                    ExitCode = -1,
                    Stdout = string.Empty,
                    Stderr = ex.Message,
                    TimedOut = false,
                    Cancelled = false,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }
        }
        finally
        {
            // §8 Lifecycle: temp file is preserved when dumping is enabled (dumpDir != null
            // means prepare succeeded) so the operator can rerun the un-masked original,
            // even if the masker or write itself failed (operator opted in; manual rerun
            // is the fallback). When dumpDir == null (env var unset/disabled-value, OR
            // prepare-phase failure already nulled it), behave exactly as before: delete.
            bool preserveTempFile = dumpDir != null;
            if (!preserveTempFile)
            {
                try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
            }

            if (dumpWritten)
            {
                _logger.LogInformation(
                    "[PS-DUMP] dumped to '{DumpPath}' (temp='{TempPath}', durationMs={DurationMs})",
                    dumpPath, tempFile, sw.ElapsedMilliseconds);
            }
        }
    }

    // ─── Script-Dump Diagnostic ─────────────────────────────────────────
    // See /myplans/operational/script-dump/script-dump-design.md for full contract.

    /// <summary>
    /// Environment variable name that gates the script-dump diagnostic (SD-D1).
    /// When set to a directory path, every executed script is dumped (masked) to that dir
    /// unless the call passes <c>allowDump=false</c>.
    /// </summary>
    internal const string DumpDirEnvVar = "HYPERV_MCP_DUMP_PS_SCRIPTS";

    /// <summary>
    /// Trimmed (case-insensitive) values that explicitly disable the dump feature.
    /// See /myplans/operational/script-dump/script-dump-design.md §1 — Activation Contract.
    /// Treating these as path names would be a footgun (e.g., mkdir '0').
    /// </summary>
    internal static readonly HashSet<string> DisabledSentinels =
        new(StringComparer.OrdinalIgnoreCase) { "0", "false", "no", "off" };

    /// <summary>
    /// Resolve the dump directory from a raw env-var value.
    /// Returns null (= disabled) for null/whitespace/sentinel values; otherwise returns the
    /// fully-qualified absolute path (relative paths resolved against
    /// <see cref="Environment.CurrentDirectory"/>).
    /// See /myplans/operational/script-dump/script-dump-design.md §1.
    /// </summary>
    internal static string? ResolveDumpDir(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var trimmed = rawValue.Trim();

        if (DisabledSentinels.Contains(trimmed))
            return null;

        // Path.GetFullPath resolves relative paths against Environment.CurrentDirectory.
        return Path.GetFullPath(trimmed);
    }

    /// <summary>
    /// Pass 1 — structural replacement of the canonical credential block emitted by
    /// <see cref="CredentialResolver.BuildCredentialBlock(string,string)"/>:
    ///     $secPass = ConvertTo-SecureString '...' -AsPlainText -Force
    ///     $cred = New-Object ... PSCredential ... '...', $secPass
    ///
    /// PowerShell single-quoted string literal grammar: contents are any char except
    /// <c>'</c>, OR a doubled single quote <c>''</c> which represents a literal apostrophe.
    /// Backslash is **not** an escape character inside single-quoted strings, so the
    /// pattern <c>'(?:''|[^'])*'</c> correctly handles passwords containing or ending
    /// with backslash (e.g. <c>'P@ss\'</c>) as well as embedded apostrophes (<c>'a''b'</c>).
    /// See /myplans/operational/script-dump/script-dump-design.md §4 — Credential Masking Contract.
    /// </summary>
    private static readonly Regex CanonicalCredBlockRegex = new(
        @"\$secPass\s*=\s*ConvertTo-SecureString\s+'(?:''|[^'])*'\s+-AsPlainText\s+-Force\s*\r?\n\s*\$cred\s*=\s*New-Object\s+(?:-TypeName\s+)?[\w\.]*PSCredential[^\r\n]*",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Pass 2 — defense-in-depth sweep over any remaining inline
    /// <c>ConvertTo-SecureString '&lt;plaintext&gt;'</c> literal.
    /// Uses the same PowerShell single-quoted string grammar as Pass 1 (see notes above).
    /// See /myplans/operational/script-dump/script-dump-design.md §4.
    /// </summary>
    private static readonly Regex StraySecureStringRegex = new(
        @"ConvertTo-SecureString\s+'(?:''|[^'])*'",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Mask credentials in a generated PowerShell script body before writing it to the
    /// dump directory. Two-pass: structural replacement of the canonical
    /// <c>$secPass</c>/<c>$cred</c> block, then a defense-in-depth sweep of any stray
    /// <c>ConvertTo-SecureString '...'</c> literals.
    /// See /myplans/operational/script-dump/script-dump-design.md §4.
    /// </summary>
    internal static string MaskCredentials(string script)
    {
        if (string.IsNullOrEmpty(script))
            return script;

        // SD-D8: Preserve the dominant newline style of the input script so the dumped
        // file does not contain mixed line endings (review feedback PR #47).
        // CRLF wins iff the script contains any CRLF; otherwise default to LF.
        var newline = script.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var structuralReplacement =
            "# [HVMCP-DUMP] credential block redacted -- supply manually before rerun:" + newline +
            "$cred = Get-Credential -Message 'Re-enter credentials for manual rerun'";

        var pass1 = CanonicalCredBlockRegex.Replace(script, structuralReplacement);
        var pass2 = StraySecureStringRegex.Replace(pass1, "ConvertTo-SecureString '***REDACTED***'");
        return pass2;
    }

    /// <summary>
    /// Detect the PowerShell executable. Prefer pwsh.exe (PS 7+) if Hyper-V cmdlets
    /// actually work, fall back to powershell.exe (5.1).
    /// See /myplans/remoting/remoting-design.md — Assumption #1: pwsh.exe preferred when compatible.
    /// See /myplans/execution/execution-design.md — EX-D6: Async stderr/stdout handling in process probes.
    ///
    /// NOTE: pwsh (PS 7+) has a known bug where Get-VM throws "Value cannot be null"
    /// when spawned non-interactively, even after Import-Module Hyper-V succeeds.
    /// Checking module availability alone is insufficient — we must actually run Get-VM
    /// to confirm it works. powershell.exe (5.1) does not have this issue.
    /// </summary>
    private string DetectPowerShell()
    {
        // Phase 1: Test pwsh with actual Get-VM execution (not just module availability).
        // pwsh (PS 7+) has a known issue where Get-VM throws "Value cannot be null"
        // when spawned non-interactively, even though the Hyper-V module is available.
        try
        {
            var (exitCode, output) = RunProbe(
                "pwsh",
                "-NoProfile -NonInteractive -Command \"Import-Module Hyper-V -ErrorAction Stop; try { Get-VM -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -match 'cannot be null') { exit 2 } else { exit 1 } }; Write-Output 'HyperV-OK'\"",
                timeoutSeconds: 15);

            if (exitCode == 0 && output.Contains("HyperV-OK"))
            {
                _logger.LogInformation("pwsh.exe detected with working Hyper-V cmdlets.");
                return "pwsh";
            }

            if (exitCode == 2)
            {
                _logger.LogWarning(
                    "pwsh.exe has Hyper-V module but Get-VM throws 'Value cannot be null' " +
                    "(known PS7 non-interactive bug). Falling back to powershell.exe (5.1).");
            }
            else
            {
                _logger.LogWarning(
                    "pwsh.exe Hyper-V probe failed (exit code {ExitCode}). " +
                    "Falling back to powershell.exe (5.1).", exitCode);
            }
        }
        catch
        {
            // pwsh not available — fall through to fallback.
        }

        // Phase 2: Use powershell.exe (5.1) which includes Windows modules natively.
        _logger.LogInformation(
            "Using powershell.exe (5.1) for Hyper-V module compatibility.");
        return "powershell";
    }

    /// <summary>
    /// Run a probe process with async stdout/stderr reads and an overall timeout.
    /// Both streams are consumed concurrently to prevent deadlocks when the OS pipe
    /// buffer fills (~4KB). The timeout covers the entire operation (process start +
    /// stream reads + wait for exit), not just WaitForExit.
    /// See /myplans/execution/execution-design.md — EX-D6.
    /// </summary>
    private (int ExitCode, string Stdout) RunProbe(string fileName, string arguments, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout and stderr asynchronously to prevent deadlocks.
        // Both tasks MUST be started before awaiting either — this ensures
        // neither pipe buffer fills and blocks the process.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Use a CancellationTokenSource that covers the ENTIRE operation,
        // not just WaitForExit. This ensures the probe is bounded even if
        // the process hangs or the pipe reads block.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();

            // Process exited — drain remaining output.
            var stdout = TryReadOutput(stdoutTask).GetAwaiter().GetResult();
            _ = TryReadOutput(stderrTask).GetAwaiter().GetResult();

            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            // Timeout — kill the process and drain partial output.
            KillProcess(process);

            var stdout = TryReadOutput(stdoutTask).GetAwaiter().GetResult();
            _ = TryReadOutput(stderrTask).GetAwaiter().GetResult();

            _logger.LogWarning(
                "Probe process {FileName} timed out after {TimeoutSeconds}s",
                fileName, timeoutSeconds);

            return (-1, stdout);
        }
    }

    /// <summary>
    /// Kill the process and its entire process tree.
    /// </summary>
    private void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill PowerShell process {ProcessId}", process.Id);
        }
    }

    /// <summary>
    /// Regex that matches ANSI/VT100 escape sequences (ESC [ ... letter).
    /// Covers CSI sequences (colors, cursor movement, progress bars) and
    /// OSC sequences (title setting). This is the primary defense against
    /// pwsh.exe (PS 7+) emitting ANSI codes in non-interactive mode, which
    /// corrupts JSON output that downstream callers parse with JsonDocument.
    /// </summary>
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B(?:\[[0-9;]*[A-Za-z]|\].*?(?:\x07|\x1B\\))",
        RegexOptions.Compiled);

    /// <summary>
    /// Strips ANSI/VT100 escape sequences from a string.
    /// PowerShell 7+ (pwsh.exe) can emit ANSI escape codes for progress bars,
    /// colored output, and terminal formatting even in non-interactive mode.
    /// These codes start with ESC (0x1B) and corrupt JSON parsing downstream.
    /// This method provides defense-in-depth beyond the NO_COLOR/TERM=dumb
    /// environment variables set on the process.
    /// </summary>
    internal static string StripAnsiEscapeCodes(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Fast path: no ESC character means no ANSI sequences to strip.
        if (!input.Contains('\x1B'))
            return input;

        return AnsiEscapeRegex.Replace(input, string.Empty);
    }

    /// <summary>
    /// Try to read output from a potentially cancelled read task.
    /// Returns whatever was captured, or empty string on failure.
    /// </summary>
    private static async Task<string> TryReadOutput(Task<string> readTask)
    {
        try
        {
            if (readTask.IsCompletedSuccessfully)
                return readTask.Result;

            // Give a short window to complete after process kill.
            var completed = await Task.WhenAny(readTask, Task.Delay(1000));
            if (completed == readTask)
                return await readTask;

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
