using System.Reflection;
using System.Runtime.CompilerServices;

namespace HyperV.Mcp.Server.Tests.TestSupport;

internal static class TestPaths
{
    public static string RepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyperV.Mcp.Server.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Cannot locate repository above source path '{sourcePath}'.");
    }

    public static string ServerExecutablePath()
    {
        // Shared OutputPath builds merge test dependencies into server output; allow a separate server build.
        var executablePath = Environment.GetEnvironmentVariable("HYPERV_MCP_TEST_SERVER_EXE");
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            var targetPath = typeof(TestPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "TestServerTargetPath").Value!;
            executablePath = Path.ChangeExtension(targetPath, ".exe");
        }
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Build the server before running process integration tests.", executablePath);
        }
        return executablePath;
    }
}
