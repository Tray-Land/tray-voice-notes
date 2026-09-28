using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayVoiceNotes.Services;
using Windows.ApplicationModel;

namespace TrayVoiceNotes.Views;

/// <summary>
/// Settings, shown inside the flyout. Every control is inline and applies immediately.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly Action _goBack;
    private IReadOnlyList<(int Number, string Name)> _microphones = [];
    private bool _loading = true;
    private bool _isShown;

    public SettingsPage(Action goBack)
    {
        _goBack = goBack;
        InitializeComponent();
        VersionText.Text = $"{App.DisplayName} {GetVersion()}";
        foreach (WhisperModelInfo model in ModelService.Models)
        {
            ModelCombo.Items.Add($"{model.DisplayName} · {model.SizeMB} MB");
        }
    }

    /// <summary>Re-reads everything: the startup state and devices can change outside the app.</summary>
    public void OnShown()
    {
        _loading = true;
        RightClickCombo.SelectedIndex = (int)SettingsService.RightClickAction;
        WhileRecordingCombo.SelectedIndex = (int)SettingsService.WhileRecordingAction;

        _microphones = RecordingService.GetMicrophones();
        MicrophoneCombo.Items.Clear();
        foreach ((int _, string name) in _microphones)
        {
            MicrophoneCombo.Items.Add(name);
        }

        int selected = _microphones.ToList().FindIndex(m => m.Number == SettingsService.MicrophoneDevice);
        MicrophoneCombo.SelectedIndex = Math.Max(0, selected);

        string model = SettingsService.WhisperModel;
        ModelCombo.SelectedIndex = ModelService.Models.ToList().FindIndex(m => m.Name == model);
        _loading = false;

        if (!_isShown)
        {
            _isShown = true;
            TranscriptionService.StatusChanged += Transcription_StatusChanged;
        }

        UpdateWhileRecordingCard();
        UpdateModelState();
        _ = LoadStartupStateAsync();
    }

    public void OnHidden()
    {
        if (_isShown)
        {
            _isShown = false;
            TranscriptionService.StatusChanged -= Transcription_StatusChanged;
        }
    }

    public void FocusDefault() => BackButton.Focus(FocusState.Programmatic);

    private static string GetVersion()
    {
        try
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "(unpackaged)";
        }
    }

    private void Transcription_StatusChanged(object? sender, EventArgs e) => UpdateModelState();

    private void UpdateWhileRecordingCard()
    {
        bool recordsOnRightClick = SettingsService.RightClickAction == RightClickAction.Record;
        WhileRecordingCombo.IsEnabled = recordsOnRightClick;
        WhileRecordingCaption.Text = !recordsOnRightClick
            ? "Applies when right-click starts recording."
            : SettingsService.WhileRecordingAction == WhileRecordingAction.Pause
                ? "Stop from the flyout when you're done."
                : "Transcription starts as soon as you stop.";
    }

    private void UpdateModelState()
    {
        string model = SettingsService.WhisperModel;
        WhisperModelInfo info = ModelService.Get(model);
        bool downloaded = ModelService.IsDownloaded(model);
        double? progress = TranscriptionService.DownloadProgress;

        DownloadRing.IsActive = progress is not null;
        DownloadRing.Value = (progress ?? 0) * 100;
        DownloadButton.IsEnabled = !downloaded && progress is null;
        DownloadButton.Visibility = downloaded ? Visibility.Collapsed : Visibility.Visible;
        DeleteModelButton.Visibility = downloaded ? Visibility.Visible : Visibility.Collapsed;
        DeleteModelButton.IsEnabled = TranscriptionService.StatusText is null;

        string language = ModelService.IsMultilingual(model) ? "Detects the spoken language." : "English only, and more accurate for it.";
        ModelCaption.Text = progress is { } p
            ? $"Downloading · {p:P0}"
            : downloaded
                ? $"Downloaded. {language} Runs on this PC."
                : $"Downloads on first use ({info.SizeMB} MB). {language} Runs on this PC.";
    }

    private async Task LoadStartupStateAsync() => ShowStartupState(await StartupService.GetStateAsync());

    private void ShowStartupState(StartupTaskState? state)
    {
        _loading = true;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

        // The user (Task Manager, Settings > Apps > Startup) or policy has the final say; the app
        // can't override it, so say where to change it instead of offering a dead toggle.
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Only available when the app is installed.",
            _ => "Keep the tray icon ready after you sign in.",
        };
        _loading = false;
    }

    private void RightClickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RightClickCombo.SelectedIndex < 0)
        {
            return;
        }

        SettingsService.RightClickAction = (RightClickAction)RightClickCombo.SelectedIndex;
        UpdateWhileRecordingCard();
    }

    private void WhileRecordingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || WhileRecordingCombo.SelectedIndex < 0)
        {
            return;
        }

        SettingsService.WhileRecordingAction = (WhileRecordingAction)WhileRecordingCombo.SelectedIndex;
        UpdateWhileRecordingCard();
    }

    private void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicrophoneCombo.SelectedIndex < 0 || MicrophoneCombo.SelectedIndex >= _microphones.Count)
        {
            return;
        }

        SettingsService.MicrophoneDevice = _microphones[MicrophoneCombo.SelectedIndex].Number;
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelCombo.SelectedIndex < 0)
        {
            return;
        }

        SettingsService.WhisperModel = ModelService.Models[ModelCombo.SelectedIndex].Name;
        UpdateModelState();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        string model = SettingsService.WhisperModel;
        DownloadButton.IsEnabled = false;
        DownloadRing.IsActive = true;
        try
        {
            Progress<double> progress = new(p =>
            {
                DownloadRing.Value = p * 100;
                ModelCaption.Text = $"Downloading · {p:P0}";
            });
            await ModelService.EnsureAsync(model, progress, CancellationToken.None);
            TranscriptionService.RetryFailed();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or TaskCanceledException)
        {
            ModelCaption.Text = "The download didn't finish. Check your connection and try again.";
            DownloadButton.IsEnabled = true;
            DownloadRing.IsActive = false;
            return;
        }

        UpdateModelState();
    }

    private void DeleteModelButton_Click(object sender, RoutedEventArgs e)
    {
        ModelService.Delete(SettingsService.WhisperModel);
        UpdateModelState();
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ShowStartupState(await StartupService.SetEnabledAsync(StartupToggle.IsOn));
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(NoteStore.RecordingsFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing sensible to do; Explorer is missing or blocked.
        }
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e) => App.Current.ExitApp();

    private void BackButton_Click(object sender, RoutedEventArgs e) => _goBack();
}
