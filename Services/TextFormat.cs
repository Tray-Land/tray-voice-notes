using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TrayVoiceNotes.Services;

/// <summary>Formatting for durations, dates, titles, and Whisper output. No WinRT dependencies.</summary>
public static partial class TextFormat
{
    private const int TitleLength = 48;

    public static string Duration(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>"Today 9:41", "Yesterday 18:02", "Mon 9:41", or "Sep 3 9:41".</summary>
    public static string RecordedAt(DateTimeOffset value, DateTimeOffset now)
    {
        DateTime local = value.LocalDateTime;
        DateTime today = now.LocalDateTime.Date;
        string time = local.ToString("t", CultureInfo.CurrentCulture);

        if (local.Date == today)
        {
            return $"Today {time}";
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday {time}";
        }

        if (local.Date > today.AddDays(-7))
        {
            return $"{local.ToString("ddd", CultureInfo.CurrentCulture)} {time}";
        }

        string format = local.Year == today.Year ? "MMM d" : "MMM d, yyyy";
        return $"{local.ToString(format, CultureInfo.CurrentCulture)} {time}";
    }

    /// <summary>The start of the transcript on one line, cut at a word boundary.</summary>
    public static string? Title(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        string oneLine = Whitespace().Replace(transcript, " ").Trim();
        if (oneLine.Length <= TitleLength)
        {
            return oneLine;
        }

        int cut = oneLine.LastIndexOf(' ', TitleLength);
        return string.Concat(oneLine.AsSpan(0, cut > TitleLength / 2 ? cut : TitleLength), "…");
    }

    /// <summary>
    /// Joins Whisper segments into readable text, dropping the markers it emits for silence and
    /// noise ("[BLANK_AUDIO]", "(music)").
    /// </summary>
    public static string CleanTranscript(IEnumerable<string> segments)
    {
        StringBuilder builder = new();
        foreach (string segment in segments)
        {
            string text = Marker().Replace(segment, " ");
            text = Whitespace().Replace(text, " ").Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\[[^\]]*\]|\([A-Za-z _-]*\)|\*[A-Za-z _-]*\*")]
    private static partial Regex Marker();
}
