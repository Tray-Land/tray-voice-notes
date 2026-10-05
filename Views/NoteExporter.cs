using Microsoft.UI.Xaml.Controls;
using TrayVoiceNotes.Models;
using TrayVoiceNotes.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TrayVoiceNotes.Views;

/// <summary>Outcome of an export attempt; <see cref="Title"/> is empty when the user cancelled.</summary>
public readonly record struct ExportResult(InfoBarSeverity Severity, string Title, string Message)
{
    public bool IsCancelled => Title.Length == 0;
}

/// <summary>Shows the save dialog and writes a note's audio, or audio + transcript + notes, to the chosen file.</summary>
public sealed class NoteExporter(nint windowHandle, Action<bool> setModal)
{
    public Task<ExportResult> ExportAudioAsync(VoiceNote note) =>
        ExportAsync(
            note,
            "WAV audio",
            ".wav",
            NoteExport.BaseName(note.CreatedAt),
            stream => NoteExport.CopyAudioAsync(NoteStore.AudioPath(note), stream));

    public Task<ExportResult> ExportZipAsync(VoiceNote note)
    {
        string baseName = NoteExport.BaseName(note.CreatedAt);
        string audioPath = NoteStore.AudioPath(note);
        string transcript = note.Transcript;
        string notes = note.Notes;
        return ExportAsync(
            note,
            "Zip archive",
            ".zip",
            baseName,
            stream => NoteExport.WriteZipAsync(stream, audioPath, baseName, transcript, notes));
    }

    private async Task<ExportResult> ExportAsync(VoiceNote note, string typeName, string extension, string suggestedName, Func<Stream, Task> write)
    {
        if (!File.Exists(NoteStore.AudioPath(note)))
        {
            return new(InfoBarSeverity.Error, "Couldn't export", "The audio file for this recording is missing.");
        }

        // The save dialog takes focus from the flyout; keep the flyout up until it closes.
        setModal(true);
        try
        {
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
            };
            picker.FileTypeChoices.Add(typeName, [extension]);
            InitializeWithWindow.Initialize(picker, windowHandle);

            if (await picker.PickSaveFileAsync() is not { } file)
            {
                return default;
            }

            await using Stream stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            await write(stream);
            return new(InfoBarSeverity.Success, "Saved", file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(InfoBarSeverity.Error, "Couldn't export", ex.Message);
        }
        finally
        {
            setModal(false);
        }
    }
}
