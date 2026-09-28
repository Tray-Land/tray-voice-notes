using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TrayVoiceNotes.Controls;
using TrayVoiceNotes.Models;
using TrayVoiceNotes.Services;

namespace TrayVoiceNotes.Views;

/// <summary>
/// The recordings list plus the live recording panel. Subscriptions to the app-lifetime services
/// exist only while the flyout is visible (<see cref="OnShown"/> / <see cref="OnHidden"/>).
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    private readonly Action<VoiceNote> _openNote;
    private readonly DispatcherQueueTimer _elapsedTimer;
    private bool _isShown;
    private float _pendingLevel;
    private int _levelQueued;

    public FlyoutPage(Action<VoiceNote> openNote)
    {
        _openNote = openNote;
        InitializeComponent();

        _elapsedTimer = DispatcherQueue.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(250);
        _elapsedTimer.Tick += (_, _) => ElapsedText.Text = TextFormat.Duration(RecordingService.Elapsed);
    }

    public void OnShown()
    {
        if (_isShown)
        {
            return;
        }

        _isShown = true;
        NotesList.ItemsSource ??= NoteStore.Notes;
        foreach (VoiceNote note in NoteStore.Notes)
        {
            note.RefreshSubtitle();
        }

        NoteStore.Notes.CollectionChanged += Notes_CollectionChanged;
        RecordingService.StateChanged += Recording_StateChanged;
        RecordingService.LevelChanged += Recording_LevelChanged;
        TranscriptionService.StatusChanged += Transcription_StatusChanged;

        UpdateEmptyState();
        UpdateRecordingPanel();
        UpdateFooter();
    }

    public void OnHidden()
    {
        if (!_isShown)
        {
            return;
        }

        _isShown = false;
        _elapsedTimer.Stop();
        NoteStore.Notes.CollectionChanged -= Notes_CollectionChanged;
        RecordingService.StateChanged -= Recording_StateChanged;
        RecordingService.LevelChanged -= Recording_LevelChanged;
        TranscriptionService.StatusChanged -= Transcription_StatusChanged;
    }

    public void Dispose()
    {
        OnHidden();

        // The collection is app-lifetime; letting go of it lets the closed window be collected.
        NotesList.ItemsSource = null;
    }

    /// <summary>Shows a problem (no microphone, model download failed) above the list.</summary>
    public void ShowError(string title, string message, bool canRetry = false)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = InfoBarSeverity.Warning;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.IsOpen = true;
    }

    public void FocusDefault()
    {
        if (RecordingService.State != RecorderState.Idle)
        {
            PauseButton.Focus(FocusState.Programmatic);
        }
        else
        {
            RecordButton.Focus(FocusState.Programmatic);
        }
    }

    private void Notes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void Recording_StateChanged(object? sender, EventArgs e) => UpdateRecordingPanel();

    // Audio thread, ~20 per second: coalesce into at most one UI update in flight.
    private void Recording_LevelChanged(object? sender, float level)
    {
        _pendingLevel = level;
        if (Interlocked.Exchange(ref _levelQueued, 1) == 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _levelQueued, 0);

                // Perceived loudness is closer to the square root of the peak.
                LevelMeter.Value = Math.Sqrt(_pendingLevel);
            });
        }
    }

    private void Transcription_StatusChanged(object? sender, EventArgs e) => UpdateFooter();

    private void UpdateEmptyState()
    {
        bool empty = NoteStore.Notes.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NotesList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Text = SettingsService.RightClickAction == RightClickAction.Record
            ? "Right-click the tray icon to start recording, and again to stop. Each recording is transcribed on this PC."
            : "Select the microphone button to start recording. Each recording is transcribed on this PC.";
    }

    private void UpdateRecordingPanel()
    {
        RecorderState state = RecordingService.State;
        bool active = state != RecorderState.Idle;
        RecordingPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.IsEnabled = !active;
        ElapsedText.Text = TextFormat.Duration(RecordingService.Elapsed);

        bool paused = state == RecorderState.Paused;
        RecordingStateText.Text = paused ? "Paused" : "Recording";
        RecordingDot.Opacity = paused ? 0.4 : 1;
        PauseGlyph.Glyph = paused ? "" : "";
        string pauseLabel = paused ? "Resume recording" : "Pause recording";
        AutomationProperties.SetName(PauseButton, pauseLabel);
        ToolTipService.SetToolTip(PauseButton, pauseLabel);

        if (state == RecorderState.Recording && _isShown)
        {
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            LevelMeter.Value = 0;
        }
    }

    private void UpdateFooter()
    {
        string? status = TranscriptionService.StatusText;
        Footer.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (status is null)
        {
            FooterRing.IsActive = false;
            return;
        }

        double? progress = TranscriptionService.DownloadProgress;
        FooterText.Text = progress is { } p ? $"{status} · {p:P0}" : $"{status}…";
        FooterRing.IsIndeterminate = progress is null;
        FooterRing.Value = (progress ?? 0) * 100;
        FooterRing.IsActive = true;

        if (TranscriptionService.LastError is { } error && !StatusBar.IsOpen)
        {
            ShowError("Transcription is unavailable", error, canRetry: true);
        }
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e) => await App.Current.StartRecordingAsync();

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingService.State == RecorderState.Paused)
        {
            await App.Current.ResumeRecordingAsync();
        }
        else
        {
            await RecordingService.PauseAsync();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await RecordingService.StopAsync();

    private async void DiscardButton_Click(object sender, RoutedEventArgs e) => await RecordingService.CancelAsync();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        StatusBar.IsOpen = false;
        TranscriptionService.RetryFailed();
    }

    private void NotesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VoiceNote note)
        {
            _openNote(note);
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VoiceNote note })
        {
            PlaybackService.Toggle(note);
        }
    }

    private void Waveform_SeekRequested(object? sender, double fraction)
    {
        if (sender is WaveformView { Tag: VoiceNote note })
        {
            PlaybackService.Seek(note, fraction);
        }
    }
}
