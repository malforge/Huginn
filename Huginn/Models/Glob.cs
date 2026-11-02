using System;

namespace Huginn.Models;

/// <summary>
/// Wildcard matching for the patterns a user types. Globs rather than regular expressions:
/// the patterns here name paths, and a mistyped regex fails in ways that are hard to see.
/// </summary>
public static class Glob
{
    /// <summary>
    /// Whether <paramref name="text"/> matches <paramref name="pattern"/>, case-insensitively and
    /// whether or not either ends in a slash. <c>*</c> stands for any run of characters,
    /// including none. <c>?</c> stands for one.
    /// </summary>
    /// <remarks>
    /// A route is shown in whichever of its spellings was recorded most, which can gain or lose a
    /// trailing slash from one poll to the next, so a rule naming it must not depend on one.
    /// </remarks>
    public static bool Matches(string pattern, string text) =>
        MatchesAsWritten(pattern, text)
        || ((pattern.EndsWith('/') || text.EndsWith('/'))
            && MatchesAsWritten(pattern.TrimEnd('/'), text.TrimEnd('/')));

    private static bool MatchesAsWritten(string pattern, string text)
    {
        if (pattern.Length == 0) return false;

        // Iterative rather than recursive, with a remembered star position to fall back to.
        // A pattern of several stars against a long path recurses badly, and these run against
        // every operation on every poll.
        int p = 0, t = 0, star = -1, mark = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                // Backtrack: let the last star swallow one more character.
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;

        return p == pattern.Length;
    }

    private static bool Same(char a, char b) =>
        char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
