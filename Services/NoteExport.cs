using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace TrayVoiceNotes.Services;

/// <summary>Builds the files a note is exported as: the audio on its own, or a .zip bundle.</summary>
internal static class NoteExport
{
    /// <summary>File name without extension, e.g. "Voice note 2026-10-04 1530".</summary>
    public static string BaseName(DateTimeOffset createdAt) =>
        $"Voice note {createdAt.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}";

    public static async Task CopyAudioAsync(string audioPath, Stream destination)
    {
        // Playback may hold the file open for reading.
        await using FileStream source = new(audioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await source.CopyToAsync(destination);
    }

    /// <summary>
    /// Writes a zip holding the audio, <c>transcript.txt</c>, and <c>notes.txt</c> (only when
    /// there are notes).
    /// </summary>
    public static async Task WriteZipAsync(Stream destination, string audioPath, string baseName, string transcript, string notes)
    {
        using ZipArchive zip = new(destination, ZipArchiveMode.Create, leaveOpen: true);

        ZipArchiveEntry audio = zip.CreateEntry($"{baseName}.wav", CompressionLevel.Optimal);
        await using (Stream entryStream = audio.Open())
        {
            await CopyAudioAsync(audioPath, entryStream);
        }

        AddText(zip, "transcript.txt", transcript);
        if (!string.IsNullOrWhiteSpace(notes))
        {
            AddText(zip, "notes.txt", notes);
        }
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }
}
