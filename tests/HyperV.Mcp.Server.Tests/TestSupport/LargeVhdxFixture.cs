using System.Diagnostics;
using System.Runtime.Versioning;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>
/// VC-D10 (Issue #169) test fixture: provisions a ≥1 GiB synthetic VHDX under
/// <see cref="Path.GetTempPath"/> for use by cold-start latency integration tests.
///
/// <para>
/// Two creation strategies are attempted, in order:
/// </para>
/// <list type="number">
///   <item>
///   <description>
///   Hyper-V's <c>New-VHD</c> cmdlet via <c>pwsh -NoProfile -c "New-VHD -Path … -SizeBytes 1GB -Dynamic"</c>.
///   This produces a real VHDX header that VC-D10 prefers. Requires the
///   Hyper-V management module on the test host (vmms service running).
///   </description>
///   </item>
///   <item>
///   <description>
///   Plain-file fallback: when <c>New-VHD</c> is unavailable (Hyper-V module
///   absent), a non-sparse 1 GiB file is produced by writing random bytes via
///   <see cref="FileStream.Write(System.ReadOnlySpan{byte})"/> in 1 MiB chunks
///   and flushed-to-disk. The random payload defeats filesystem sparse-file
///   optimizations so cold-I/O latency tests exercise real disk reads. This is
///   sufficient for the SHA-256 cache contract (the cache reads <em>bytes</em>;
///   it does not parse a VHDX header) and lets the latency test run on hosts
///   where Hyper-V isn't installed (e.g., CI agents with the .NET SDK only).
///   </description>
///   </item>
/// </list>
///
/// <para>
/// On <see cref="Dispose"/> the file is best-effort deleted; lock errors are
/// swallowed so test teardown never throws.
/// </para>
///
/// <para>
/// <see cref="IsRealVhdx"/> records whether the New-VHD path succeeded. Callers
/// that strictly require a real Hyper-V VHDX can inspect it and skip when
/// <see langword="false"/>. The cache integration tests in <c>Issue169ColdStartLatencyTests</c>
/// treat the fallback as acceptable because the cache contract is byte-stream
/// driven.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LargeVhdxFixture : IDisposable
{
    /// <summary>Synthetic VHDX target size: 1 GiB.</summary>
    public const long TargetSizeBytes = 1L * 1024L * 1024L * 1024L;

    /// <summary>Full path to the provisioned synthetic VHDX file.</summary>
    public string Path { get; }

    /// <summary>
    /// <see langword="true"/> when the fixture was created via Hyper-V's
    /// <c>New-VHD</c>; <see langword="false"/> when the plain-file fallback
    /// was used.
    /// </summary>
    public bool IsRealVhdx { get; }

    /// <summary>
    /// Captured stderr from the <c>New-VHD</c> attempt, useful for diagnostics
    /// when the fixture fell back to the plain-file path. <see langword="null"/>
    /// when <c>New-VHD</c> was never tried (non-Windows) or succeeded cleanly.
    /// </summary>
    public string? NewVhdDiagnostic { get; }

    private bool _disposed;

    /// <summary>
    /// Provisions a synthetic VHDX file. Throws only on catastrophic IO
    /// failure of the fallback path; the New-VHD failure mode is captured into
    /// <see cref="NewVhdDiagnostic"/> and the constructor falls back rather
    /// than throwing.
    /// </summary>
    public LargeVhdxFixture()
    {
        var dir = System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(
            dir,
            "issue169-vhdx-" + Guid.NewGuid().ToString("N") + ".vhdx");

        var (ok, stderr) = TryCreateViaNewVhd(Path);
        if (ok && File.Exists(Path) && new FileInfo(Path).Length >= TargetSizeBytes / 2)
        {
            IsRealVhdx = true;
            NewVhdDiagnostic = null;
            return;
        }

        NewVhdDiagnostic = stderr;

        // 🟡 #4 (Issue #169 Gate 6 remediation): Fallback must produce a
        // *non-sparse* 1 GiB file so cold-I/O tests measure real read latency.
        // The previous SetLength + single-byte-touch approach left the file
        // mostly-sparse on NTFS — the OS would skip reading any extents that
        // were never written, so SHA-256 cold-reads completed in milliseconds
        // and the cold-start latency assertion lost its meaning.
        //
        // Strategy: write the file in 1 MiB random-byte chunks until length
        // ≥ TargetSizeBytes. Each chunk forces NTFS to allocate real extents,
        // and the random content defeats any compression / dedup short-circuit
        // that an underlying storage layer might otherwise apply.
        try
        {
            const int chunkBytes = 1 * 1024 * 1024; // 1 MiB
            var buffer = new byte[chunkBytes];
            var rng = new Random();
            using var fs = new FileStream(
                Path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: chunkBytes,
                useAsync: false);
            long written = 0;
            while (written < TargetSizeBytes)
            {
                var remaining = TargetSizeBytes - written;
                var toWrite = (int)Math.Min(chunkBytes, remaining);
                rng.NextBytes(buffer);
                fs.Write(buffer, 0, toWrite);
                written += toWrite;
            }
            // Flush so the on-disk size reflects what callers will hash.
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(Path);
            throw;
        }
        IsRealVhdx = false;
    }

    private static (bool Ok, string? Stderr) TryCreateViaNewVhd(string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "non-Windows host");
        }

        // Prefer pwsh; fall back to Windows PowerShell. Either is acceptable.
        foreach (var exe in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"-NoProfile -NonInteractive -Command \"New-VHD -Path '{targetPath}' -SizeBytes 1GB -Dynamic | Out-Null\"",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    continue;
                }

                if (!proc.WaitForExit(60_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* swallow */ }
                    continue;
                }

                var stderr = proc.StandardError.ReadToEnd();
                if (proc.ExitCode == 0)
                {
                    return (true, null);
                }
                return (false, $"{exe} exit={proc.ExitCode}: {stderr.Trim()}");
            }
            catch (Exception ex)
            {
                // Try next executable.
                _ = ex;
            }
        }
        return (false, "New-VHD not available (Hyper-V module absent)");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort — swallow lock errors.
        }
    }

    /// <summary>Best-effort delete of the synthetic VHDX. Never throws.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TryDelete(Path);
        GC.SuppressFinalize(this);
    }
}
