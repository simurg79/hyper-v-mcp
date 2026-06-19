using System;
using System.IO;
using System.Linq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Build;

/// <summary>
/// Regression tests for issue #66: the post-build strip target in
/// HyperV.Mcp.Server.csproj must remove ONLY Microsoft.PowerShell.Security
/// (RC-10.3b Code-Integrity invariant) and MUST preserve Utility / Management
/// (and the other intrinsic PS7 modules) which the in-proc Core runspace needs
/// for the Get-VMHost startup probe and routine cmdlet dispatch.
/// </summary>
public sealed class Issue66StripGlobTests
{
    [Fact]
    public void StripGlob_RemovesOnlyMicrosoftPowerShellSecurity_FromServerBin()
    {
        var modulesDir = LocateServerBinModulesDir();
        Assert.True(Directory.Exists(modulesDir),
            $"Server bin Modules directory missing at: {modulesDir}");

        // RC-10.3b invariant — the unsigned bundled Security module must be gone.
        var securityDir = Path.Combine(modulesDir, "Microsoft.PowerShell.Security");
        Assert.False(Directory.Exists(securityDir),
            $"RC-10.3b invariant violated: Microsoft.PowerShell.Security must NOT " +
            $"be present in server bin (Code-Integrity rejection of unsigned " +
            $"Security.types.ps1xml). Found at: {securityDir}");

        // Issue #66 regression — manifest files, not just empty directories.
        var utilityManifest = Path.Combine(modulesDir,
            "Microsoft.PowerShell.Utility", "Microsoft.PowerShell.Utility.psd1");
        Assert.True(File.Exists(utilityManifest),
            $"Issue #66 regression: Microsoft.PowerShell.Utility manifest missing " +
            $"at {utilityManifest}. The in-proc PS7 (Core) runspace requires " +
            $"Select-Object from this module for the Get-VMHost startup probe.");

        var managementManifest = Path.Combine(modulesDir,
            "Microsoft.PowerShell.Management", "Microsoft.PowerShell.Management.psd1");
        Assert.True(File.Exists(managementManifest),
            $"Issue #66 regression: Microsoft.PowerShell.Management manifest " +
            $"missing at {managementManifest}.");
    }

    private static string LocateServerBinModulesDir()
    {
        // Test assembly path:
        //   <repo>/tests/HyperV.Mcp.Server.Tests/bin/<config>/net8.0-windows/HyperV.Mcp.Server.Tests.dll
        var testAsmDir = Path.GetDirectoryName(
            typeof(Issue66StripGlobTests).Assembly.Location)!;
        var tfm = Path.GetFileName(testAsmDir);                              // net8.0-windows
        var config = Path.GetFileName(Path.GetDirectoryName(testAsmDir))!;   // Debug | Release

        // Walk up from the test assembly directory to the repo root, identified
        // by the presence of HyperV.Mcp.Server.sln. Mirrors the BaseDirectory
        // walk convention used by PowerShellHostTests around line 1193.
        var dir = new DirectoryInfo(testAsmDir);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "HyperV.Mcp.Server.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var serverBin = Path.Combine(dir!.FullName,
            "src", "HyperV.Mcp.Server", "bin", config, tfm);
        Assert.True(Directory.Exists(serverBin),
            $"Server bin not found at {serverBin}. Did the server project build " +
            $"before the test project?");

        // runtimes/win/lib/<tfm>/Modules — the PS SDK runtime asset emits multiple
        // sibling TFM directories under runtimes/win/lib (e.g. net8.0 alongside a
        // legacy netcoreapp2.1 set). The bundled Modules tree the in-proc PS7 runspace
        // actually loads from — and that the StripBundledMicrosoftPowerShellModules
        // target operates on — lives under the modern TFM matching the project's
        // <TargetFramework>. Pick that one explicitly rather than globbing net*.
        var libRoot = Path.Combine(serverBin, "runtimes", "win", "lib");
        Assert.True(Directory.Exists(libRoot),
            $"Server bin runtimes/win/lib missing at {libRoot}");
        // tfm here is e.g. "net8.0-windows"; runtimes/win/lib uses the platform-neutral
        // moniker (e.g. "net8.0"). Strip the "-windows" suffix if present.
        var libTfm = tfm.Split('-')[0];
        var netDir = Path.Combine(libRoot, libTfm);
        Assert.True(Directory.Exists(netDir),
            $"Server bin runtimes/win/lib/{libTfm} missing at {netDir}. " +
            $"Available: {string.Join(", ", Directory.EnumerateDirectories(libRoot).Select(Path.GetFileName))}");
        return Path.Combine(netDir, "Modules");
    }
}
