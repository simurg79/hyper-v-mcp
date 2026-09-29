using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #358 — runs the preparation helper's Python suite as part of the .NET run.
///
/// Nothing else invoked <c>scripts/test_prepare_ubuntu_autoinstall_iso.py</c>, so its guards
/// over the publication gate could rot unnoticed.
///
/// The repository root MUST come from <see cref="CallerFilePathAttribute"/>, not
/// <c>AppContext.BaseDirectory</c>: under <c>-p:BaseOutputPath=</c> the assembly runs outside
/// the working tree, the upward walk for the .sln finds nothing, and an earlier version of
/// this test went green without ever launching Python.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue358PreparationHelperPythonSuiteTests
{
    /// <summary>Emitted verbatim when the interpreter is absent, so a skip is greppable and never reads as a real run.</summary>
    private const string SkipMarker = "ISSUE358-PYTHON-SUITE-SKIPPED";

    /// <summary>Emitted verbatim only after the interpreter actually ran the suite to completion.</summary>
    private const string RanMarker = "ISSUE358-PYTHON-SUITE-EXECUTED";

    private readonly ITestOutputHelper output;

    public Issue358PreparationHelperPythonSuiteTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void PreparationHelperPythonSuite_Passes()
    {
        var suitePath = Path.Combine(RepositoryRoot(), "scripts", "test_prepare_ubuntu_autoinstall_iso.py");
        File.Exists(suitePath).Should().BeTrue(
            $"the wiring is only meaningful if it finds the suite; looked at {suitePath}");

        var interpreter = FindPythonInterpreter();
        if (interpreter is null)
        {
            // Deliberately NOT an assertion failure: the helper is a developer-run script and the
            // .NET suite must stay green on hosts without Python. The marker keeps a skip
            // distinguishable from a genuine run.
            output.WriteLine($"{SkipMarker}: no 'python' or 'python3' on PATH; the helper's guards were NOT verified by this run.");
            return;
        }

        var (exitCode, transcript) = RunSuite(interpreter, suitePath);

        // unittest always reports the case count; its absence means the interpreter died before
        // running anything, which MUST NOT pass as a clean exit.
        transcript.Should().MatchRegex(@"Ran [1-9]\d* tests?", $"the Python suite must actually execute cases:\n{transcript}");
        exitCode.Should().Be(0, $"the preparation helper's guards must hold:\n{transcript}");

        output.WriteLine($"{RanMarker}: {interpreter} exited 0.");
        output.WriteLine(transcript);
    }

    private static (int ExitCode, string Transcript) RunSuite(string interpreter, string suitePath)
    {
        using var process = Process.Start(new ProcessStartInfo(interpreter, $"\"{suitePath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(suitePath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        // Drain both pipes concurrently; unittest writes its report to stderr, and reading the
        // streams one after the other deadlocks once either pipe buffer fills.
        var transcript = new StringBuilder();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit(600_000).Should().BeTrue("the helper's Python suite must finish within ten minutes");
        transcript.Append(standardOutput.GetAwaiter().GetResult());
        transcript.Append(standardError.GetAwaiter().GetResult());

        return (process.ExitCode, transcript.ToString());
    }

    /// <summary>
    /// Resolves the working tree from this file's compile-time path, which survives
    /// <c>BaseOutputPath</c> redirection.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyperV.Mcp.Server.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"could not locate HyperV.Mcp.Server.sln above the compile-time source path '{thisFile}'");
    }

    private static string? FindPythonInterpreter()
    {
        foreach (var candidate in new[] { "python", "python3" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (probe is null) { continue; }
                probe.WaitForExit(15000);
                if (probe.ExitCode == 0) { return candidate; }
            }
            catch (Exception) { /* interpreter absent; try the next candidate */ }
        }
        return null;
    }
}
