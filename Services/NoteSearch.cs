using System.Globalization;

namespace TrayVoiceNotes.Services;

/// <summary>Transcript search: every whitespace-separated term must appear, ignoring case and accents.</summary>
internal static class NoteSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>How much text before the first match a snippet keeps.</summary>
    private const int SnippetLead = 30;

    public static bool Matches(string transcript, string query)
    {
        CompareInfo compare = CultureInfo.CurrentCulture.CompareInfo;
        foreach (string term in Terms(query))
        {
            if (compare.IndexOf(transcript, term, Options) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every occurrence of every term, sorted, with overlapping or touching ranges merged.</summary>
    public static IReadOnlyList<(int Start, int Length)> FindRanges(string text, string query)
    {
        CompareInfo compare = CultureInfo.CurrentCulture.CompareInfo;
        List<(int Start, int Length)> found = [];
        foreach (string term in Terms(query))
        {
            int position = 0;
            while (position < text.Length)
            {
                int index = compare.IndexOf(text.AsSpan(position), term, Options, out int length);
                if (index < 0 || length == 0)
                {
                    break;
                }

                found.Add((position + index, length));
                position += index + length;
            }
        }

        found.Sort();
        List<(int Start, int Length)> merged = [];
        foreach ((int start, int length) in found)
        {
            if (merged.Count > 0 && start <= merged[^1].Start + merged[^1].Length)
            {
                (int lastStart, int lastLength) = merged[^1];
                merged[^1] = (lastStart, Math.Max(lastLength, start + length - lastStart));
            }
            else
            {
                merged.Add((start, length));
            }
        }

        return merged;
    }

    /// <summary>
    /// The transcript, starting a little before the first match (with a leading ellipsis) so the
    /// match isn't cut off by a line limit. Unchanged when the match is already near the start.
    /// </summary>
    public static string Snippet(string text, string query)
    {
        IReadOnlyList<(int Start, int Length)> ranges = FindRanges(text, query);
        if (ranges.Count == 0 || ranges[0].Start <= SnippetLead)
        {
            return text;
        }

        int first = ranges[0].Start;
        int start = first - SnippetLead;

        // Begin at a word boundary rather than mid-word.
        int space = text.IndexOf(' ', start, first - start);
        if (space >= 0)
        {
            start = space + 1;
        }

        return "…" + text[start..];
    }

    private static string[] Terms(string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
