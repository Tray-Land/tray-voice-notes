using System.Collections.ObjectModel;
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
    private readonly NoteExporter _exporter;
    private readonly ObservableCollection<VoiceNote> _results = [];
    private readonly DispatcherQueueTimer _elapsedTimer;
    private bool _isShown;
    private float _pendingLevel;
    private int _levelQueued;

    public FlyoutPage(Action<VoiceNote> openNote, NoteExporter exporter)
    {
        _openNote = openNote;
        _exporter = exporter;
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
        SearchBox.Text = string.Empty;
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

    private void Notes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SearchQuery.Length > 0)
        {
            ApplyFilter();
        }
        else
        {
            UpdateEmptyState();
        }
    }

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

    private void Transcription_StatusChanged(object? sender, EventArgs e)
    {
        UpdateFooter();

        // A transcript that just finished may now match the search.
        if (SearchQuery.Length > 0)
        {
            ApplyFilter();
        }
    }

    private string SearchQuery => SearchBox.Text.Trim();

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    /// <summary>Points the list at all notes, or at the notes whose transcript matches the search.</summary>
    private void ApplyFilter()
    {
        string query = SearchQuery;
        foreach (VoiceNote note in NoteStore.Notes)
        {
            note.SearchQuery = query;
        }

        if (query.Length == 0)
        {
            _results.Clear();
            NotesList.ItemsSource = NoteStore.Notes;
        }
        else
        {
            _results.Clear();
            foreach (VoiceNote note in NoteStore.Notes.Where(n => NoteSearch.Matches(n.Transcript, query)))
            {
                _results.Add(note);
            }

            NotesList.ItemsSource = _results;
        }

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool noNotes = NoteStore.Notes.Count == 0;
        bool searching = SearchQuery.Length > 0;
        bool noMatches = searching && _results.Count == 0;
        bool empty = noNotes || noMatches;

        SearchBox.Visibility = noNotes ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NotesList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        if (noMatches && !noNotes)
        {
            EmptyTitle.Text = "No matching transcripts";
            EmptyHint.Text = "Try a different word or phrase.";
            return;
        }

        EmptyTitle.Text = "No recordings yet";
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

    private void QuitButton_Click(object sender, RoutedEventArgs e) => App.Current.ExitApp();

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

    private void DeleteNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VoiceNote note })
        {
            ConfirmDelete(note);
        }
    }

    private async void ExportAudioMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VoiceNote note })
        {
            ShowExportResult(await _exporter.ExportAudioAsync(note));
        }
    }

    private async void ExportZipMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VoiceNote note })
        {
            ShowExportResult(await _exporter.ExportZipAsync(note));
        }
    }

    private void DeleteSwipeItem_Invoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (args.SwipeControl is { Tag: VoiceNote note })
        {
            args.SwipeControl.Close();
            ConfirmDelete(note);
        }
    }

    private void ExportSwipeItem_Invoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
    {
        if (args.SwipeControl is not { Tag: VoiceNote note } swipe)
        {
            return;
        }

        swipe.Close();
        MenuFlyoutItem audio = new() { Text = "Save audio…", Icon = new FontIcon { Glyph = "" } };
        audio.Click += async (_, _) => ShowExportResult(await _exporter.ExportAudioAsync(note));
        MenuFlyoutItem zip = new() { Text = "Save audio, transcript and notes (.zip)…", Icon = new FontIcon { Glyph = "" } };
        zip.Click += async (_, _) => ShowExportResult(await _exporter.ExportZipAsync(note));

        MenuFlyout menu = new();
        menu.Items.Add(audio);
        menu.Items.Add(zip);
        menu.ShowAt(swipe);
    }

    private void ShowExportResult(ExportResult result)
    {
        if (result.IsCancelled)
        {
            return;
        }

        StatusBar.Title = result.Title;
        StatusBar.Message = result.Message;
        StatusBar.Severity = result.Severity;
        RetryButton.Visibility = Visibility.Collapsed;
        StatusBar.IsOpen = true;
    }

    private void ConfirmDelete(VoiceNote note)
    {
        Button confirm = new() { Content = "Delete", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        Flyout flyout = new()
        {
            Content = new StackPanel
            {
                MaxWidth = 240,
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Delete this recording and its transcript and notes?", TextWrapping = TextWrapping.Wrap },
                    confirm,
                },
            },
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            NoteStore.Delete(note);
        };

        if (NotesList.ContainerFromItem(note) is FrameworkElement container)
        {
            flyout.ShowAt(container);
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
