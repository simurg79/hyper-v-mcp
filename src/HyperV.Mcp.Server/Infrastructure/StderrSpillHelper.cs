using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Shared helper that spills full failure-path diagnostic text (typically a
/// stderr buffer or <c>Exception.ToString()</c> chain) to a per-failure file
/// under <c>%TEMP%</c>. Used by <see cref="PowerShellHost"/> and
/// <see cref="SessionStore"/> to preserve evidence that the historical
/// 300-byte preview cap was truncating away.
/// </summary>
/// <remarks>
/// <para>See /myplans/diagnostics/diagnostics-design.md — DIAG-D6.</para>
/// <para>See https://github.com/simurg79/hyper-v-mcp-server/issues/59.</para>
/// <para>
/// Behavior contract:
/// <list type="bullet">
///   <item><description>Writes to <c>%TEMP%\hypervmcp-stderr-{utc-timestamp}-{pid}-{seq}.log</c>
///     where <c>{seq}</c> is a monotonic process-wide <see cref="System.Threading.Interlocked"/>
///     counter ensuring sub-second concurrent failures cannot collide on the same filename
///     (Code Review Gate 6 Blocker #3).</description></item>
///   <item><description>Defensive credential redaction is applied to every byte before
///     it touches the disk (<c>HYPERV_MCP_VM_PASSWORD</c> + <c>ConvertTo-SecureString</c>
///     + <c>-Credential</c> patterns) so the secrets-never-on-disk invariant holds.</description></item>
///   <item><description>Maintains a soft ~50 MB rotation cap by deleting the oldest
///     spill files first; tolerates per-file <see cref="IOException"/> and continues
///     with the next candidate.</description></item>
///   <item><description>NEVER throws — every public method is fully guarded so that a
///     logging failure cannot break a production code path.</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class StderrSpillHelper
{
    /// <summary>
    /// Filename prefix shared by every spill file so the rotation sweep can
    /// reliably identify them without picking up unrelated content in <c>%TEMP%</c>.
    /// </summary>
    internal const string SpillFilePrefix = "hypervmcp-stderr-";

    /// <summary>
    /// File extension used by spill files.
    /// </summary>
    internal const string SpillFileSuffix = ".log";

    /// <summary>
    /// Soft total-size cap (bytes) for the spill directory. When the directory's
    /// total size after a write exceeds this value, the oldest files are deleted
    /// until the total drops below the cap. ~50 MB per DIAG-D6.
    /// </summary>
    internal const long DefaultRotationCapBytes = 50L * 1024L * 1024L;

    /// <summary>
    /// Test seam: resolves the directory that <see cref="BuildSpillPath"/> writes
    /// to and that <see cref="EnforceRotationCap"/> sweeps. Defaults to
    /// <see cref="Path.GetTempPath"/> so production behavior is unchanged.
    /// Tests may temporarily replace this with a unique per-test subdirectory to
    /// isolate fixture spill files from the real <c>%TEMP%</c> (issue #68).
    /// Thread-safety: marked <see langword="volatile"/> so concurrent reads see a
    /// fully-published delegate reference; tests that mutate this seam must
    /// restore the previous value in a <c>finally</c> block and should avoid
    /// running in parallel with other tests that exercise <see cref="Spill(string?)"/>.
    /// </summary>
    internal static volatile Func<string> TempPathProvider = Path.GetTempPath;

    /// <summary>
    /// Cached UTF-8 encoding without a byte-order-mark, used at the spill-file
    /// write site so the on-disk file length is exactly the payload byte count
    /// (<see cref="Encoding.GetByteCount(string)"/>) — no 3-byte preamble.
    /// Keeps the summary's reported <c>(N bytes)</c> value equal to
    /// <c>new FileInfo(path).Length</c> and yields BOM-less files that play
    /// nicely with Windows <c>Get-Content</c> / downstream log pipelines (#70).
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Spill <paramref name="content"/> (after defensive redaction) to a new file
    /// under <c>%TEMP%</c> and return a single-line summary suitable for embedding
    /// in a MetaLog preview line. The summary contains the full spill path and the
    /// payload byte count, equal to the on-disk file length (UTF-8 no BOM, #70),
    /// so an operator can grep it directly.
    /// </summary>
    /// <param name="content">
    /// Raw diagnostic content (stderr buffer, <c>Exception.ToString()</c>, etc.).
    /// May be null or empty — the method still returns a usable summary string and
    /// does not throw.
    /// </param>
    /// <returns>
    /// A summary like <c>"Spilled=C:\Users\...\hypervmcp-stderr-...log (6020 bytes)"</c>,
    /// or <c>"Spilled=&lt;unavailable&gt; (...)"</c> if the write failed. The
    /// <c>(N bytes)</c> count is payload bytes, equal to on-disk file length
    /// (UTF-8 no BOM).
    /// </returns>
    public static string Spill(string? content)
    {
        var redacted = RedactDefensively(content ?? string.Empty);
        var bytes = Utf8NoBom.GetByteCount(redacted);

        // Code Review Gate 6 Blocker #3 (#59): use FileMode.CreateNew with retry so
        // two failures in the same process during the same UTC second cannot
        // overwrite each other. Retry up to 5 times with a freshly-generated
        // sequence number on IOException (filename already taken).
        string? path = null;
        const int maxAttempts = 5;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            string candidate;
            try
            {
                candidate = BuildSpillPath();
            }
            catch
            {
                return $"Spilled=<unavailable> ({bytes} bytes; could not build spill path)";
            }

            try
            {
                using var fs = new FileStream(
                    candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                // #70: write payload bytes directly via the cached no-BOM
                // UTF8Encoding so on-disk length == GetByteCount(redacted) and
                // downstream Get-Content / log pipelines see plain UTF-8.
                var buf = Utf8NoBom.GetBytes(redacted);
                fs.Write(buf, 0, buf.Length);
                path = candidate;
                break;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                // Filename collision (or transient IO) — regenerate suffix and retry.
                continue;
            }
            catch
            {
                return $"Spilled=<unavailable> ({bytes} bytes; write failed)";
            }
        }

        if (path is null)
        {
            return $"Spilled=<unavailable> ({bytes} bytes; create-new retry exhausted)";
        }

        // Best-effort rotation. Failure here must not affect the just-written file's
        // visibility to the caller — we always return a successful summary.
        try { EnforceRotationCap(DefaultRotationCapBytes); } catch { /* swallow */ }

        return $"Spilled={path} ({bytes} bytes)";
    }

    /// <summary>
    /// Process-wide monotonic sequence counter used as a unique suffix on the
    /// spill filename so sub-second concurrent failures cannot collide
    /// (Code Review Gate 6 Blocker #3, #59).
    /// </summary>
    private static long _spillSequence;

    /// <summary>
    /// Build the per-failure spill path:
    /// <c>%TEMP%\hypervmcp-stderr-{utc-timestamp}-{pid}-{seq}.log</c>.
    /// Timestamp uses <c>yyyy-MM-ddTHH-mm-ssZ</c> (colon-free) so the value is a
    /// valid Windows filename component. <c>{seq}</c> is a monotonic process-wide
    /// counter incremented via <see cref="System.Threading.Interlocked.Increment(ref long)"/>;
    /// each call therefore produces a distinct path even within the same UTC second.
    /// </summary>
    internal static string BuildSpillPath()
    {
        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
        var pid = Environment.ProcessId;
        var seq = System.Threading.Interlocked.Increment(ref _spillSequence);
        return Path.Combine(
            TempPathProvider(),
            $"{SpillFilePrefix}{ts}-{pid}-{seq}{SpillFileSuffix}");
    }

    /// <summary>
    /// Defensive credential redaction. Mirrors the pattern set used by
    /// <c>PowerShellHost.RedactCredentialsDefensively</c> and
    /// <see cref="ErrorMapper.RedactCredentials"/>:
    /// <list type="bullet">
    ///   <item><description>Replace the literal value of
    ///     <c>HYPERV_MCP_VM_PASSWORD</c> (when set) with <c>***REDACTED***</c>.</description></item>
    ///   <item><description>Replace <c>ConvertTo-SecureString '...'</c> argument values.</description></item>
    ///   <item><description>Replace <c>-Credential ...</c> inline values.</description></item>
    /// </list>
    /// Visible to the assembly so tests can assert the secrets-never-on-disk invariant.
    /// </summary>
    internal static string RedactDefensively(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text;

        // 1. Literal HYPERV_MCP_VM_PASSWORD value (when configured).
        try
        {
            var pw = Environment.GetEnvironmentVariable(CredentialResolver.EnvVarPassword);
            if (!string.IsNullOrEmpty(pw))
            {
                result = result.Replace(pw, "***REDACTED***");
            }
        }
        catch { /* env-var access must never break logging */ }

        // 2. ConvertTo-SecureString argument values.
        // Code Review Gate 6 Blocker #4 (#59): the previous `'[^']*'` pattern
        // mishandled PowerShell single-quoted strings whose payload contains a
        // doubled single-quote (`''` = literal apostrophe), leaving part of the
        // password literal unredacted. Use the canonical PowerShell single-quoted
        // grammar `'(?:''|[^'])*'` — the SAME pattern used by
        // PowerShellExecutor.StraySecureStringRegex (PowerShellExecutor.cs:339).
        // Keep the two sites in sync if either grammar is updated.
        //
        // PR #67 review (copilot-pull-request-reviewer, comment 3179029488):
        // Use cached static Regex with RegexOptions.IgnoreCase | Multiline so that
        // case-variant cmdlet spellings (e.g. `convertto-securestring`) — which
        // PowerShell accepts — are still redacted. This matches the options on
        // PowerShellExecutor.StraySecureStringRegex.
        try
        {
            result = ConvertToSecureStringRegex.Replace(
                result, "ConvertTo-SecureString ***REDACTED***");
        }
        catch { /* regex compile / runaway must never break logging */ }

        // 3. -Credential parameter inline values.
        try
        {
            result = CredentialParamRegex.Replace(result, "-Credential [PSCredential]");
        }
        catch { /* swallow */ }

        return result;
    }

    /// <summary>
    /// Cached defensive redaction regex for inline
    /// <c>ConvertTo-SecureString '...'</c> literals. Mirrors options used by
    /// <c>PowerShellExecutor.StraySecureStringRegex</c>:
    /// <see cref="RegexOptions.Multiline"/> | <see cref="RegexOptions.IgnoreCase"/>.
    /// PowerShell cmdlet names are case-insensitive, so case-variant spellings
    /// (e.g. <c>convertto-securestring</c>) appearing in stderr/exception text
    /// must still be redacted to preserve the secrets-never-on-disk invariant.
    /// </summary>
    private static readonly Regex ConvertToSecureStringRegex = new(
        @"ConvertTo-SecureString\s+'(?:''|[^'])*'",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Cached defensive redaction regex for inline <c>-Credential &lt;value&gt;</c>
    /// arguments. Case-insensitive: PowerShell parameter names are case-insensitive.
    /// </summary>
    private static readonly Regex CredentialParamRegex = new(
        @"-Credential\s+\S+",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Enforce the soft rotation cap by deleting the oldest spill files first
    /// until the total size of the spill directory drops below
    /// <paramref name="capBytes"/>. Tolerates <see cref="IOException"/> on
    /// individual deletes and continues to the next candidate.
    /// </summary>
    internal static void EnforceRotationCap(long capBytes)
    {
        if (capBytes <= 0) return;

        IEnumerable<FileInfo> spills;
        try
        {
            var tempDir = new DirectoryInfo(TempPathProvider());
            if (!tempDir.Exists) return;
            spills = tempDir.EnumerateFiles(SpillFilePrefix + "*" + SpillFileSuffix);
        }
        catch
        {
            return;
        }

        // Materialize so we can compute total size, sort, and iterate without
        // re-enumerating.
        List<FileInfo> all;
        try
        {
            all = spills.ToList();
        }
        catch
        {
            return;
        }

        long total = 0;
        foreach (var f in all)
        {
            try { total += f.Length; }
            catch { /* swallow per-file refresh failures */ }
        }

        if (total <= capBytes) return;

        // Oldest-first deletion. CreationTimeUtc is preferred over LastWriteTime
        // because spill files are written once and never appended to.
        all.Sort((a, b) =>
        {
            DateTime aT, bT;
            try { aT = a.CreationTimeUtc; } catch { aT = DateTime.MinValue; }
            try { bT = b.CreationTimeUtc; } catch { bT = DateTime.MinValue; }
            return aT.CompareTo(bT);
        });

        foreach (var f in all)
        {
            if (total <= capBytes) break;
            long len = 0;
            try { len = f.Length; } catch { /* swallow */ }
            try
            {
                f.Delete();
                total -= len;
            }
            catch (IOException)
            {
                // DIAG-D6: tolerate individual delete failures (file in use, ACL,
                // etc.) and continue with the next candidate.
                continue;
            }
            catch
            {
                // Any other unexpected failure — keep going so one bad file does
                // not stall rotation forever.
                continue;
            }
        }
    }
}
