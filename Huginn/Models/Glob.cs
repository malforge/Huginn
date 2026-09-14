using System;

namespace Huginn.Models;

/// <summary>
/// Wildcard matching for the patterns a user types. Globs rather than regular expressions:
/// the patterns here name paths, and a mistyped regex fails in ways that are hard to see.
/// </summary>
public static class Glob
{
    /// <summary>
    /// Whether <paramref name="text"/> matches <paramref name="pattern"/>, case-insensitively.
    /// <c>*</c> stands for any run of characters, including none. <c>?</c> stands for one.
    /// </summary>
    public static bool Matches(string pattern, string text)
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
