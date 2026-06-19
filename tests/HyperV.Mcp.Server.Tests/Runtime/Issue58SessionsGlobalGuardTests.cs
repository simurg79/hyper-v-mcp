using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Regression tests for Issue #58 — <c>$global:__HvMcpSessions</c> lookup misses
/// on retry after runspace recycle, surfacing as
/// "Cannot index into a null array" and masking the original failure.
///
/// Fix is a defensive guard <c>if (-not $global:__HvMcpSessions) { $global:__HvMcpSessions = @{} }</c>
/// prepended to:
/// <list type="bullet">
///   <item><c>SessionStore.IsAliveCoreAsync</c> embedded PowerShell script (~line 717)</item>
///   <item><c>SessionStore.TryRemoveSessionInRunspaceAsync</c> embedded PowerShell script (~line 747)</item>
///   <item><c>PowerShellHost.Ps51InitializationScript</c> constant (~line 1192)</item>
/// </list>
///
/// LIMITATION: A true behavioral test requires Windows PowerShell 5.1 / live
/// runspace recycling which is not feasible in the unit harness (and would be
/// flaky / Windows-host-bound). We instead lock the regression in by asserting
/// the production source files contain the guard before any indexer read of
/// <c>$global:__HvMcpSessions</c>. This mirrors the script-text-assertion style
/// used elsewhere in <see cref="SessionStoreTests"/> for runspace-bound logic.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue58SessionsGlobalGuardTests
{
    // Source files are referenced by relative path from this test file.
    // Using [CallerFilePath] makes the lookup robust against changes in the
    // bin/ output directory or `dotnet test` working directory.
    private static string GetRepoRoot([CallerFilePath] string thisFile = "")
    {
        // tests/HyperV.Mcp.Server.Tests/Runtime/Issue58SessionsGlobalGuardTests.cs
        // → walk up three levels.
        var dir = Path.GetDirectoryName(thisFile)!;        // .../Runtime
        dir = Path.GetDirectoryName(dir)!;                  // .../HyperV.Mcp.Server.Tests
        dir = Path.GetDirectoryName(dir)!;                  // .../tests
        dir = Path.GetDirectoryName(dir)!;                  // repo root
        return dir;
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(GetRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"production source file must exist: {path}");
        return File.ReadAllText(path);
    }

    private const string GuardPattern =
        @"if\s*\(-not\s+\$global:__HvMcpSessions\)\s*\{\s*\$global:__HvMcpSessions\s*=\s*@\{\}\s*\}";

    /// <summary>
    /// Issue #58: <c>SessionStore.IsAliveCoreAsync</c>'s embedded script must
    /// initialize <c>$global:__HvMcpSessions</c> on demand BEFORE indexing it.
    /// </summary>
    [Fact]
    public void Issue58_SessionStore_IsAliveCore_Script_Has_Guard_Before_Indexer()
    {
        var src = ReadSource("src/HyperV.Mcp.Server/Infrastructure/SessionStore.cs");

        // Locate the IsAliveCoreAsync method DECLARATION via its full signature.
        // Using LastIndexOf("IsAliveCoreAsync") would be unreliable because the
        // identifier also appears in a comment inside TryRemoveSessionInRunspaceAsync
        // ("same reasoning as in IsAliveCoreAsync"), which is positioned AFTER the
        // method declaration in the source file (PR #62 review feedback).
        const string declMarker = "private async Task<bool> IsAliveCoreAsync";
        var methodIdx = src.IndexOf(declMarker, StringComparison.Ordinal);
        methodIdx.Should().BeGreaterThan(0,
            $"production source must declare '{declMarker}'.");

        // Search the next ~3000 chars (covers the method body + script literal).
        var window = src.Substring(methodIdx, Math.Min(3000, src.Length - methodIdx));

        var guardMatch = Regex.Match(window, GuardPattern);
        guardMatch.Success.Should().BeTrue(
            "Issue #58: IsAliveCoreAsync must contain the defensive guard " +
            "'if (-not $global:__HvMcpSessions) { $global:__HvMcpSessions = @{} }'.");

        var indexerIdx = window.IndexOf("$global:__HvMcpSessions[$sessionName]", StringComparison.Ordinal);
        indexerIdx.Should().BeGreaterThan(0,
            "the script must still index into the hashtable to look up the session.");

        guardMatch.Index.Should().BeLessThan(indexerIdx,
            "Issue #58: the defensive guard MUST appear BEFORE any indexer read of " +
            "$global:__HvMcpSessions, otherwise a recycled runspace causes " +
            "'Cannot index into a null array' on retry.");
    }

    /// <summary>
    /// Issue #58: <c>SessionStore.TryRemoveSessionInRunspaceAsync</c>'s embedded
    /// script must initialize the global hashtable before the indexer.
    /// </summary>
    [Fact]
    public void Issue58_SessionStore_TryRemoveSessionInRunspace_Script_Has_Guard_Before_Indexer()
    {
        var src = ReadSource("src/HyperV.Mcp.Server/Infrastructure/SessionStore.cs");

        var methodIdx = src.IndexOf("TryRemoveSessionInRunspaceAsync", StringComparison.Ordinal);
        methodIdx.Should().BeGreaterThan(0,
            "TryRemoveSessionInRunspaceAsync must exist in SessionStore.");

        // The first match is the call site; the actual method declaration is the
        // last occurrence (private async Task ...). Use LastIndexOf for the body.
        methodIdx = src.LastIndexOf("TryRemoveSessionInRunspaceAsync", StringComparison.Ordinal);
        var window = src.Substring(methodIdx, Math.Min(3000, src.Length - methodIdx));

        var guardMatch = Regex.Match(window, GuardPattern);
        guardMatch.Success.Should().BeTrue(
            "Issue #58: TryRemoveSessionInRunspaceAsync must contain the defensive guard.");

        var indexerIdx = window.IndexOf("$global:__HvMcpSessions[$sessionName]", StringComparison.Ordinal);
        indexerIdx.Should().BeGreaterThan(0,
            "the script must still index into the hashtable.");

        guardMatch.Index.Should().BeLessThan(indexerIdx,
            "Issue #58: the defensive guard MUST appear BEFORE the indexer read.");
    }

    /// <summary>
    /// Issue #58: <c>PowerShellHost.Ps51InitializationScript</c> must seed the
    /// hashtable as part of runspace initialization. The C# string literal is
    /// concatenated, so the guard pattern must be present in the source text.
    /// </summary>
    [Fact]
    public void Issue58_PowerShellHost_Ps51InitializationScript_Has_Guard()
    {
        var src = ReadSource("src/HyperV.Mcp.Server/Infrastructure/PowerShellHost.cs");

        // Locate the constant DECLARATION ("internal const string Ps51InitializationScript").
        // Earlier occurrences in the file are usages (e.g., a logger.LogDebug reference).
        var declMarker = "internal const string Ps51InitializationScript";
        var constIdx = src.IndexOf(declMarker, StringComparison.Ordinal);
        constIdx.Should().BeGreaterThan(0,
            $"production source must declare '{declMarker}'.");

        // Take a generous window covering the whole concatenated string literal.
        var window = src.Substring(constIdx, Math.Min(4000, src.Length - constIdx));

        Regex.IsMatch(window, GuardPattern).Should().BeTrue(
            "Issue #58: Ps51InitializationScript must initialize $global:__HvMcpSessions " +
            "if it is null. Without this, after a runspace recycle the read sites in " +
            "SessionStore would still find a $null hashtable on the very next call.");
    }
}
