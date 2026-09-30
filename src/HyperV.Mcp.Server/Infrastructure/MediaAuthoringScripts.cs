namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// The single definition of the IMAPI2FS image-authoring PowerShell, shared by the Windows
/// autounattend ISO and the Ubuntu NoCloud seed ISO.
///
/// WHY compiled C# and not a PowerShell-level cast: PowerShell cannot dispatch the COM
/// <c>IStream</c> members (<c>Read</c>/<c>Stat</c>/<c>Seek</c>) on the IMAPI2 <c>ImageStream</c> via
/// IDispatch — the call fails as a managed <c>MethodNotFound</c>, so a script cast is not
/// implementable. The injected type marshals against
/// <c>System.Runtime.InteropServices.ComTypes.IStream</c>, which has an RCW.
///
/// WHY one constant: two divergent copies of this workaround caused issue #292 — the Windows path
/// was fixed, the Ubuntu path kept the naive <c>.Read()</c> shape and shipped dead.
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D34.
/// </summary>
internal static class MediaAuthoringScripts
{
    /// <summary>IMAPI2 <c>FsiFileSystems</c>: ISO9660 + Joliet — what NoCloud seed media uses.</summary>
    internal const int FileSystemsIso9660Joliet = 3;

    /// <summary>IMAPI2 <c>FsiFileSystems</c>: UDF — what the Windows autounattend ISO uses.</summary>
    internal const int FileSystemsUdf = 4;

    /// <summary>
    /// The <c>PSTypeName</c> guard keeps repeated interpolation within one session from
    /// re-compiling or throwing.
    /// </summary>
    internal const string IStreamHelperInjection = @"
if (-not ([System.Management.Automation.PSTypeName]'IStreamHelper').Type) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class IStreamHelper
{
    public static void WriteStreamToFile(object comStream, string filePath)
    {
        IStream istream = (IStream)comStream;
        System.Runtime.InteropServices.ComTypes.STATSTG stat;
        istream.Stat(out stat, 0);
        long totalBytes = stat.cbSize;
        istream.Seek(0, 0, IntPtr.Zero);

        byte[] buffer = new byte[65536];
        long totalRead = 0;
        using (FileStream fs = File.Create(filePath))
        {
            while (totalRead < totalBytes)
            {
                int toRead = (int)Math.Min(buffer.Length, totalBytes - totalRead);
                IntPtr bytesReadPtr = Marshal.AllocHGlobal(4);
                try
                {
                    istream.Read(buffer, toRead, bytesReadPtr);
                    int bytesRead = Marshal.ReadInt32(bytesReadPtr);
                    if (bytesRead <= 0) break;
                    fs.Write(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                }
                finally
                {
                    Marshal.FreeHGlobal(bytesReadPtr);
                }
            }
        }
    }
}
'@ -Language CSharp
}
";

    /// <summary>
    /// Builds the IMAPI2FS authoring block. The <c>try</c>/<c>catch</c> is required so an authoring
    /// failure surfaces its own message instead of a bare terminal-output mismatch, and the
    /// <c>finally</c> release keeps COM teardown deterministic.
    /// </summary>
    /// <param name="sourceDirExpression">PowerShell expression yielding the staging directory.</param>
    /// <param name="outputPathExpression">PowerShell expression yielding the output .iso path.</param>
    /// <param name="volumeLabel">Volume label — <c>CIDATA</c> for NoCloud, <c>AUTOUNATTEND</c> for Windows.</param>
    /// <param name="fileSystemsToCreate">IMAPI2 <c>FsiFileSystems</c> flags (3 = ISO9660+Joliet, 4 = UDF).</param>
    /// <param name="failureMessagePrefix">Prefix for the rethrown authoring failure message.</param>
    internal static string BuildImapi2AuthoringBlock(
        string sourceDirExpression,
        string outputPathExpression,
        string volumeLabel,
        int fileSystemsToCreate,
        string failureMessagePrefix)
    {
        return $@"
try {{
{IStreamHelperInjection}
    $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $fsi.FileSystemsToCreate = {fileSystemsToCreate}
    $fsi.VolumeName = '{volumeLabel}'
    $fsi.Root.AddTree({sourceDirExpression}, $false)
    $resultImage = $fsi.CreateResultImage()
    $resultStream = $resultImage.ImageStream
    try {{
        [IStreamHelper]::WriteStreamToFile($resultStream, {outputPathExpression})
    }} finally {{
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($resultStream) | Out-Null
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($fsi) | Out-Null
    }}
}} catch {{
    throw ""{failureMessagePrefix}: $($_.Exception.Message)""
}}
";
    }
}
