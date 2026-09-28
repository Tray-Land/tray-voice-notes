using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using TrayVoiceNotes.Models;
using Windows.Storage;

namespace TrayVoiceNotes.Services;

[JsonSerializable(typeof(List<VoiceNote>))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class NoteJsonContext : JsonSerializerContext;

/// <summary>
/// The list of voice notes: WAV files in <c>LocalFolder\Recordings</c> plus a small JSON index
/// (transcripts, notes, peaks). The index is a few KB per hundred notes, so it stays loaded;
/// the audio stays on disk until something plays or transcribes it.
/// </summary>
internal static class NoteStore
{
    private const string IndexFileName = "notes.json";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private static ObservableCollection<VoiceNote>? _notes;
    private static DispatcherQueueTimer? _saveTimer;

    /// <summary>Newest first. Touch only on the UI thread.</summary>
    public static ObservableCollection<VoiceNote> Notes => _notes ??= Load();

    public static string RecordingsFolder { get; } = CreateFolder();

    public static string AudioPath(VoiceNote note) => AudioPath(note.Id);

    public static string AudioPath(string id) => Path.Combine(RecordingsFolder, $"{id}.wav");

    public static VoiceNote? Find(string id) => Notes.FirstOrDefault(n => n.Id == id);

    public static void Add(VoiceNote note)
    {
        Notes.Insert(0, note);
        note.PropertyChanged += Note_PropertyChanged;
        SaveSoon();
    }

    public static void Delete(VoiceNote note)
    {
        PlaybackService.StopIfCurrent(note);
        TranscriptionService.Cancel(note);
        note.PropertyChanged -= Note_PropertyChanged;
        Notes.Remove(note);
        try
        {
            File.Delete(AudioPath(note));
        }
        catch (IOException)
        {
            // Still open somewhere; the orphaned file is harmless.
        }

        SaveSoon();
    }

    /// <summary>Writes the index now (on exit) instead of waiting for the debounce.</summary>
    public static void Flush()
    {
        _saveTimer?.Stop();
        Save();
    }

    private static ObservableCollection<VoiceNote> Load()
    {
        List<VoiceNote> list = [];
        try
        {
            string path = Path.Combine(RecordingsFolder, IndexFileName);
            if (File.Exists(path))
            {
                list = JsonSerializer.Deserialize(File.ReadAllText(path), NoteJsonContext.Default.ListVoiceNote) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt index shouldn't lose the audio; keep going with what's on disk.
        }

        // Drop entries whose audio is gone, and anything interrupted mid-transcription goes back
        // in the queue.
        list.RemoveAll(n => !File.Exists(AudioPath(n)));
        foreach (VoiceNote note in list)
        {
            note.PropertyChanged += Note_PropertyChanged;
        }

        ObservableCollection<VoiceNote> notes = new(list.OrderByDescending(n => n.CreatedAt));
        foreach (VoiceNote note in notes.Where(n => n.Status is TranscriptStatus.Pending or TranscriptStatus.Transcribing))
        {
            note.Status = TranscriptStatus.Pending;
            TranscriptionService.Enqueue(note);
        }

        return notes;
    }

    private static void Note_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VoiceNote.Transcript) or nameof(VoiceNote.Notes) or nameof(VoiceNote.Status)
            or nameof(VoiceNote.Peaks) or nameof(VoiceNote.DurationSeconds))
        {
            SaveSoon();
        }
    }

    private static void SaveSoon()
    {
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _saveTimer.Interval = SaveDelay;
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => Save();
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static void Save()
    {
        if (_notes is null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(RecordingsFolder, IndexFileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_notes.ToList(), NoteJsonContext.Default.ListVoiceNote));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Try again on the next change.
        }
    }

    private static string CreateFolder()
    {
        string root;
        try
        {
            root = ApplicationData.Current.LocalFolder.Path;
        }
        catch (InvalidOperationException)
        {
            // Unpackaged: no ApplicationData.
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayVoiceNotes");
        }

        string folder = Path.Combine(root, "Recordings");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
