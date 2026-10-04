using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayVoiceNotes.Models;
using TrayVoiceNotes.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TrayVoiceNotes.Views;

/// <summary>
/// One recording: a seekable waveform player and editable transcript and notes. Edits save as
/// you type (the store debounces the write).
/// </summary>
public sealed partial class NotePage : Page
{
    private readonly Action _goBack;
    private readonly nint _windowHandle;
    private readonly Action<bool> _setModal;
    private VoiceNote? _note;
    private bool _updating;

    /// <param name="windowHandle">Owner for the save dialog.</param>
    /// <param name="setModal">Tells the flyout a dialog is open, so it doesn't light-dismiss behind it.</param>
    public NotePage(Action goBack, nint windowHandle, Action<bool> setModal)
    {
        _goBack = goBack;
        _windowHandle = windowHandle;
        _setModal = setModal;
        InitializeComponent();
    }

    public VoiceNote? Note => _note;

    public void Show(VoiceNote note)
    {
        Detach();
        _note = note;
        note.PropertyChanged += Note_PropertyChanged;
        ExportBar.IsOpen = false;

        HeaderText.Text = TextFormat.RecordedAt(note.CreatedAt, DateTimeOffset.Now);
        Waveform.Peaks = note.Peaks;
        DurationText.Text = note.DurationText;

        _updating = true;
        TranscriptBox.Text = note.Transcript;
        NotesBox.Text = note.Notes;
        _updating = false;

        UpdatePlayback();
        UpdateTranscriptState();
    }

    public void FocusDefault() => BackButton.Focus(FocusState.Programmatic);

    /// <summary>Stops listening to the note; the page may be reused or dropped after this.</summary>
    public void Detach()
    {
        if (_note is not null)
        {
            _note.PropertyChanged -= Note_PropertyChanged;
            _note = null;
        }
    }

    private void Note_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VoiceNote.PlaybackProgress):
            case nameof(VoiceNote.IsPlaying):
                UpdatePlayback();
                break;
            case nameof(VoiceNote.Status):
            case nameof(VoiceNote.Error):
                UpdateTranscriptState();
                break;
            case nameof(VoiceNote.Transcript) when _note is not null && TranscriptBox.Text != _note.Transcript:
                // A fresh transcription arrived.
                _updating = true;
                TranscriptBox.Text = _note.Transcript;
                _updating = false;
                break;
        }
    }

    private void UpdatePlayback()
    {
        if (_note is null)
        {
            return;
        }

        Waveform.Progress = _note.PlaybackProgress;
        PositionText.Text = TextFormat.Duration(TimeSpan.FromSeconds(_note.PlaybackProgress * _note.DurationSeconds));
        PlayGlyph.Glyph = _note.PlayGlyph;
        AutomationProperties.SetName(PlayButton, _note.PlayLabel);
        ToolTipService.SetToolTip(PlayButton, _note.PlayLabel);
    }

    private void UpdateTranscriptState()
    {
        if (_note is null)
        {
            return;
        }

        bool busy = _note.Status is TranscriptStatus.Transcribing or TranscriptStatus.Pending;
        TranscribingRing.IsActive = _note.Status == TranscriptStatus.Transcribing;
        TranscriptStatusText.Text = _note.Status == TranscriptStatus.Done ? string.Empty : _note.StatusText;
        RetranscribeButton.IsEnabled = !busy;
        TranscriptBox.IsReadOnly = busy;
    }

    private void TranscriptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating && _note is not null)
        {
            _note.Transcript = TranscriptBox.Text;
        }
    }

    private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating && _note is not null)
        {
            _note.Notes = NotesBox.Text;
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_note is not null)
        {
            PlaybackService.Toggle(_note);
        }
    }

    private void Waveform_SeekRequested(object? sender, double fraction)
    {
        if (_note is not null)
        {
            PlaybackService.Seek(_note, fraction);
            UpdatePlayback();
        }
    }

    private void RetranscribeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_note is not null)
        {
            TranscriptionService.Enqueue(_note);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_note is null)
        {
            return;
        }

        string text = string.IsNullOrWhiteSpace(_note.Notes)
            ? _note.Transcript
            : $"{_note.Transcript}{Environment.NewLine}{Environment.NewLine}{_note.Notes}";
        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private async void ExportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_note is not { } note)
        {
            return;
        }

        string baseName = NoteExport.BaseName(note.CreatedAt);
        await ExportAsync(
            note,
            "WAV audio",
            ".wav",
            baseName,
            stream => NoteExport.CopyAudioAsync(NoteStore.AudioPath(note), stream));
    }

    private async void ExportZip_Click(object sender, RoutedEventArgs e)
    {
        if (_note is not { } note)
        {
            return;
        }

        string baseName = NoteExport.BaseName(note.CreatedAt);
        string audioPath = NoteStore.AudioPath(note);
        string transcript = note.Transcript;
        string notes = note.Notes;
        await ExportAsync(
            note,
            "Zip archive",
            ".zip",
            baseName,
            stream => NoteExport.WriteZipAsync(stream, audioPath, baseName, transcript, notes));
    }

    private async Task ExportAsync(VoiceNote note, string typeName, string extension, string suggestedName, Func<Stream, Task> write)
    {
        ExportBar.IsOpen = false;
        if (!File.Exists(NoteStore.AudioPath(note)))
        {
            ShowExportResult(InfoBarSeverity.Error, "Couldn't export", "The audio file for this recording is missing.");
            return;
        }

        // The save dialog takes focus from the flyout; keep the flyout up until it closes.
        _setModal(true);
        try
        {
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
            };
            picker.FileTypeChoices.Add(typeName, [extension]);
            InitializeWithWindow.Initialize(picker, _windowHandle);

            if (await picker.PickSaveFileAsync() is not { } file)
            {
                return;
            }

            await using Stream stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            await write(stream);
            ShowExportResult(InfoBarSeverity.Success, "Saved", file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowExportResult(InfoBarSeverity.Error, "Couldn't export", ex.Message);
        }
        finally
        {
            _setModal(false);
        }
    }

    private void ShowExportResult(InfoBarSeverity severity, string title, string message)
    {
        ExportBar.Severity = severity;
        ExportBar.Title = title;
        ExportBar.Message = message;
        ExportBar.IsOpen = true;
    }

    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        DeleteFlyout.Hide();
        if (_note is { } note)
        {
            Detach();
            NoteStore.Delete(note);
        }

        _goBack();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _goBack();
}
