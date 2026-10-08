using System.Text;

namespace LlamaApp.Common;

/// <summary>
/// Splits a raw, command-line-style string into argv tokens — the same shape
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> takes.
/// Whitespace (spaces, tabs, newlines) separates tokens; double and single
/// quotes group a value that contains whitespace, and a backslash inside
/// double quotes escapes a quote or another backslash. Used for the
/// user-editable "custom serve arguments" setting, so the text a user types
/// (or pastes from the llama.cpp docs) becomes the exact tokens the server
/// receives — no shell, no re-quoting, no surprises.
/// </summary>
public static class ArgumentTokenizer
{
    /// <summary>
    /// Tokenizes <paramref name="text"/>. Null/blank input yields an empty
    /// list. Throws <see cref="FormatException"/> when a quote is left open —
    /// callers surface that as a validation error instead of launching.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var current = new StringBuilder();
        var hasCurrent = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (hasCurrent)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasCurrent = false;
                }
                continue;
            }

            hasCurrent = true;
            if (c is '"' or '\'')
            {
                i = ReadQuoted(text, i, c, current);
                continue;
            }

            current.Append(c);
        }

        if (hasCurrent) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// Reads a quoted segment starting at <paramref name="start"/> (the opening
    /// quote) and appends its contents to <paramref name="into"/>. Double
    /// quotes honor backslash escapes for <c>"</c> and <c>\</c>; single quotes
    /// are literal (matching shell behavior). Returns the index of the closing
    /// quote; throws when the quote never closes.
    /// </summary>
    private static int ReadQuoted(string text, int start, char quote, StringBuilder into)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '"' && c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next is '"' or '\\')
                {
                    into.Append(next);
                    i++;
                    continue;
                }
                // A backslash before anything else is literal (Windows paths).
                into.Append(c);
                continue;
            }

            if (c == quote) return i;
            into.Append(c);
        }

        throw new FormatException($"Unterminated {quote} quote in the argument list.");
    }
}
