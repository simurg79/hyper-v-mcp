using System.Diagnostics;
using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #205 (TC-W05c): <c>vm_run_script</c> previously stripped literal double-quote
/// characters from script bodies because the in-guest wrapper re-shelled through
/// <c>powershell.exe -NoProfile -Command $scriptText</c>, whose argv tokenizer consumes
/// bare <c>"</c> characters as quoting metacharacters.
///
/// Component design: <c>/myplans/execution/commands/vm-run-script-quote-preservation-design.md</c>
/// (VC-QP-D1..D8, T1–T19). Parent: <c>/myplans/execution/commands/commands-design.md</c> CMD-D5/D8.
///
/// The fix replaces <c>-Command &lt;string&gt;</c> with a temp <c>hypervmcp-*.ps1</c> file
/// (UTF-8 with BOM, per VC-QP-D6) and <c>powershell.exe -NoProfile -ExecutionPolicy Bypass
/// -File &lt;path&gt;</c> (VC-QP-D1, D5). The same fix applies to the <c>pwsh</c> arm
/// (VC-QP-D3) and to the <c>powershell</c>/<c>pwsh</c>/<c>default</c> arms of
/// <see cref="CommandExecutor.CommandInnerScript"/>.
///
/// These tests have two flavors:
///   (a) Static-string assertions against the production
///       <see cref="CommandExecutor.ScriptInnerScript"/> /
///       <see cref="CommandExecutor.CommandInnerScript"/> constants — these verify the
///       generated wrapper contains the expected fix shape (T8, T9, T10) and that the
///       legacy <c>-Command &lt;string&gt;</c> anti-pattern has been removed. These run
///       in any environment.
///   (b) Real-PowerShell end-to-end tests (T1–T7, T11–T19) that execute the production
///       <c>ScriptInnerScript</c> / <c>CommandInnerScript</c> bodies via
///       <c>powershell.exe</c> as a real <see cref="Process"/>, with a user-supplied
///       script body containing literal quotes. T16 is the canonical real-parser
///       regression mirror of issue #204 T14. The test project targets
///       <c>net8.0-windows</c>, so <c>powershell.exe</c> is guaranteed available.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue205QuotePreservationTests
{
    // ───────────────────────────────────────────────────────────────────
    // Static-string assertions against the production constants.
    // These cannot regress without removing the fix.
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// T8 — Temp filename uses the <c>hypervmcp-</c> prefix in both the happy path
    /// and the GUID collision-fallback (VC-QP-D4).
    /// </summary>
    [Fact]
    public void T8_ScriptInnerScript_UsesHypervmcpPrefixForTempFile()
    {
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "'hypervmcp-' + $tempName + '.ps1'",
            "VC-QP-D4: happy path renames GetTempFileName() result to hypervmcp-<tmpname>.ps1");
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "'hypervmcp-' + [System.Guid]::NewGuid().ToString('N') + '.ps1'",
            "VC-QP-D4: collision fallback uses hypervmcp-<guid>.ps1");
        CommandExecutor.CommandInnerScript.Should().Contain(
            "'hypervmcp-' + $tempName + '.ps1'",
            "VC-QP-D3: vm_run_command powershell/pwsh arms get the same fix");
        CommandExecutor.CommandInnerScript.Should().Contain(
            "'hypervmcp-' + [System.Guid]::NewGuid().ToString('N') + '.ps1'");
    }

    /// <summary>
    /// T9 — Generated wrapper invokes the interpreter with <c>-ExecutionPolicy Bypass -File</c>
    /// (VC-QP-D5).
    /// </summary>
    [Fact]
    public void T9_ScriptInnerScript_InvokesInterpreterWithFileAndExecutionPolicyBypass()
    {
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1",
            "VC-QP-D1/D5: powershell arm must invoke -File with ExecutionPolicy Bypass");
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "pwsh.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1",
            "VC-QP-D3: pwsh arm gets the same fix");

        CommandExecutor.CommandInnerScript.Should().Contain(
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1");
        CommandExecutor.CommandInnerScript.Should().Contain(
            "pwsh.exe -NoProfile -ExecutionPolicy Bypass -File $tempPs1");
    }

    /// <summary>
    /// T10 (negative) — The <c>-Command &lt;string&gt;</c> anti-pattern is gone from
    /// every powershell/pwsh arm. The <c>cmd</c> arm is untouched and never used
    /// <c>-Command</c> at all.
    /// </summary>
    [Fact]
    public void T10_ScriptInnerScript_DoesNotInvokePowerShellWithDashCommand()
    {
        CommandExecutor.ScriptInnerScript.Should().NotContain(
            "powershell.exe -NoProfile -Command",
            "VC-QP-D1: the -Command anti-pattern (which strips literal quotes) must not return");
        CommandExecutor.ScriptInnerScript.Should().NotContain(
            "pwsh.exe -NoProfile -Command");

        CommandExecutor.CommandInnerScript.Should().NotContain(
            "powershell.exe -NoProfile -Command");
        CommandExecutor.CommandInnerScript.Should().NotContain(
            "pwsh.exe -NoProfile -Command");
    }

    /// <summary>
    /// VC-QP-D6 — temp file must be written as UTF-8 *with BOM*.
    /// </summary>
    [Fact]
    public void T8b_ScriptInnerScript_WritesTempFileAsUtf8WithBom()
    {
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "[System.Text.UTF8Encoding]::new($true)",
            "VC-QP-D6: temp .ps1 must be written UTF-8 *with* BOM so Windows PowerShell 5.1 reads it as UTF-8");
        CommandExecutor.CommandInnerScript.Should().Contain(
            "[System.Text.UTF8Encoding]::new($true)");
    }

    /// <summary>
    /// VC-QP-D4 — collision detection: <c>Move-Item</c> in the happy path must NOT
    /// use <c>-Force</c>. Otherwise a colliding <c>hypervmcp-*.ps1</c> would be silently
    /// overwritten and the GUID fallback would never run.
    /// </summary>
    [Fact]
    public void VcQpD4_MoveItem_DoesNotUseForce()
    {
        CommandExecutor.ScriptInnerScript.Should().NotContain(
            "Move-Item -LiteralPath $tempBase -Destination $tempPs1 -Force",
            "VC-QP-D4: -Force would silently overwrite collisions and bypass the GUID fallback");
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "Move-Item -LiteralPath $tempBase -Destination $tempPs1 -ErrorAction Stop");

        CommandExecutor.CommandInnerScript.Should().NotContain(
            "Move-Item -LiteralPath $tempBase -Destination $tempPs1 -Force");
        CommandExecutor.CommandInnerScript.Should().Contain(
            "Move-Item -LiteralPath $tempBase -Destination $tempPs1 -ErrorAction Stop");
    }

    /// <summary>
    /// VC-QP-D7 — cleanup of the temp <c>.ps1</c> file happens in a <c>finally</c>
    /// block on both success and failure paths.
    /// </summary>
    [Fact]
    public void VcQpD7_TempFileCleanup_InFinallyBlock()
    {
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "Remove-Item -LiteralPath $tempPs1 -Force -ErrorAction SilentlyContinue",
            "VC-QP-D1: temp .ps1 must be removed in finally regardless of inner success/failure");
        CommandExecutor.CommandInnerScript.Should().Contain(
            "Remove-Item -LiteralPath $tempPs1 -Force -ErrorAction SilentlyContinue");
    }

    /// <summary>
    /// VC-QP-D7 invariant — the cmd arm of <see cref="CommandExecutor.CommandInnerScript"/>
    /// is left exactly as it was (already correct: <c>cmd.exe /c $cmd</c> needs no temp file).
    /// </summary>
    [Fact]
    public void VcQpD7_CmdArm_OfCommandInnerScript_Unchanged()
    {
        CommandExecutor.CommandInnerScript.Should().Contain(
            "'cmd'        { $output = & cmd.exe /c $cmd 2>&1 }",
            "CMD arm has no quote-stripping bug and must remain a one-liner");
    }

    /// <summary>
    /// VC-QP-D7 invariant — the cmd arm of <see cref="CommandExecutor.ScriptInnerScript"/>
    /// still uses the existing temp <c>.cmd</c> + <c>cmd.exe /c</c> pattern (already correct).
    /// </summary>
    [Fact]
    public void VcQpD7_CmdArm_OfScriptInnerScript_Unchanged()
    {
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "[System.IO.Path]::GetTempFileName() + '.cmd'",
            "the cmd script arm already used a temp file and must not be touched");
        CommandExecutor.ScriptInnerScript.Should().Contain(
            "$output = & cmd.exe /c $tempFile 2>&1");
    }

    // ───────────────────────────────────────────────────────────────────
    // Real-PowerShell end-to-end tests (T1–T7, T11–T19).
    // The test project targets net8.0-windows; powershell.exe is always available.
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the production <see cref="CommandExecutor.ScriptInnerScript"/> body with a
    /// user-supplied script body and shell selector, via a real <c>powershell.exe</c>
    /// child process. Returns the JSON envelope emitted by the inner script.
    /// </summary>
    private static string RunScriptInnerWithRealPowerShell(string scriptBody, string shell)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptBody));
        // Wrap the *production* ScriptInnerScript text in `& { ... }` so it executes
        // as a scriptblock with positional args ($base64Script, $sh).
        var wrapper =
            "& { " + CommandExecutor.ScriptInnerScript + " } " +
            SingleQuote(b64) + " " + SingleQuote(shell);
        return RunPowerShellEncoded(wrapper);
    }

    private static string RunCommandInnerWithRealPowerShell(string commandBody, string shell)
    {
        var wrapper =
            "& { " + CommandExecutor.CommandInnerScript + " } " +
            SingleQuote(commandBody) + " " + SingleQuote(shell);
        return RunPowerShellEncoded(wrapper);
    }

    /// <summary>
    /// Returns the trimmed stdout of the powershell.exe child (which is the JSON envelope
    /// emitted by the inner script's final <c>ConvertTo-Json -Compress</c>).
    /// </summary>
    private static string RunPowerShellEncoded(string wrapper)
    {
        // Windows CreateProcess caps the command line at 32,767 chars. The wrapper
        // becomes UTF-16LE → base64 (≈ 2.66× growth) when passed via -EncodedCommand,
        // so wrappers larger than ~12 KB blow the limit. For large wrappers (notably
        // T13's ≥ 8 KB body), fall back to writing the wrapper to a temp .ps1 and
        // invoking it via -File. -File reads the script from disk verbatim and is
        // not subject to the command-line length cap, while still exercising real
        // powershell.exe end-to-end (same goal as the -EncodedCommand path).
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));
        var useFile = encoded.Length > 30_000;

        string? tempPs1 = null;
        ProcessStartInfo psi;
        if (useFile)
        {
            tempPs1 = Path.Combine(Path.GetTempPath(),
                "hypervmcp-test-" + Guid.NewGuid().ToString("N") + ".ps1");
            // UTF-8 BOM matches the production wrapper's encoding choice (VC-QP-D6)
            // so non-ASCII bytes round-trip unambiguously.
            File.WriteAllText(tempPs1, wrapper, new UTF8Encoding(true));
            psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tempPs1 + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }
        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start powershell.exe");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(60_000);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"powershell.exe outer wrapper failed (exit {proc.ExitCode}). stderr=<{stderr}> stdout=<{stdout}>");
            return stdout.Trim();
        }
        finally
        {
            if (tempPs1 != null)
            {
                try { File.Delete(tempPs1); } catch { /* best-effort cleanup */ }
            }
        }
    }

    private static string SingleQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static (string Stdout, string Stderr, int ExitCode) ParseEnvelope(string envelopeJson)
    {
        envelopeJson.Should().NotBeEmpty("inner script must emit a JSON envelope");
        using var doc = System.Text.Json.JsonDocument.Parse(envelopeJson);
        var root = doc.RootElement;
        return (
            root.GetProperty("Stdout").GetString() ?? string.Empty,
            root.GetProperty("Stderr").GetString() ?? string.Empty,
            root.GetProperty("ExitCode").GetInt32());
    }

    private static IEnumerable<string> EnumerateHypervmcpTempLeftovers() =>
        Directory.EnumerateFiles(Path.GetTempPath(), "hypervmcp-*.ps1", SearchOption.TopDirectoryOnly);

    // ─── T1 — literal " preserved ─────────────────────────────────────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T1_LiteralDoubleQuotesPreserved()
    {
        var envelope = RunScriptInnerWithRealPowerShell(
            "Write-Output \"double-quoted\"", "powershell");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"stderr=<{stderr}>");
        stdout.Trim().Should().Be("double-quoted",
            "Issue #205: literal \" must round-trip through the temp .ps1 + -File path");
    }

    // ─── T2 — literal ' preserved ─────────────────────────────────────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T2_LiteralSingleQuotesPreserved()
    {
        var envelope = RunScriptInnerWithRealPowerShell(
            "Write-Output 'single-quoted'", "powershell");
        var (stdout, _, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0);
        stdout.Trim().Should().Be("single-quoted");
    }

    // ─── T3 — mixed quotes ────────────────────────────────────────────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T3_MixedQuotesPreserved()
    {
        var body = "$x = \"he said \"\"hi\"\"\"; Write-Output $x";
        var envelope = RunScriptInnerWithRealPowerShell(body, "powershell");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"stderr=<{stderr}>");
        stdout.Trim().Should().Be("he said \"hi\"");
    }

    // ─── T4 — pwsh.exe arm parity (only if pwsh is installed) ─────────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T4_PwshArm_LiteralQuotesPreserved()
    {
        if (!PwshAvailable())
            return; // pwsh.exe not on PATH — pwsh arm parity covered by static T9/T10.

        var envelope = RunScriptInnerWithRealPowerShell(
            "Write-Output \"pwsh-double\"", "pwsh");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"stderr=<{stderr}>");
        stdout.Trim().Should().Be("pwsh-double");
    }

    // ─── T5 — vm_run_command (CommandInnerScript powershell arm) parity ─
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T5_CommandInnerScript_PowerShellArm_LiteralQuotesPreserved()
    {
        var envelope = RunCommandInnerWithRealPowerShell(
            "Write-Output \"cmd-arm-double\"", "powershell");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"stderr=<{stderr}>");
        stdout.Trim().Should().Be("cmd-arm-double",
            "VC-QP-D3: vm_run_command powershell arm gets the same fix");
    }

    // ─── T6 — temp .ps1 is cleaned up on success ──────────────────────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T6_TempFileCleanedUpOnSuccess()
    {
        var before = EnumerateHypervmcpTempLeftovers().ToHashSet();
        var envelope = RunScriptInnerWithRealPowerShell(
            "Write-Output \"cleanup-success\"", "powershell");
        var (_, _, exit) = ParseEnvelope(envelope);
        exit.Should().Be(0);

        var after = EnumerateHypervmcpTempLeftovers().ToHashSet();
        var leaked = after.Except(before).ToArray();
        leaked.Should().BeEmpty("VC-QP-D1: temp .ps1 must be removed in finally on success");
    }

    // ─── T7 — temp .ps1 is cleaned up on failure (script throws) ──────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T7_TempFileCleanedUpOnFailure()
    {
        var before = EnumerateHypervmcpTempLeftovers().ToHashSet();
        var envelope = RunScriptInnerWithRealPowerShell(
            "throw 'boom'", "powershell");
        var (_, stderr, exit) = ParseEnvelope(envelope);
        exit.Should().NotBe(0);
        stderr.Should().Contain("boom");

        var after = EnumerateHypervmcpTempLeftovers().ToHashSet();
        var leaked = after.Except(before).ToArray();
        leaked.Should().BeEmpty("VC-QP-D1: temp .ps1 must be removed in finally even when the body throws");
    }

    // ─── T11 — here-strings @"..."@ and @'...'@ round-trip ────────────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T11_HereStringsRoundTrip()
    {
        // Double-quoted here-string with embedded quotes.
        var dq = "$msg = @\"\nhe said \"hi\"\n\"@\nWrite-Output $msg";
        var env1 = RunScriptInnerWithRealPowerShell(dq, "powershell");
        var (stdout1, stderr1, exit1) = ParseEnvelope(env1);
        exit1.Should().Be(0, $"stderr=<{stderr1}>");
        stdout1.Should().Contain("he said \"hi\"");

        // Single-quoted here-string preserves both quote kinds literally.
        var sq = "$msg = @'\nshe said 'hi' and \"yo\"\n'@\nWrite-Output $msg";
        var env2 = RunScriptInnerWithRealPowerShell(sq, "powershell");
        var (stdout2, stderr2, exit2) = ParseEnvelope(env2);
        exit2.Should().Be(0, $"stderr=<{stderr2}>");
        stdout2.Should().Contain("she said 'hi' and \"yo\"");
    }

    // ─── T12 — Unicode (BMP + astral) round-trips via UTF-8 BOM ───────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T12_UnicodeBmpAndAstralRoundTrip()
    {
        var body = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
                   "Write-Output 'café — π — 漢字 — 🚀'";
        var envelope = RunScriptInnerWithRealPowerShell(body, "powershell");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"stderr=<{stderr}>");
        stdout.Should().Contain("café");
        stdout.Should().Contain("π");
        stdout.Should().Contain("漢字");
        stdout.Should().Contain("🚀");
    }

    // ─── T13 — large body (≥ 8 KB) sanity ─────────────────────────────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T13_LargeBodySanity()
    {
        var sb = new StringBuilder();
        // N chosen so total UTF-8 byte count exceeds 8 KB (design says "≥ 8 KB").
        // Each line is roughly 36 bytes; 232 lines yields ~8.3 KB — comfortably above
        // the 8192-byte threshold while keeping the harness's outer -EncodedCommand
        // payload (UTF-16LE base64-of-base64-of-body wrapped in the production
        // ScriptInnerScript source) under the Windows CreateProcess 32 KB command-
        // line limit (raising N to ~260 trips that limit).
        const int N = 232;
        for (var i = 0; i < N; i++)
        {
            sb.Append("Write-Output \"line ").Append(i).Append(": \"\"quoted\"\"\"\n");
        }
        var body = sb.ToString();
        Encoding.UTF8.GetByteCount(body).Should().BeGreaterThan(8 * 1024);

        var envelope = RunScriptInnerWithRealPowerShell(body, "powershell");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"stderr=<{stderr}>");
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(N, "all 200 Write-Output lines must surface");
        lines[0].Should().Contain("line 0: \"quoted\"");
        lines[^1].Should().Contain("line " + (N - 1) + ": \"quoted\"");
    }

    // ─── T14 — $LASTEXITCODE propagates from child + explicit `exit N` ─
    [Theory]
    [Trait("Category", "RealPowerShell")]
    // NOTE: under `powershell.exe -File <script.ps1>`, invoking a native child
    // (e.g. `cmd /c exit 7`) sets `$LASTEXITCODE` but does NOT automatically
    // become the host process exit code — by PowerShell's documented contract
    // the script itself must end with `exit $LASTEXITCODE` to propagate.
    // VC-QP-D7 promises propagation of `$LASTEXITCODE` / explicit `exit N`, both
    // of which require the caller to write the trailing `exit $LASTEXITCODE` for
    // the native-child case. The test data therefore appends it; the explicit
    // `exit N` variant needs no such trailer.
    [InlineData("cmd /c exit 7; exit $LASTEXITCODE", 7)]
    [InlineData("exit 42", 42)]
    public void T14_LastExitCodePropagatesFromFileWrappedChild(string body, int expected)
    {
        var envelope = RunScriptInnerWithRealPowerShell(body, "powershell");
        var (_, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(expected,
            $"VC-QP-D7: $LASTEXITCODE / explicit `exit N` must propagate through -File. stderr=<{stderr}>");
    }

    // ─── T15 — stdout / stderr split preserved with Write-Error ───────
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T15_StdoutStderrSplitPreserved()
    {
        var body = "Write-Output 'on-stdout'; Write-Error 'on-stderr'; Write-Output 'stdout-again'";
        var envelope = RunScriptInnerWithRealPowerShell(body, "powershell");
        var (stdout, stderr, _) = ParseEnvelope(envelope);

        stdout.Should().Contain("on-stdout");
        stdout.Should().Contain("stdout-again");
        stdout.Should().NotContain("on-stderr");
        stderr.Should().Contain("on-stderr");
    }

    // ─── T16 — Real powershell.exe parser regression ───────────────────
    /// <summary>
    /// T16 — Canonical real-parser regression mirror of issue #204 T14. Drives the
    /// production wrapper-generation code (<see cref="CommandExecutor.ScriptInnerScript"/>)
    /// for a body containing literal double quotes, invokes <c>powershell.exe</c> as a
    /// real child process, and asserts the body executes correctly. Catches any
    /// escaping regression that mocks would miss.
    /// </summary>
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T16_RealPowerShellParserRegression_LiteralQuoteBodyExecutesCorrectly()
    {
        const string body = "Write-Output \"double-quoted\"";
        var envelope = RunScriptInnerWithRealPowerShell(body, "powershell");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"real powershell.exe must parse + execute the generated wrapper end-to-end. stderr=<{stderr}>");
        stdout.Trim().Should().Be("double-quoted",
            "T16: literal quotes survive the production wrapper through a real powershell.exe parser");
    }

    // ─── T17 — bare script-block literal body (VC-QP-D8 documented shift) ─
    /// <summary>
    /// T17 — Documents the intentional <c>-Command</c>-vs-<c>-File</c> behavior change
    /// (VC-QP-D8 (a)). Under <c>-File</c>, a bare script-block literal as the entire
    /// body is parsed as an expression statement whose value (a <c>ScriptBlock</c>
    /// object) is emitted to the success stream by the default formatter — the block
    /// itself is NOT invoked, so <c>Write-Output hi</c> never runs. The default
    /// <c>ScriptBlock</c> formatter renders the block's source text, so stdout
    /// contains the literal source <c>Write-Output hi</c> rather than the value
    /// <c>hi</c> that the old <c>-Command</c> code path would have produced.
    /// Verified against real <c>powershell.exe -File</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T17_BareScriptBlockLiteral_UnderFile_NotInvoked_EmitsFormattedScriptBlockSource()
    {
        var envelope = RunScriptInnerWithRealPowerShell(
            "{ Write-Output hi }", "powershell");
        var (stdout, _, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0);
        // The key product behavior asserted by VC-QP-D8 (a) is that the block is
        // NOT executed under -File. The default ScriptBlock formatter emits the
        // block's source text (`Write-Output hi`) rather than the value `hi` that
        // running the block would have produced. We assert the formatted source
        // appears and the bare value `hi` (which would only appear if the block
        // were actually invoked) does not appear on its own.
        stdout.Trim().Should().Be("Write-Output hi",
            "VC-QP-D8 (a): under -File, a bare script-block literal is an expression statement whose ScriptBlock value is emitted by the default formatter as its source text — the block is NOT invoked");
    }

    // ─── T18 — $MyInvocation / $PSCommandPath / $PSScriptRoot populated ─
    /// <summary>
    /// T18 — Documents VC-QP-D8 (b): under <c>-File</c>, the standard introspection
    /// variables are populated with the temp <c>.ps1</c> path. The path must match
    /// the <c>hypervmcp-*.ps1</c> naming seam from VC-QP-D4.
    /// </summary>
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T18_PSCommandPath_PSScriptRoot_MyInvocation_PopulatedWithHypervmcpPath()
    {
        var body =
            "Write-Output ('cmdpath=' + $PSCommandPath)\n" +
            "Write-Output ('root=' + $PSScriptRoot)\n" +
            "Write-Output ('mycmd=' + $MyInvocation.MyCommand.Path)";
        var envelope = RunScriptInnerWithRealPowerShell(body, "powershell");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"stderr=<{stderr}>");
        stdout.Should().Contain("cmdpath=", "PSCommandPath line must surface");
        stdout.Should().Contain("hypervmcp-",
            "VC-QP-D8 (b): $PSCommandPath / $MyInvocation.MyCommand.Path must reference the hypervmcp-*.ps1 temp file");
        stdout.Should().Contain("root=",
            "VC-QP-D8 (b): $PSScriptRoot must be populated (temp dir)");
    }

    // ─── T19 — expression-mode one-liner under -File ──────────────────
    /// <summary>
    /// T19 — Documents VC-QP-D8 (c): trivial expression-mode one-liners still emit
    /// their value under <c>-File</c> (the statement is executed and the value goes
    /// to the success stream).
    /// </summary>
    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T19_ExpressionModeOneLiner_StillEmitsValue()
    {
        var envelope = RunScriptInnerWithRealPowerShell("2+2", "powershell");
        var (stdout, stderr, exit) = ParseEnvelope(envelope);

        exit.Should().Be(0, $"stderr=<{stderr}>");
        stdout.Trim().Should().Be("4",
            "VC-QP-D8 (c): a bare `2+2` statement still emits 4 under -File");
    }

    private static bool PwshAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"exit 0\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
