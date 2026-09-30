using System;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Delimiter-bounded, ASCII-case-insensitive token and phrase matching for classification
/// and scoring over arbitrary diagnostic text.
/// </summary>
/// <remarks>
/// MUST be used instead of raw <c>string.Contains</c> for every classification needle.
/// Issue #289: a raw substring needle such as <c>access is denied</c> or <c>cannot find path</c>
/// matches leaked comment prose inside diagnostic text and misclassifies the failure — the same
/// bug class as <c>NOT_STOPPED</c> matching as <c>STOPPED</c>. See
/// /myplans/remoting/session-management/session-open-error-envelope-design.md — SOE-D7.
/// </remarks>
internal static class TokenMatcher
{
    /// <summary>
    /// True when <paramref name="text"/> contains <paramref name="token"/> as a whole token —
    /// i.e. not adjoined on either side by another token character.
    /// </summary>
    internal static bool ContainsToken(string? text, string? token)
        => ContainsPhrase(text, token);

    /// <summary>
    /// True when <paramref name="phrase"/> (which may contain spaces) occurs in
    /// <paramref name="text"/> with a token boundary at both ends.
    /// </summary>
    internal static bool ContainsPhrase(string? text, string? phrase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(phrase))
        {
            return false;
        }

        int searchFrom = 0;
        while (searchFrom <= text!.Length - phrase!.Length)
        {
            int found = text.IndexOf(phrase, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            bool leftBoundary = found == 0 || !IsTokenChar(text[found - 1]);
            int afterIndex = found + phrase.Length;
            bool rightBoundary = afterIndex >= text.Length || !IsTokenChar(text[afterIndex]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            searchFrom = found + 1;
        }

        return false;
    }

    /// <summary>
    /// Token characters are letters, digits, and underscore. Everything else — punctuation,
    /// whitespace, path separators, quotes — is a boundary.
    /// </summary>
    /// <remarks>
    /// Underscore MUST be a token character, not a boundary. Compound status identifiers such as
    /// <c>NOT_STOPPED</c> and <c>PRE_STOPPED</c> otherwise match the needle <c>STOPPED</c> and
    /// select the opposite classification branch — the issue #289 bug class this type exists to
    /// prevent. See /myplans/remoting/session-management/session-open-error-envelope-design.md — SOE-D7.
    /// </remarks>
    internal static bool IsTokenChar(char candidate)
        => char.IsLetterOrDigit(candidate) || candidate == '_';
}
