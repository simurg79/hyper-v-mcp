using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>
/// VC-D10 (Issue #169): host-OS page-cache eviction helper. The cold-start
/// latency test wants to demonstrate that, after warm-on-init has populated
/// <c>BaseImageHashCache</c>, a subsequent <c>vm_create</c> does NOT re-read
/// the ≥1 GiB base VHDX from disk. To make that demonstration honest we want
/// the underlying OS page cache to be cold so the *only* fast path available
/// is the in-process hash cache.
///
/// <para>
/// Two best-effort eviction strategies are attempted, in order:
/// </para>
/// <list type="number">
///   <item><description>
///   <c>kernel32!SetSystemFileCacheSize(uint.MaxValue, uint.MaxValue, 0)</c> —
///   widely-quoted Windows pattern for purging the working set of the system
///   file cache. Requires <c>SeIncreaseQuotaPrivilege</c> (administrator) on
///   most builds.
///   </description></item>
///   <item><description>
///   <c>ntdll!NtSetSystemInformation(SystemMemoryListInformation = 0x50,
///   MemoryPurgeStandbyList = 4)</c> — empties the standby list. Requires
///   <c>SeProfileSingleProcessPrivilege</c> (administrator).
///   </description></item>
/// </list>
///
/// <para>
/// Both calls fail-soft: an elevation denial, a missing entry-point, or any
/// other error logs to <see cref="Console.Error"/> and <see cref="TryEvictPageCache"/>
/// returns <see langword="false"/>. The latency test treats the return value
/// as advisory: when eviction was skipped the throughput precondition is
/// downgraded to best-effort per VC-D10, and the 5 s latency budget remains
/// the primary pass criterion.
/// </para>
/// </summary>
public static class ColdCachePrimer
{
    /// <summary>
    /// Attempts to evict the host OS page cache for <paramref name="path"/>'s
    /// volume. Returns <see langword="true"/> when at least one of the two
    /// eviction strategies reported success; <see langword="false"/> otherwise
    /// (insufficient privileges, non-Windows, or both calls failed).
    /// </summary>
    /// <param name="path">A path on the volume whose cache should be cooled.
    /// Currently only used to validate input and log diagnostics; the eviction
    /// itself is global to the system cache.</param>
    public static bool TryEvictPageCache(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ColdCachePrimer: non-Windows host — eviction skipped.");
            return false;
        }

        var any = false;
        any |= TrySetSystemFileCacheSize();
        any |= TryEmptyStandbyList();

        if (!any)
        {
            Console.Error.WriteLine(
                "ColdCachePrimer: no eviction strategy succeeded for '" + path +
                "' — assume warm page cache. (Run elevated to enable eviction.)");
        }
        return any;
    }

    [SupportedOSPlatform("windows")]
    private static bool TrySetSystemFileCacheSize()
    {
        try
        {
            // (uint.MaxValue, uint.MaxValue, 0) is the documented incantation
            // for "drop the resident working set".
            var ok = SetSystemFileCacheSize(
                new IntPtr(-1),
                new IntPtr(-1),
                0);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                Console.Error.WriteLine(
                    $"ColdCachePrimer: SetSystemFileCacheSize failed, GetLastError={err} (likely needs elevation).");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ColdCachePrimer: SetSystemFileCacheSize threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryEmptyStandbyList()
    {
        try
        {
            int command = MemoryPurgeStandbyList;
            var status = NtSetSystemInformation(
                SystemMemoryListInformation,
                ref command,
                sizeof(int));
            if (status != 0)
            {
                Console.Error.WriteLine(
                    $"ColdCachePrimer: NtSetSystemInformation(SystemMemoryListInformation) NTSTATUS=0x{status:X} (likely needs elevation).");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ColdCachePrimer: NtSetSystemInformation threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemFileCacheSize(
        IntPtr minimumFileCacheSize,
        IntPtr maximumFileCacheSize,
        uint flags);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(
        int systemInformationClass,
        ref int systemInformation,
        int systemInformationLength);
}
