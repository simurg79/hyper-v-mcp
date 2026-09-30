using System;
using System.IO;
using System.Linq;
using Xunit;
using HyperV.Mcp.Server.Tests.TestSupport;

namespace HyperV.Mcp.Server.Tests.Build;

/// <summary>Remove only unsigned Microsoft.PowerShell.Security; preserve Utility, Management and other intrinsic PS7 modules needed by the
/// Core runspace for Get-VMHost and routine dispatch.</summary>
public sealed class Issue66StripGlobTests
{
    [Fact]
    public void StripGlob_RemovesOnlyMicrosoftPowerShellSecurity_FromServerBin()
    {
        var modulesDir = LocateServerBinModulesDir();
        Assert.True(Directory.Exists(modulesDir),
            $"Server bin Modules directory missing at: {modulesDir}");

        // The unsigned bundled Security module must be gone.
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
        var serverBin = Path.GetDirectoryName(TestPaths.ServerExecutablePath())!;
        Assert.True(Directory.Exists(serverBin),
            $"Server bin not found at {serverBin}. Did the server project build " +
            $"before the test project?");

        // The SDK also ships legacy TFM directories; inspect the runtime's own module tree.
        var libRoot = Path.Combine(serverBin, "runtimes", "win", "lib");
        Assert.True(Directory.Exists(libRoot),
            $"Server bin runtimes/win/lib missing at {libRoot}");
        var frameworkVersion = typeof(HyperV.Mcp.Server.Program).Assembly
            .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            .Cast<System.Runtime.Versioning.TargetFrameworkAttribute>().Single().FrameworkName;
        var version = new System.Runtime.Versioning.FrameworkName(frameworkVersion).Version;
        var libTfm = $"net{version.Major}.{version.Minor}";
        var netDir = Path.Combine(libRoot, libTfm);
        Assert.True(Directory.Exists(netDir),
            $"Server bin runtimes/win/lib/{libTfm} missing at {netDir}. " +
            $"Available: {string.Join(", ", Directory.EnumerateDirectories(libRoot).Select(Path.GetFileName))}");
        return Path.Combine(netDir, "Modules");
    }
}
