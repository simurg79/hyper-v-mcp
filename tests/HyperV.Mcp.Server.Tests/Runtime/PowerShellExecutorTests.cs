using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Integration tests for <see cref="PowerShellExecutor"/>.
/// See /myplans/remoting/remoting-design.md — REM-D5: Out-of-process pwsh.exe execution model.
///
/// These tests exercise the REAL PowerShellExecutor (not mocked) since they
/// verify actual process execution behavior. They require PowerShell (pwsh or
/// powershell.exe) to be installed on the test machine.
///
/// Test coverage:
/// - Simple script execution with stdout capture
/// - Exit code propagation
/// - Stderr capture
/// - Timeout enforcement (process killed, partial output preserved)
/// - Empty/null script argument validation
/// - CancellationToken support
/// </summary>
[Trait("Category", "Integration")]
[Collection("ScriptDumpDiagnostic")]
public class PowerShellExecutorTests
{
    private readonly PowerShellExecutor _executor;

    public PowerShellExecutorTests()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>();
        _executor = new PowerShellExecutor(logger);
    }

    // ─── Simple Execution ──────────────────────────────────────────────

    /// <summary>
    /// Execute a simple Write-Output script and verify stdout contains the expected text.
    /// This is the most basic smoke test for the executor.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SimpleWriteOutput_CapturesStdout()
    {
        var result = await _executor.ExecuteAsync("Write-Output 'hello'");

        result.Should().NotBeNull();
        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().Be("hello");
        result.Stderr.Should().BeEmpty();
        result.TimedOut.Should().BeFalse();
        result.Cancelled.Should().BeFalse();
        result.Success.Should().BeTrue();
        result.DurationMs.Should().BeGreaterThan(0);
    }

    // ─── Exit Code ─────────────────────────────────────────────────────

    /// <summary>
    /// Execute a script that exits with a non-zero code and verify the exit code
    /// is captured correctly. This tests error propagation from PowerShell.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_CapturesExitCode()
    {
        var result = await _executor.ExecuteAsync("exit 42");

        result.Should().NotBeNull();
        result.ExitCode.Should().Be(42);
        result.Success.Should().BeFalse("exit code 42 should not be success");
        result.TimedOut.Should().BeFalse();
        result.Cancelled.Should().BeFalse();
    }

    // ─── Stderr ────────────────────────────────────────────────────────

    /// <summary>
    /// Execute a script that writes to stderr and verify the error output is captured.
    /// Write-Error in PowerShell writes to stderr.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WriteError_CapturesStderr()
    {
        // Use $ErrorActionPreference = 'Continue' to ensure script continues
        // after Write-Error and exits normally.
        var result = await _executor.ExecuteAsync(
            "$ErrorActionPreference = 'Continue'; Write-Error 'test error'; exit 0");

        result.Should().NotBeNull();
        result.Stderr.Should().Contain("test error",
            "stderr should contain the error message from Write-Error");
    }

    // ─── Timeout ───────────────────────────────────────────────────────

    /// <summary>
    /// Execute a long-running script with a short timeout and verify the process
    /// is killed and TimedOut is set. This validates the timeout enforcement path
    /// described in REM-D5.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Timeout_KillsProcessAndSetsTimedOut()
    {
        var result = await _executor.ExecuteAsync(
            "Start-Sleep -Seconds 60", timeoutSeconds: 2);

        result.Should().NotBeNull();
        result.TimedOut.Should().BeTrue("process should be killed on timeout");
        result.ExitCode.Should().Be(-1, "timed-out process should report exit code -1");
        result.Success.Should().BeFalse();
        result.Cancelled.Should().BeFalse();
        result.DurationMs.Should().BeGreaterThan(0);
    }

    // ─── Argument Validation ───────────────────────────────────────────

    /// <summary>
    /// Passing a null script must throw ArgumentException.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullScript_ThrowsArgumentException()
    {
        Func<Task> act = async () => await _executor.ExecuteAsync(null!);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("script");
    }

    /// <summary>
    /// Passing an empty script must throw ArgumentException.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EmptyScript_ThrowsArgumentException()
    {
        Func<Task> act = async () => await _executor.ExecuteAsync("");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("script");
    }

    /// <summary>
    /// Passing a whitespace-only script must throw ArgumentException.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhitespaceScript_ThrowsArgumentException()
    {
        Func<Task> act = async () => await _executor.ExecuteAsync("   ");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("script");
    }

    // ─── Cancellation ──────────────────────────────────────────────────

    /// <summary>
    /// Execute a long-running script with an already-cancelled token.
    /// The executor should detect cancellation and return Cancelled = true.
    /// This validates the CancellationToken support path described in REM-D5.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PreCancelledToken_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _executor.ExecuteAsync(
            "Start-Sleep -Seconds 60", timeoutSeconds: 30, ct: cts.Token);

        result.Should().NotBeNull();
        result.Cancelled.Should().BeTrue("pre-cancelled token should result in cancellation");
        result.ExitCode.Should().Be(-1);
        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
    }

    /// <summary>
    /// Execute a long-running script and cancel it mid-flight.
    /// The executor should kill the process and return Cancelled = true.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledDuringExecution_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var result = await _executor.ExecuteAsync(
            "Start-Sleep -Seconds 60", timeoutSeconds: 30, ct: cts.Token);

        result.Should().NotBeNull();
        result.Cancelled.Should().BeTrue("token cancelled during execution should result in cancellation");
        result.ExitCode.Should().Be(-1);
        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
    }

    // ─── Multi-line Script ─────────────────────────────────────────────

    /// <summary>
    /// Execute a multi-line script piped via stdin to verify that stdin-based
    /// script delivery works correctly (avoiding command-line escaping issues).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MultiLineScript_ExecutesCorrectly()
    {
        var script = @"
$a = 1
$b = 2
$c = $a + $b
Write-Output $c
";
        var result = await _executor.ExecuteAsync(script);

        result.Should().NotBeNull();
        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().Be("3");
        result.Success.Should().BeTrue();
    }

    // ─── Success Property ──────────────────────────────────────────────

    /// <summary>
    /// Verify that the Success property is a composite of ExitCode, TimedOut, and Cancelled.
    /// Regression test: ensures Success is correctly computed, not just stored.
    /// </summary>
    [Fact]
    public void PowerShellResult_Success_IsFalseWhenExitCodeNonZero()
    {
        var result = new PowerShellResult { ExitCode = 1 };
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void PowerShellResult_Success_IsFalseWhenTimedOut()
    {
        var result = new PowerShellResult { ExitCode = 0, TimedOut = true };
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void PowerShellResult_Success_IsFalseWhenCancelled()
    {
        var result = new PowerShellResult { ExitCode = 0, Cancelled = true };
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void PowerShellResult_Success_IsTrueWhenClean()
    {
        var result = new PowerShellResult { ExitCode = 0, TimedOut = false, Cancelled = false };
        result.Success.Should().BeTrue();
    }

    // ─── Executable Detection ──────────────────────────────────────────

    /// <summary>
    /// Verify that the executor detected a PowerShell executable.
    /// Either pwsh or powershell must be available on the test machine.
    /// </summary>
    [Fact]
    public void Constructor_DetectsPowerShellExecutable()
    {
        _executor.ExecutablePath.Should().BeOneOf("pwsh", "powershell",
            "the executor must detect either pwsh or powershell");
    }

    // ─── Hyper-V Module Detection Regression Tests ──────────────────────
    //
    // Regression tests for the DetectPowerShell Hyper-V module check fix.
    // Bug: DetectPowerShell() previously preferred pwsh blindly, but pwsh (PS 7+)
    // does not include the Hyper-V module by default, causing all Hyper-V cmdlets
    // to fail silently (exit code 0, but "The term 'Get-VM' is not recognized").
    // Fix: DetectPowerShell() now verifies Hyper-V module availability in pwsh
    // before selecting it; falls back to powershell.exe (5.1) if unavailable.
    // See /myplans/remoting/remoting-design.md — Assumption #1.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression: The selected PowerShell executable must have the Hyper-V
    /// module available. This is the core invariant that the fix guarantees.
    /// Before the fix, pwsh could be selected without verifying module availability,
    /// causing all Hyper-V operations to fail at runtime.
    /// </summary>
    [Fact]
    public async Task DetectPowerShell_SelectedExecutable_HasHyperVModuleAvailable()
    {
        // Run the same Hyper-V module check that DetectPowerShell uses,
        // but against the already-selected executable.
        var result = await _executor.ExecuteAsync(
            "if (Get-Module -ListAvailable -Name Hyper-V) { Write-Output 'HyperV-OK' } else { Write-Output 'HyperV-MISSING'; exit 1 }");

        result.ExitCode.Should().Be(0,
            $"the selected executable '{_executor.ExecutablePath}' must have the Hyper-V module available");
        result.Stdout.Trim().Should().Contain("HyperV-OK",
            $"the selected executable '{_executor.ExecutablePath}' must report Hyper-V module as available");
    }

    /// <summary>
    /// Regression: The selected executable must be able to resolve Hyper-V cmdlets.
    /// This was the original failure mode — Get-VM returned "not recognized" error
    /// but exit code 0 in pwsh without the Hyper-V module.
    /// </summary>
    [Fact]
    public async Task DetectPowerShell_SelectedExecutable_CanResolveHyperVCmdlets()
    {
        // Get-Command will fail if the cmdlet isn't available, which is exactly
        // the scenario that caused the original bug.
        var result = await _executor.ExecuteAsync(
            "Get-Command Get-VM -ErrorAction Stop | Select-Object -ExpandProperty Name");

        result.ExitCode.Should().Be(0,
            $"'{_executor.ExecutablePath}' must resolve Get-VM cmdlet — " +
            "this was the original regression where pwsh silently failed");
        result.Stdout.Trim().Should().Be("Get-VM");
    }

    /// <summary>
    /// Regression: When pwsh is selected, it must be because the Hyper-V module
    /// was verified available. If powershell is selected, verify it also has the
    /// module (powershell.exe 5.1 on Windows always includes Hyper-V when the
    /// role/feature is installed).
    /// </summary>
    [Fact]
    public async Task DetectPowerShell_PwshSelectedOnlyWhenHyperVModuleAvailable()
    {
        var executable = _executor.ExecutablePath;

        if (executable == "pwsh")
        {
            // If pwsh was selected, independently verify it has the module.
            // This confirms DetectPowerShell's check was correct.
            var result = await _executor.ExecuteAsync(
                "if (Get-Module -ListAvailable -Name Hyper-V) { Write-Output 'HyperV-OK' } else { exit 1 }");

            result.ExitCode.Should().Be(0,
                "pwsh was selected, so the Hyper-V module must be verified available — " +
                "this is the core fix for the DetectPowerShell regression");
            result.Stdout.Trim().Should().Contain("HyperV-OK");
        }
        else
        {
            // powershell.exe was selected — this is the expected fallback when
            // pwsh either doesn't exist or lacks the Hyper-V module.
            executable.Should().Be("powershell",
                "when pwsh is not suitable, powershell.exe must be the fallback");

            // Verify this fallback also has Hyper-V (it should on any Windows
            // machine with the Hyper-V role installed).
            var result = await _executor.ExecuteAsync(
                "if (Get-Module -ListAvailable -Name Hyper-V) { Write-Output 'HyperV-OK' } else { exit 1 }");

            result.ExitCode.Should().Be(0,
                "powershell.exe fallback must also have the Hyper-V module available");
        }
    }

    /// <summary>
    /// Regression: Verify that a fresh PowerShellExecutor instance consistently
    /// selects an executable that supports Hyper-V. The detection must be
    /// deterministic — creating multiple instances should yield the same result.
    /// </summary>
    [Fact]
    public void DetectPowerShell_IsConsistentAcrossInstances()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>();
        var executor2 = new PowerShellExecutor(logger);

        executor2.ExecutablePath.Should().Be(_executor.ExecutablePath,
            "PowerShell detection should be deterministic — " +
            "multiple instances must select the same executable");
    }

    /// <summary>
    /// Regression: When the selected executable runs a Hyper-V script that
    /// lists modules, the Hyper-V module must appear. This tests the full
    /// pipeline that was broken before the fix — executing a real module
    /// query through the executor's process-spawning mechanism.
    /// </summary>
    [Fact]
    public async Task DetectPowerShell_SelectedExecutable_ListsHyperVInModules()
    {
        var result = await _executor.ExecuteAsync(
            "Get-Module -ListAvailable -Name Hyper-V | Select-Object -ExpandProperty Name");

        result.ExitCode.Should().Be(0,
            $"'{_executor.ExecutablePath}' must be able to list the Hyper-V module");
        result.Stdout.Trim().Should().Contain("Hyper-V",
            "the Hyper-V module must be listed as available by the selected executable");
    }

    // ─── Script-Dump Diagnostic Tests ───────────────────────────────────
    //
    // See /myplans/operational/script-dump/script-dump-design.md §9 — Testing Strategy.
    // See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md
    // — TI-D6, TI-D7: tests inject a FakeEnvironment + FixedTempPathProvider so the
    // dump path is exercised deterministically without mutating process-wide env
    // state or enumerating the shared %TEMP%. Each test owns a per-test temp
    // directory that is cleaned up in a finally block.
    // ─────────────────────────────────────────────────────────────────────

    private const string DumpDirEnvVar = "HYPERV_MCP_DUMP_PS_SCRIPTS";

    /// <summary>
    /// Allocates (but does not create) a unique per-test temp directory path under
    /// <c>%TEMP%\hvmcp-tests\&lt;guid&gt;</c>. Test bodies create + delete it.
    /// </summary>
    private static string AllocatePerTestTempDir() =>
        Path.Combine(Path.GetTempPath(), "hvmcp-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Build a <see cref="PowerShellExecutor"/> that reads <c>HYPERV_MCP_DUMP_PS_SCRIPTS</c>
    /// from <paramref name="dumpDirEnvValue"/> (null = disabled) and stages its
    /// temp <c>hvmcp-*.ps1</c> file under <paramref name="tempDir"/>. Both the
    /// dump dir and the temp dir typically point at the same per-test directory.
    /// </summary>
    private static PowerShellExecutor BuildIsolatedExecutor(string tempDir, string? dumpDirEnvValue)
    {
        var env = new FakeEnvironment();
        if (dumpDirEnvValue is not null)
        {
            env.Set(DumpDirEnvVar, dumpDirEnvValue);
        }

        var temp = new FixedTempPathProvider(tempDir);
        var logger = NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>();
        return new PowerShellExecutor(logger, env, temp);
    }

    /// <summary>Pass 1 — structural replacement of the canonical $secPass / $cred block.</summary>
    [Fact]
    public void MaskCredentials_ReplacesSecureStringBlock()
    {
        var script = @"$secPass = ConvertTo-SecureString 'P@ssw0rd!' -AsPlainText -Force
$cred = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList 'admin', $secPass
Get-VM -Credential $cred";

        var masked = PowerShellExecutor.MaskCredentials(script);

        masked.Should().NotContain("P@ssw0rd!", "the plaintext password must not appear in the masked output");
        masked.Should().NotContain("$secPass = ConvertTo-SecureString 'P@ssw0rd!'",
            "the canonical secPass assignment must be structurally replaced");
        masked.Should().Contain("[HVMCP-DUMP] credential block redacted",
            "the structural replacement marker comment must be present");
        masked.Should().Contain("Get-Credential",
            "the replacement should hint at Get-Credential for manual rerun");
        masked.Should().Contain("Get-VM -Credential $cred",
            "non-credential lines after the block must be preserved");
    }

    /// <summary>Pass 2 — defense-in-depth sweep of stray ConvertTo-SecureString literals.</summary>
    [Fact]
    public void MaskCredentials_DefenseInDepthCatchesStrayConvertToSecureString()
    {
        // A stray ConvertTo-SecureString that does NOT match the canonical $secPass/$cred block.
        var script = "$other = ConvertTo-SecureString 'StrayPlaintext123' -AsPlainText -Force";

        var masked = PowerShellExecutor.MaskCredentials(script);

        masked.Should().NotContain("StrayPlaintext123",
            "Pass 2 must redact stray inline ConvertTo-SecureString literals");
        masked.Should().Contain("***REDACTED***",
            "Pass 2 must replace the literal with the redacted marker");
    }

    /// <summary>
    /// Regression — passwords containing or ending with backslash must still be masked.
    /// PowerShell single-quoted strings do NOT treat backslash as an escape character,
    /// so a literal ending in <c>\</c> immediately before the closing quote is valid
    /// PowerShell and the masker must handle it. Prior implementation treated <c>\</c>
    /// as an escape and failed to match these blocks, leaking the plaintext password.
    /// </summary>
    [Theory]
    [InlineData(@"DOMAIN\admin\")]            // ends with backslash
    [InlineData(@"P@ss\word\")]                 // multiple backslashes incl. trailing
    [InlineData(@"trailing-backslash\")]
    [InlineData(@"middle\back\slash")]
    public void MaskCredentials_BackslashInPassword_IsRedacted(string plaintextPassword)
    {
        // Build the canonical block exactly as CredentialResolver.BuildCredentialBlock would,
        // with the password inlined (no escaping required since single quotes are absent).
        var script =
            $"$secPass = ConvertTo-SecureString '{plaintextPassword}' -AsPlainText -Force\r\n" +
            $"$cred = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList 'admin', $secPass\r\n" +
            "Get-VM -Credential $cred";

        var masked = PowerShellExecutor.MaskCredentials(script);

        masked.Should().NotContain(plaintextPassword,
            "passwords containing or ending with backslash must be redacted by Pass 1 / Pass 2");
        masked.Should().Contain("[HVMCP-DUMP] credential block redacted",
            "the canonical block matcher must still trigger for backslash-bearing passwords");
    }

    /// <summary>
    /// Regression — passwords containing embedded single quotes (escaped as <c>''</c>
    /// per PowerShell single-quoted string grammar) must still be masked.
    /// </summary>
    [Fact]
    public void MaskCredentials_DoubledSingleQuoteInPassword_IsRedacted()
    {
        // Canonical PS literal for the password "it's a 'secret'" is 'it''s a ''secret'''.
        const string psEscapedPassword = "it''s a ''secret''";
        var script =
            $"$secPass = ConvertTo-SecureString '{psEscapedPassword}' -AsPlainText -Force\r\n" +
            "$cred = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList 'admin', $secPass\r\n" +
            "Get-VM -Credential $cred";

        var masked = PowerShellExecutor.MaskCredentials(script);

        masked.Should().NotContain(psEscapedPassword,
            "passwords with embedded doubled single quotes must be redacted by Pass 1");
        masked.Should().Contain("[HVMCP-DUMP] credential block redacted");
    }

    /// <summary>
    /// Regression — Pass 2 stray-literal sweep also handles backslash-bearing literals
    /// when no canonical block surrounds them.
    /// </summary>
    [Fact]
    public void MaskCredentials_StrayLiteralWithTrailingBackslash_IsRedacted()
    {
        var script = @"$other = ConvertTo-SecureString 'StrayPassword\' -AsPlainText -Force";

        var masked = PowerShellExecutor.MaskCredentials(script);

        masked.Should().NotContain(@"StrayPassword\",
            "Pass 2 must redact stray literals whose contents end with a backslash");
        masked.Should().Contain("***REDACTED***");
    }

    /// <summary>Negative test — non-credential content is untouched.</summary>
    [Fact]
    public void MaskCredentials_PreservesNonCredentialContent()
    {
        var script = @"# Comment about credentials
$vmName = 'web-server-01'
$path = 'C:\HyperV\VMs\web-server-01.vhdx'
Get-VM -Name $vmName -ComputerName 'hyperv-host.contoso.com'
Write-Output 'Operation complete'";

        var masked = PowerShellExecutor.MaskCredentials(script);

        masked.Should().Be(script,
            "non-credential content (comments, hostnames, VM names, paths, normal cmdlets) must be untouched");
    }

    /// <summary>End-to-end: env var set → masked dump file appears.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDumpDirSet_WritesMaskedScript()
    {
        var perTestDir = AllocatePerTestTempDir();
        Directory.CreateDirectory(perTestDir);
        try
        {
            var executor = BuildIsolatedExecutor(perTestDir, dumpDirEnvValue: perTestDir);

            var script = @"$secPass = ConvertTo-SecureString 'TopSecret!' -AsPlainText -Force
$cred = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList 'admin', $secPass
Write-Output 'hello'";

            var result = await executor.ExecuteAsync(script);
            result.Should().NotBeNull();

            Directory.Exists(perTestDir).Should().BeTrue("the dump dir should have been created");
            // Dump files use a timestamp+guid prefixed name; the temp .ps1 also lives
            // in this directory (TI-D7). Distinguish by reading content.
            var dumpFiles = Directory.GetFiles(perTestDir, "hvmcp-*.ps1")
                .Where(f => File.ReadAllText(f).Contains("[HVMCP-DUMP]", StringComparison.Ordinal))
                .ToArray();
            dumpFiles.Should().HaveCount(1, "exactly one masked dump file should have been written");

            var content = await File.ReadAllTextAsync(dumpFiles[0]);
            content.Should().NotContain("TopSecret!", "the dump must be masked");
            content.Should().Contain("[HVMCP-DUMP] credential block redacted");
            content.Should().Contain("Write-Output 'hello'", "non-credential lines must be preserved");
        }
        finally
        {
            try { if (Directory.Exists(perTestDir)) Directory.Delete(perTestDir, recursive: true); } catch { }
        }
    }

    /// <summary>When dumping is enabled, the un-masked %TEMP% script is preserved (SD-D5 / §8).</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDumpDirSet_PreservesTempFile()
    {
        var perTestDir = AllocatePerTestTempDir();
        Directory.CreateDirectory(perTestDir);
        try
        {
            var executor = BuildIsolatedExecutor(perTestDir, dumpDirEnvValue: perTestDir);

            await executor.ExecuteAsync("Write-Output 'preserve-temp-test'");

            // With dumping enabled, the staged un-masked temp .ps1 (whose contents
            // do NOT contain the [HVMCP-DUMP] marker) must be preserved alongside
            // the masked dump. Enumerate ONLY the per-test directory (TI-D7).
            var unmaskedTempFiles = Directory.GetFiles(perTestDir, "hvmcp-*.ps1")
                .Where(f => !File.ReadAllText(f).Contains("[HVMCP-DUMP]", StringComparison.Ordinal))
                .ToArray();

            unmaskedTempFiles.Should().HaveCountGreaterOrEqualTo(1,
                "with dumping enabled, the un-masked temp .ps1 must be preserved for manual rerun");
        }
        finally
        {
            try { if (Directory.Exists(perTestDir)) Directory.Delete(perTestDir, recursive: true); } catch { }
        }
    }

    /// <summary>Hard rule: dump-side failures must never throw out of ExecuteAsync (§6).</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDumpDirUnwritable_DoesNotThrow()
    {
        // A path with reserved/invalid characters that CreateDirectory will reject on Windows.
        var bad = @"C:\nope?<>|invalid\hvmcp-dump";
        var perTestDir = AllocatePerTestTempDir();
        Directory.CreateDirectory(perTestDir);
        try
        {
            // Env points at a bad dump dir; temp staging still uses a valid per-test dir
            // so the executor can write its working .ps1.
            var executor = BuildIsolatedExecutor(perTestDir, dumpDirEnvValue: bad);

            Func<Task> act = async () => await executor.ExecuteAsync("Write-Output 'survive-bad-dump-dir'");

            await act.Should().NotThrowAsync(
                "any prepare-phase failure must be swallowed at Warning level (SD-D6)");
        }
        finally
        {
            try { if (Directory.Exists(perTestDir)) Directory.Delete(perTestDir, recursive: true); } catch { }
        }
    }

    /// <summary>Regression guard: env var unset → today's temp-file cleanup behavior unchanged.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDumpDirUnset_DeletesTempFileAsBefore()
    {
        var perTestDir = AllocatePerTestTempDir();
        Directory.CreateDirectory(perTestDir);
        try
        {
            var executor = BuildIsolatedExecutor(perTestDir, dumpDirEnvValue: null);

            await executor.ExecuteAsync("Write-Output 'no-dump-test'");

            // Enumerate ONLY the per-test directory (TI-D7). With dumping disabled,
            // the executor must delete its staged temp .ps1.
            var leaked = Directory.GetFiles(perTestDir, "hvmcp-*.ps1");

            leaked.Should().BeEmpty(
                "with dumping disabled, ExecuteAsync must delete its temp file as before (regression guard)");
        }
        finally
        {
            try { if (Directory.Exists(perTestDir)) Directory.Delete(perTestDir, recursive: true); } catch { }
        }
    }

    /// <summary>SD-D4: allowDump=false short-circuits dumping for the OS-install code path.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenOsInstallScript_NeverWritesDump_EvenIfEnvVarSet()
    {
        var perTestDir = AllocatePerTestTempDir();
        Directory.CreateDirectory(perTestDir);
        try
        {
            var executor = BuildIsolatedExecutor(perTestDir, dumpDirEnvValue: perTestDir);

            await executor.ExecuteAsync(
                "Write-Output 'os-install-stub'",
                timeoutSeconds: 30,
                ct: default,
                allowDump: false);

            // No masked dump file may exist (it would contain the [HVMCP-DUMP] marker).
            var maskedDumps = Directory.GetFiles(perTestDir, "hvmcp-*.ps1")
                .Where(f => File.ReadAllText(f).Contains("[HVMCP-DUMP]", StringComparison.Ordinal))
                .ToArray();
            maskedDumps.Should().BeEmpty(
                "allowDump=false must prevent any dump file from being written even when the env var is set");
        }
        finally
        {
            try { if (Directory.Exists(perTestDir)) Directory.Delete(perTestDir, recursive: true); } catch { }
        }
    }

    /// <summary>SD-D7: concurrent dumps don't collide; filenames are unique via timestamp + GUID.</summary>
    [Fact]
    public async Task ExecuteAsync_TwoConcurrentCalls_BothDumpsWrittenWithUniqueFilenames()
    {
        var perTestDir = AllocatePerTestTempDir();
        Directory.CreateDirectory(perTestDir);
        try
        {
            var executor = BuildIsolatedExecutor(perTestDir, dumpDirEnvValue: perTestDir);

            // Use scripts that contain the canonical credential block so MaskCredentials
            // emits the [HVMCP-DUMP] marker — this lets the per-test-dir enumeration
            // (TI-D7) distinguish masked dump files from un-masked staged temp .ps1 files.
            const string credScript1 = @"$secPass = ConvertTo-SecureString 'TopSecret1!' -AsPlainText -Force
$cred = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList 'admin', $secPass
Write-Output 'concurrent-1'";
            const string credScript2 = @"$secPass = ConvertTo-SecureString 'TopSecret2!' -AsPlainText -Force
$cred = New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList 'admin', $secPass
Write-Output 'concurrent-2'";

            var t1 = Task.Run(() => executor.ExecuteAsync(credScript1));
            var t2 = Task.Run(() => executor.ExecuteAsync(credScript2));
            await Task.WhenAll(t1, t2);

            // Distinguish masked dumps from the un-masked staged temp files by content.
            var dumpFiles = Directory.GetFiles(perTestDir, "hvmcp-*.ps1")
                .Where(f => File.ReadAllText(f).Contains("[HVMCP-DUMP]", StringComparison.Ordinal))
                .ToArray();
            dumpFiles.Should().HaveCount(2, "both concurrent dumps must be written");
            dumpFiles.Select(Path.GetFileName).Distinct().Should().HaveCount(2,
                "filenames must be unique (timestamp + GUID disambiguation)");
        }
        finally
        {
            try { if (Directory.Exists(perTestDir)) Directory.Delete(perTestDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// §1 Activation Contract: matrix of disabled sentinels and path forms.
    /// Tests <see cref="PowerShellExecutor.ResolveDumpDir(string?)"/> directly (the testable seam).
    /// A null result == disabled; non-null == enabled.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("FALSE", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData(@"C:\dump", true)]
    [InlineData(@"\\unc\share\dump", true)]
    [InlineData("./relative", true)]
    public void IsDumpEnabled_ActivationContractMatrix(string? rawValue, bool expectedEnabled)
    {
        var result = PowerShellExecutor.ResolveDumpDir(rawValue);

        if (expectedEnabled)
        {
            result.Should().NotBeNull(
                $"raw value '{rawValue ?? "<null>"}' should resolve to a dump directory (enabled)");
        }
        else
        {
            result.Should().BeNull(
                $"raw value '{rawValue ?? "<null>"}' should be treated as disabled (sentinel or empty)");
        }
    }

    /// <summary>
    /// Regression — when the env var value can't be normalized into a path
    /// (e.g. embedded NUL byte, or invalid characters), the dump path must degrade
    /// to disabled at Warning level rather than throwing out of <see cref="PowerShellExecutor.ExecuteAsync"/>.
    /// Covers SD-D6 / §6 (Failure Modes): "Path resolution failure" row.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenDumpDirEnvVarFailsNormalization_DegradesAndDoesNotWriteDump()
    {
        // A NUL byte inside the path is rejected by Path.GetFullPath on .NET (throws
        // ArgumentException). This exercises the resolution-time failure path that was
        // previously outside the prepare-phase try/catch.
        var bad = "bad\0path";
        const string marker = "survive-bad-normalization-marker";

        var perTestDir = AllocatePerTestTempDir();
        Directory.CreateDirectory(perTestDir);
        try
        {
            var executor = BuildIsolatedExecutor(perTestDir, dumpDirEnvValue: bad);

            PowerShellResult? result = null;
            Func<Task> act = async () =>
                result = await executor.ExecuteAsync($"Write-Output '{marker}'");

            await act.Should().NotThrowAsync(
                "path-normalization failures must be handled in the same warning/degrade path " +
                "as CreateDirectory failures (SD-D6)");

            // The script must have actually executed — degradation is graceful, not silent skip.
            result.Should().NotBeNull();
            result!.Success.Should().BeTrue("the underlying pwsh invocation must succeed even when dump prep fails");
            result.Stdout.Should().Contain(marker);

            // The bad value cannot be a valid directory; therefore no dump dir exists for it,
            // confirming the prepare-phase failure short-circuited the dump path entirely.
            Directory.Exists(bad).Should().BeFalse(
                "an invalid env var value must never result in a created dump directory");

            // Enumerate ONLY the per-test directory (TI-D7) — no masked dump file may exist.
            var maskedDumps = Directory.GetFiles(perTestDir, "hvmcp-*.ps1")
                .Where(f => File.ReadAllText(f).Contains("[HVMCP-DUMP]", StringComparison.Ordinal))
                .ToArray();
            maskedDumps.Should().BeEmpty(
                "a normalization-failed dump path must not produce a dump file anywhere");
        }
        finally
        {
            try { if (Directory.Exists(perTestDir)) Directory.Delete(perTestDir, recursive: true); } catch { }
        }
    }
}
