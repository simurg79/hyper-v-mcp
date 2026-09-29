using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #289 — classification needles were raw <c>string.Contains</c> substring matches, so a
/// compound status identifier such as <c>NOT_STOPPED</c> matched the needle <c>STOPPED</c> and
/// selected the OPPOSITE branch. <see cref="TokenMatcher"/> exists to prevent that bug class, and
/// the decisive detail is that <c>_</c> MUST be an identifier character, not a boundary: if
/// underscore were a boundary, <c>NOT_STOPPED</c> would tokenize as <c>NOT</c> + <c>STOPPED</c> and
/// the defect would survive the rewrite intact.
///
/// See myplans/remoting/session-management/session-open-error-envelope-design.md — SOE-D7.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue289TokenBoundaryTests
{
    // ── The #289 bug class, stated directly ──────────────────────────────────

    [Theory]
    [InlineData("NOT_STOPPED")]
    [InlineData("PRE_STOPPED")]
    [InlineData("STOPPED_PENDING")]
    [InlineData("VM_NOT_STOPPED_YET")]
    public void UnderscoreCompoundIdentifier_DoesNotMatchTheBareNeedle(string compound)
    {
        TokenMatcher.ContainsToken(compound, "STOPPED").Should().BeFalse(
            $"#289: '{compound}' is a single identifier, not an occurrence of 'STOPPED'. " +
            "Underscore MUST be a token character; treating it as a boundary would split the " +
            "identifier and select the opposite classification branch.");
    }

    [Fact]
    public void UnderscoreIsAnIdentifierCharacter_NotABoundary()
    {
        TokenMatcher.IsTokenChar('_').Should().BeTrue(
            "#289/SOE-D7: underscore MUST be a token character — this single predicate is what " +
            "keeps NOT_STOPPED from matching STOPPED.");
    }

    [Fact]
    public void BareIdentifier_StillMatches_SoTheGuardIsNotVacuous()
    {
        TokenMatcher.ContainsToken("The VM is STOPPED.", "STOPPED").Should().BeTrue(
            "the matcher must still classify the genuine occurrence, or it would trade a " +
            "false positive for a false negative.");
    }

    // ── Boundary characters that MUST still delimit ──────────────────────────

    [Theory]
    [InlineData("The VM is STOPPED.")]
    [InlineData("state=STOPPED")]
    [InlineData("(STOPPED)")]
    [InlineData("'STOPPED'")]
    [InlineData("STOPPED")]
    [InlineData("C:\\path\\STOPPED\\file")]
    [InlineData("STOPPED,Running")]
    public void PunctuationAndSeparators_RemainBoundaries(string text)
    {
        TokenMatcher.ContainsToken(text, "STOPPED").Should().BeTrue(
            "punctuation, whitespace, quotes and path separators are boundaries, so a genuine " +
            "occurrence adjoined by them must still match.");
    }

    [Theory]
    [InlineData("STOPPEDX")]
    [InlineData("XSTOPPED")]
    [InlineData("STOPPED2")]
    [InlineData("9STOPPED")]
    public void LettersAndDigits_RemainTokenCharacters(string text)
    {
        TokenMatcher.ContainsToken(text, "STOPPED").Should().BeFalse(
            "a needle adjoined by a letter or digit is part of a larger token, not an occurrence.");
    }

    [Fact]
    public void Matching_IsAsciiCaseInsensitive()
    {
        TokenMatcher.ContainsToken("the vm is stopped.", "STOPPED").Should().BeTrue();
        TokenMatcher.ContainsToken("the vm is not_stopped.", "STOPPED").Should().BeFalse(
            "case-insensitivity MUST NOT weaken the underscore boundary rule");
    }

    // ── Phrase matching, the form the classification needles actually use ────

    [Fact]
    public void Phrase_MatchesOnlyWithBoundariesAtBothEnds()
    {
        TokenMatcher.ContainsPhrase("Error: access is denied by the guest.", "access is denied")
            .Should().BeTrue();
        TokenMatcher.ContainsPhrase("no_access_is_denied_flag was set", "access is denied")
            .Should().BeFalse(
                "#289: an underscore-joined compound MUST NOT satisfy a multi-word needle either.");
    }

    /// <summary>
    /// The scan must not stop at the first boundary-failing occurrence: a later, genuine occurrence
    /// still counts. A first-match-only implementation would silently under-classify.
    /// </summary>
    [Fact]
    public void LaterGenuineOccurrence_IsFound_AfterAnAdjoinedOne()
    {
        TokenMatcher.ContainsToken("NOT_STOPPED earlier, but the VM is STOPPED now.", "STOPPED")
            .Should().BeTrue(
                "scanning must continue past an occurrence that failed the boundary test.");
    }

    [Theory]
    [InlineData(null, "STOPPED")]
    [InlineData("", "STOPPED")]
    [InlineData("STOPPED", null)]
    [InlineData("STOPPED", "")]
    [InlineData("SHORT", "MUCH LONGER NEEDLE")]
    public void DegenerateInputs_AreACleanMiss_NotAThrow(string? text, string? needle)
    {
        TokenMatcher.ContainsToken(text, needle).Should().BeFalse(
            "a blank or oversized needle is a clean miss; throwing would fault unrelated calls.");
    }
}
