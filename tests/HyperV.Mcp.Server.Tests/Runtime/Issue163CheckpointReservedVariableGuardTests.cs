using System.Text.RegularExpressions;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #163 (SEV-1; smoke-run #266): the composed create/merge PowerShell scripts
/// assigned to <c>$pid</c>, which case-insensitively collides with PowerShell's
/// read-only automatic variable <c>$PID</c> — every vm_checkpoint create failed with
/// "Cannot overwrite variable PID because it is read-only or constant."
///
/// The mocked #206 suite captured the composed script but never executed it, so the
/// reserved-variable clash was invisible. This un-gated guard closes that gap WITHOUT a
/// live Hyper-V host: it captures the composed script text via the mock executor seam and
/// statically asserts it never assigns to the reserved/automatic variables on the allowlist
/// below (PID, PSItem, _, Matches, Host, true, false, null, error) — not every automatic
/// variable — which is sufficient to catch the $pid regression and its closest neighbors.
/// Fails PRE-fix (the old <c>foreach ($pid ...)</c> / <c>$preIds.Add($pid)</c> text) and
/// passes POST-fix (<c>$parentId</c> / <c>$preIdEntry</c>).
/// </summary>
[Trait("Category", "Runtime")]
public class Issue163CheckpointReservedVariableGuardTests
{
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";
    private const string LocalHostId = "local";

    // A write is an ASSIGNMENT to the variable or its binding in a foreach header —
    // '$pid ='  or  'foreach ($pid in ...)'. A read such as $PID.ToString() is legal.
    // (?<![\w]) / (?![\w]) enforce PowerShell's identifier word-boundaries so we do not
    // match longer names like $pidList or $parentId.
    private static readonly Regex ReservedWriteRegex = new(
        @"(?<![\w])\$(PID|PSITEM|_|MATCHES|HOST|TRUE|FALSE|NULL|ERROR)(?![\w])\s*(=(?!=)|(?=\s+in\b))"
        + @"|foreach\s*\(\s*\$(PID|PSITEM|MATCHES|HOST|TRUE|FALSE|NULL|ERROR)(?![\w])\s+in\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static CheckpointManager BuildManager(out List<string> capturedScripts, out Mock<IPowerShellExecutor> mockExecutor)
    {
        var scripts = new List<string>();
        capturedScripts = scripts;
        mockExecutor = new Mock<IPowerShellExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => scripts.Add(s))
            .ReturnsAsync(new PowerShellResult { ExitCode = 0, Stdout = "{}", Stderr = string.Empty, DurationMs = 1 });

        var options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [LocalHostId] = new HostProfile
                {
                    HostId = LocalHostId,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                },
            },
        };

        return new CheckpointManager(
            mockExecutor.Object,
            new HostResolver(options),
            new Mock<ISessionStore>().Object,
            new Mock<ILogger<CheckpointManager>>().Object);
    }

    [Fact]
    public async Task ComposedCreateScript_DoesNotWriteReservedVariable()
    {
        var manager = BuildManager(out var capturedScripts, out _);

        // The create envelope parse rejects the empty "{}" stdout; we only care about the
        // composed script text captured before the throw. Swallow the expected failure.
        try { await manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1"); }
        catch { /* envelope parse throws on stub stdout; script capture is what matters */ }

        capturedScripts.Should().NotBeEmpty("the create flow must compose at least one script");
        AssertNoReservedWrite(capturedScripts, "create");
    }

    [Fact]
    public async Task ComposedMergeScript_DoesNotWriteReservedVariable()
    {
        var manager = BuildManager(out var capturedScripts, out _);

        await manager.MergeAllAsync(LocalHostId, TestVmId);

        capturedScripts.Should().NotBeEmpty("the merge flow must compose a script");
        AssertNoReservedWrite(capturedScripts, "merge");
    }

    private static void AssertNoReservedWrite(IEnumerable<string> scripts, string flow)
    {
        foreach (var script in scripts)
        {
            var match = ReservedWriteRegex.Match(script);
            match.Success.Should().BeFalse(
                $"the composed {flow} script must never ASSIGN to a PowerShell read-only automatic "
                + $"variable (Issue #163). Offending token: '{match.Value.Trim()}'. "
                + "Rename the local (e.g. $pid -> $parentId / $preIdEntry).");
        }
    }
}
