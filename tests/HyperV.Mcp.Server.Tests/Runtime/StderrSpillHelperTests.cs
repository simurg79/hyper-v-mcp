using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #59 / DIAG-D6 — <see cref="StderrSpillHelper"/> behavior.
/// See /myplans/diagnostics/diagnostics-design.md — DIAG-D6.
/// See https://github.com/simurg79/hyper-v-mcp-server/issues/59.
///
/// Verifies the contract:
/// <list type="bullet">
///   <item>Spill writes a <c>hypervmcp-stderr-*.log</c> file under %TEMP%.</item>
///   <item>Defensive credential redaction strips <c>HYPERV_MCP_VM_PASSWORD</c>
///         literal values and the standard PowerShell credential patterns.</item>
///   <item>The summary string contains the spill path and the byte length.</item>
///   <item>Rotation deletes oldest-first and tolerates one file held open.</item>
///   <item>The helper never throws even on best-effort failure paths.</item>
/// </list>
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public class StderrSpillHelperTests
{
    /// <summary>
    /// A successful spill writes the redacted content to %TEMP% under the
    /// canonical filename pattern and returns a summary that names the file.
    /// </summary>
    [Fact]
    public void Spill_WritesFile_Under_Temp_With_Expected_Pattern()
    {
        var content = "raw stderr buffer line 1\nraw stderr buffer line 2";

        var summary = StderrSpillHelper.Spill(content);

        summary.Should().StartWith("Spilled=", "summary must begin with the canonical 'Spilled=' marker.");
        summary.Should().NotContain("<unavailable>", "the happy path must not produce the failure marker.");

        var path = ExtractSpillPath(summary);
        try
        {
            path.Should().NotBeNullOrEmpty();
            File.Exists(path!).Should().BeTrue("Spill must create the file on disk.");

            Path.GetFileName(path!).Should().StartWith(StderrSpillHelper.SpillFilePrefix);
            Path.GetFileName(path!).Should().EndWith(StderrSpillHelper.SpillFileSuffix);
            Path.GetDirectoryName(path!)!.TrimEnd(Path.DirectorySeparatorChar)
                .Should().Be(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));

            // The summary reports the redacted content's UTF-8 byte count, which
            // — per #70 — must equal the on-disk file length (UTF-8 no BOM).
            var redactedByteCount = System.Text.Encoding.UTF8.GetByteCount(content);
            summary.Should().Contain($"({redactedByteCount} bytes)",
                "summary must report the redacted content's UTF-8 byte count.");
            File.ReadAllText(path!).Should().Contain("raw stderr buffer line 1");

            // #70: spill files must be plain UTF-8 with NO byte-order-mark so
            // downstream Get-Content / log pipelines on Windows see the raw
            // payload, and so the summary's (N bytes) matches FileInfo.Length.
            var onDisk = File.ReadAllBytes(path!);
            onDisk.Length.Should().BeGreaterOrEqualTo(3,
                "fixture content is well over 3 bytes; sanity check before BOM assertion.");
            (onDisk[0] == 0xEF && onDisk[1] == 0xBB && onDisk[2] == 0xBF)
                .Should().BeFalse("#70: spill file must not start with the UTF-8 BOM EF BB BF.");
            new FileInfo(path!).Length.Should().Be(redactedByteCount,
                "#70: on-disk file length must equal the summary's reported byte count (UTF-8 no BOM).");
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// Spill must redact the HYPERV_MCP_VM_PASSWORD literal value and the
    /// ConvertTo-SecureString / -Credential PowerShell patterns before any byte
    /// touches disk. Asserts the secrets-never-on-disk invariant.
    /// </summary>
    [Fact]
    public void Spill_Redacts_Password_And_PSCredential_Patterns()
    {
        const string secret = "Sup3rS3cret!UNIQUE-7af31e";
        var origPw = Environment.GetEnvironmentVariable(CredentialResolver.EnvVarPassword);
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarPassword, secret);
        string? path = null;
        try
        {
            var content =
                $"failure log: ConvertTo-SecureString '{secret}' -AsPlainText -Force\n" +
                $"-Credential {secret}\n" +
                $"raw secret echoed: {secret}";

            var summary = StderrSpillHelper.Spill(content);
            path = ExtractSpillPath(summary);
            path.Should().NotBeNullOrEmpty();
            File.Exists(path!).Should().BeTrue();

            var written = File.ReadAllText(path!);
            written.Should().NotContain(secret,
                "DIAG-D6: the literal HYPERV_MCP_VM_PASSWORD value must never appear on disk.");
            written.Should().Contain("***REDACTED***");
            written.Should().NotContain($"ConvertTo-SecureString '{secret}'",
                "ConvertTo-SecureString single-quoted argument must be redacted.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialResolver.EnvVarPassword, origPw);
            TryDelete(path);
        }
    }

    /// <summary>
    /// The summary string contains both the spill path and the byte length so an
    /// operator can grep the server logs and immediately locate the on-disk artifact.
    /// </summary>
    [Fact]
    public void Spill_Summary_Contains_Path_And_ByteLength()
    {
        var content = new string('x', 4096);

        var summary = StderrSpillHelper.Spill(content);
        var path = ExtractSpillPath(summary);
        try
        {
            summary.Should().Contain(path!);
            summary.Should().MatchRegex(@"\(\d+ bytes\)");
            summary.Should().Contain("(4096 bytes)");
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// Rotation deletes oldest-first when the cap is exceeded and tolerates one
    /// file held open via <see cref="IOException"/> — the rotation must continue,
    /// not throw, and must still drop the directory's measured size.
    /// </summary>
    [Fact]
    public void EnforceRotationCap_DeletesOldestFirst_AndTolerates_LockedFile()
    {
        // Issue #68: isolate this rotation fixture from the real %TEMP% by
        // pointing StderrSpillHelper.TempPathProvider at a unique per-test
        // subdirectory. This prevents collateral deletion of unrelated
        // hypervmcp-stderr-*.log files that a developer or concurrent test run
        // may have legitimately produced under %TEMP%, and makes the test
        // deterministic enough to assert exact remaining-count math.
        var isolatedDir = Path.Combine(
            Path.GetTempPath(),
            "hvmcp-spill-rot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedDir);

        var originalProvider = StderrSpillHelper.TempPathProvider;
        StderrSpillHelper.TempPathProvider = () => isolatedDir;

        const long capBytes = 8 * 1024L; // 8KB cap for the test
        const int fileSize = 4 * 1024;   // each fixture file = exactly 4KB
        const int fixtureCount = 6;
        // Total fixture size = 24KB; cap = 8KB; locked oldest (4KB) survives,
        // so the helper must delete fixtures[1..N] in order until the remaining
        // total drops to ≤ 8KB. With the locked file (4KB) counted in `total`,
        // the helper needs to free 24KB - 8KB = 16KB → delete 4 of the 5
        // unlocked oldest fixtures → exactly 1 unlocked fixture (the newest)
        // remains, plus the locked one = 2 files total.
        const int expectedRemainingUnlocked = 1;

        var fixtures = new List<string>();
        FileStream? heldOpen = null;
        try
        {
            for (int i = 0; i < fixtureCount; i++)
            {
                var p = Path.Combine(
                    isolatedDir,
                    StderrSpillHelper.SpillFilePrefix +
                    "rotationtest-" + i + "-" + Guid.NewGuid().ToString("N") +
                    StderrSpillHelper.SpillFileSuffix);
                File.WriteAllBytes(p, new byte[fileSize]);
                File.SetCreationTimeUtc(p, DateTime.UtcNow.AddMinutes(-100 + i));
                fixtures.Add(p);
            }

            // Hold the OLDEST file open with FileShare.None so EnforceRotationCap
            // hits an IOException trying to delete it. The helper must continue.
            var lockedPath = fixtures[0];
            heldOpen = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

            // Act
            var act = () => StderrSpillHelper.EnforceRotationCap(capBytes);
            act.Should().NotThrow("DIAG-D6: rotation must tolerate per-file IOException.");

            // The locked file must still exist (delete failed, helper moved on).
            File.Exists(lockedPath).Should().BeTrue(
                "the file held open with FileShare.None must survive rotation.");

            // Now that the directory is isolated to this test's fixtures, we can
            // assert the EXACT count of surviving unlocked fixtures rather than a
            // loose `BeLessThan(5)`. The newest (fixtures[N-1]) must survive,
            // every middle fixture must be deleted.
            var survivingUnlocked = fixtures.Skip(1).Where(File.Exists).ToList();
            survivingUnlocked.Should().HaveCount(expectedRemainingUnlocked,
                "rotation must delete oldest-first until total ≤ cap, leaving exactly the newest unlocked fixture.");
            survivingUnlocked.Should().ContainSingle().Which.Should().Be(
                fixtures[^1],
                "the single unlocked survivor must be the NEWEST fixture (oldest-first deletion order).");

            // And no surprise files appeared in the isolated dir.
            Directory.EnumerateFiles(isolatedDir,
                StderrSpillHelper.SpillFilePrefix + "*" + StderrSpillHelper.SpillFileSuffix)
                .Should().HaveCount(2,
                    "exactly the locked oldest fixture and the newest unlocked fixture must remain.");
        }
        finally
        {
            heldOpen?.Dispose();
            StderrSpillHelper.TempPathProvider = originalProvider;
            try { Directory.Delete(isolatedDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// DIAG-D6 best-effort guarantee: <see cref="StderrSpillHelper.Spill"/> never
    /// throws, even when handed pathological input. Returns a summary string in
    /// every branch.
    /// </summary>
    [Fact]
    public void Spill_NeverThrows_OnNullOrEmpty_Input()
    {
        var act1 = () => StderrSpillHelper.Spill(null);
        var act2 = () => StderrSpillHelper.Spill(string.Empty);
        var act3 = () => StderrSpillHelper.Spill(new string('z', 1024 * 64));

        act1.Should().NotThrow();
        act2.Should().NotThrow();
        act3.Should().NotThrow();

        // And: each summary is a non-empty string that contains "Spilled=".
        var s1 = StderrSpillHelper.Spill(null);
        var s2 = StderrSpillHelper.Spill(string.Empty);
        s1.Should().Contain("Spilled=");
        s2.Should().Contain("Spilled=");

        TryDelete(ExtractSpillPath(s1));
        TryDelete(ExtractSpillPath(s2));
    }

    /// <summary>
    /// EnforceRotationCap must never throw — even on cap=0 / negative or when
    /// the temp dir contains only unrelated files.
    /// </summary>
    [Fact]
    public void EnforceRotationCap_NeverThrows_On_EdgeInputs()
    {
        var act0 = () => StderrSpillHelper.EnforceRotationCap(0);
        var actNeg = () => StderrSpillHelper.EnforceRotationCap(-1);
        var actHuge = () => StderrSpillHelper.EnforceRotationCap(long.MaxValue);

        act0.Should().NotThrow();
        actNeg.Should().NotThrow();
        actHuge.Should().NotThrow();
    }

    /// <summary>
    /// Code Review Gate 6 Blocker #3 (#59): launching ≥5 concurrent <see cref="StderrSpillHelper.Spill(string?)"/>
    /// calls within the same UTC second must produce ≥5 distinct on-disk files.
    /// Before the fix, two spills colliding on <c>{utc-timestamp}-{pid}</c> would
    /// silently overwrite each other via <c>File.WriteAllText</c>; the new
    /// <c>FileMode.CreateNew</c> + <see cref="System.Threading.Interlocked"/> sequence
    /// guarantees uniqueness. Asserts no overwrites and the new filename shape.
    /// </summary>
    [Fact]
    public async Task Spill_Concurrent_Calls_Produce_Distinct_Files_NoOverwrite()
    {
        const int concurrency = 8;
        var tasks = new Task<string>[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            int idx = i;
            tasks[i] = Task.Run(() => StderrSpillHelper.Spill(
                $"concurrent-spill-payload-{idx}-{Guid.NewGuid():N}"));
        }

        var summaries = await Task.WhenAll(tasks);
        var paths = summaries.Select(ExtractSpillPath).Where(p => !string.IsNullOrEmpty(p)).ToList()!;
        try
        {
            paths.Should().HaveCount(concurrency,
                "every concurrent Spill() call must report a non-null spill path.");
            paths.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(concurrency,
                "Gate 3 re-loop Blocker #3: concurrent spills must produce distinct paths (Interlocked sequence segment).");

            foreach (var p in paths)
            {
                File.Exists(p!).Should().BeTrue(
                    $"each concurrent spill file must persist on disk (no CreateNew overwrite): {p}");
                var name = Path.GetFileName(p!);
                // Shape: hypervmcp-stderr-{utc}-{pid}-{seq}.log → at least 4 dash-separated tail segments.
                name.Should().StartWith(StderrSpillHelper.SpillFilePrefix);
                name.Should().EndWith(StderrSpillHelper.SpillFileSuffix);
                name.Should().MatchRegex(
                    @"^hypervmcp-stderr-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z-\d+-\d+\.log$",
                    "filename must include the new {utc}-{pid}-{seq} suffix triple.");
            }
        }
        finally
        {
            foreach (var p in paths) TryDelete(p);
        }
    }

    /// <summary>
    /// Code Review Gate 6 Blocker #4 (#59): the redaction regex for
    /// <c>ConvertTo-SecureString '...'</c> previously used <c>'[^']*'</c>, which
    /// stops at the first apostrophe and therefore mishandles PowerShell single-
    /// quoted strings whose payload contains the doubled-apostrophe escape
    /// (<c>''</c> = literal <c>'</c>). The fix uses the canonical PS grammar
    /// <c>'(?:''|[^'])*'</c>. This test gives the helper a stderr buffer
    /// containing a doubled-apostrophe password literal and asserts that NO
    /// fragment of the secret survives on disk.
    /// </summary>
    [Fact]
    public void Spill_Redacts_Apostrophe_Containing_PSCredential_String()
    {
        // PowerShell single-quoted literal for the password P@s'word is 'P@s''word'.
        // Pre-fix: greedy `'[^']*'` matched only `'P@s'` and left `word' -AsPlainText -Force` on disk.
        const string content =
            "ERROR (host pipeline): ConvertTo-SecureString 'P@s''word' -AsPlainText -Force\n" +
            "stack trace continues...";

        var summary = StderrSpillHelper.Spill(content);
        var path = ExtractSpillPath(summary);
        try
        {
            path.Should().NotBeNullOrEmpty();
            File.Exists(path!).Should().BeTrue();

            var written = File.ReadAllText(path!);

            // Hard secrets-never-on-disk invariant:
            written.Should().NotContain("P@s",
                "Gate 3 re-loop Blocker #4: no fragment of the apostrophe-containing password may remain after redaction.");
            written.Should().NotContain("word'",
                "the trailing apostrophe-fragment of the doubled-quote literal must also be redacted.");
            written.Should().NotContain("P@s''word",
                "the full doubled-apostrophe literal must not appear on disk.");

            // And the redaction marker must be present in place of the literal.
            written.Should().Contain("ConvertTo-SecureString ***REDACTED***",
                "the canonical redaction marker must replace the entire single-quoted argument.");
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string? ExtractSpillPath(string summary)
    {
        // Format: "Spilled={path} ({bytes} bytes)"
        const string prefix = "Spilled=";
        var start = summary.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = summary.LastIndexOf(" (", StringComparison.Ordinal);
        if (end <= start) return null;
        return summary.Substring(start, end - start);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
