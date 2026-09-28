using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayVoiceNotes.Services;
using TrayVoiceNotes.Views;
using WinUIEx;

namespace TrayVoiceNotes;

/// <summary>
/// Tray-only application: no window at launch, just a notification-area icon. Left-click toggles
/// the flyout; right-click starts and stops (or pauses) a recording, and the icon turns red while
/// recording. Windows exist only while they're in use; the idle app is an icon and little else.
/// </summary>
public partial class App : Application
{
    public const string DisplayName = "Tray Voice Notes";

    private const string MutexName = "Local\\TrayVoiceNotes_SingleInstance";
    private const string ShowEventName = "Local\\TrayVoiceNotes_ShowFlyout";
    private const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip limit
    private const int VK_SHIFT = 0x10;

    // Startup leaves JIT and first-request state behind that the idle app never touches again.
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(30);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _trayIcon;
    private TrayFlyoutWindow? _flyout;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;

    public App()
    {
        InitializeComponent();

        // A tray app lives on after its windows close; only the Exit menu ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the tray icon: ask it to open its flyout, then quit.
            try
            {
                // Let the running instance take the foreground when it shows its flyout.
                Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // The other instance may be shutting down; nothing to do.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);

        InitializeTrayIcon();

        // Loading the index is cheap, and it resumes any transcription an exit interrupted.
        _ = NoteStore.Notes;

        // First run: show where the app lives instead of launching into silence.
        if (!SettingsService.HasLaunchedBefore)
        {
            SettingsService.HasLaunchedBefore = true;
            ShowFlyout();
        }
        else
        {
            MemoryService.ReleaseIdle(StartupTrimDelay);
        }
    }

    /// <summary>Sets the tray tooltip under the app name, e.g. "Recording" or null for just the name.</summary>
    public void SetTrayStatus(string? status)
    {
        if (_trayIcon is null)
        {
            return;
        }

        string tooltip = string.IsNullOrWhiteSpace(status) ? DisplayName : $"{DisplayName}\n{status}";
        _trayIcon.Tooltip = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
    }

    public void ShowFlyout()
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        flyout.HidePopup();
        flyout.ShowMainPage();
        flyout.ShowPopup();
    }

    public void ShowSettings()
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        flyout.ShowSettingsPage();
        if (!flyout.IsPopupVisible)
        {
            flyout.ShowPopup();
        }
    }

    public Task StartRecordingAsync()
    {
        try
        {
            RecordingService.Start();
        }
        catch (Exception ex) when (ex is NAudio.MmException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMicrophoneError(ex);
        }

        return Task.CompletedTask;
    }

    public async Task ResumeRecordingAsync()
    {
        try
        {
            RecordingService.Resume();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            // The device went away while paused: keep what was captured.
            await RecordingService.StopAsync();
            ShowMicrophoneError(ex);
        }
    }

    public async void ExitApp()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        // Keep a recording in progress rather than losing it; it's transcribed on the next launch.
        await RecordingService.StopAsync();
        PlaybackService.Unload();
        NoteStore.Flush();
        _flyout?.CloseWindow();

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Exit();
    }

    private void ShowMicrophoneError(Exception ex)
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        if (!flyout.IsPopupVisible)
        {
            flyout.ShowPopup();
        }

        flyout.ShowError(
            "Can't use the microphone",
            ex is NAudio.MmException { Result: NAudio.MmResult.BadDeviceId }
                ? "No microphone was found. Connect one and try again."
                : "Check that a microphone is connected and that desktop apps may use it in Settings > Privacy & security > Microphone.");
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIcon(1, TrayIconPath, DisplayName);
        _trayIcon.Selected += (_, _) => EnsureFlyout().Toggle();
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacementService.SetTrayIcon(_trayIcon);
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(UpdateTrayIcon);
        RecordingService.StateChanged += (_, _) => UpdateTrayIcon();
        TranscriptionService.StatusChanged += (_, _) => UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        _trayIcon?.SetIcon(TrayIconPath);

        bool rightClickRecords = SettingsService.RightClickAction == RightClickAction.Record;
        bool pauseMode = SettingsService.WhileRecordingAction == WhileRecordingAction.Pause;
        SetTrayStatus(RecordingService.State switch
        {
            RecorderState.Recording when rightClickRecords => pauseMode ? "Recording · right-click to pause" : "Recording · right-click to stop",
            RecorderState.Recording => "Recording",
            RecorderState.Paused when rightClickRecords => pauseMode ? "Paused · right-click to resume" : "Paused · right-click to stop",
            RecorderState.Paused => "Paused",
            _ => TranscriptionService.StatusText is { } status ? $"{status}…" : null,
        });
    }

    // Recording states use red icons, which read on both taskbar themes. Otherwise a white glyph
    // disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static string TrayIconPath => Path.Combine(AppContext.BaseDirectory, "Assets", RecordingService.State switch
    {
        RecorderState.Recording => "Recording.ico",
        RecorderState.Paused => "Paused.ico",
        _ => SystemThemeService.IsLight ? "AppIcon-dark.ico" : "AppIcon.ico",
    });

    private async void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        bool shiftHeld = (Windows.Win32.PInvoke.GetKeyState(VK_SHIFT) & 0x8000) != 0;
        TrayRightClickResult result = TrayClickPolicy.Decide(
            RecordingService.State, SettingsService.RightClickAction, SettingsService.WhileRecordingAction, shiftHeld);

        switch (result)
        {
            case TrayRightClickResult.ShowMenu:
                args.Flyout = BuildMenu();
                break;
            case TrayRightClickResult.Start:
                await StartRecordingAsync();
                break;
            case TrayRightClickResult.Pause:
                await RecordingService.PauseAsync();
                break;
            case TrayRightClickResult.Resume:
                await ResumeRecordingAsync();
                break;
            case TrayRightClickResult.Stop:
                await RecordingService.StopAsync();
                break;
        }
    }

    // Built fresh each time so the recording items match the current state.
    private MenuFlyout BuildMenu()
    {
        MenuFlyout menu = new();
        switch (RecordingService.State)
        {
            case RecorderState.Idle:
                menu.Items.Add(MenuItem("Start recording", "", () => _ = StartRecordingAsync()));
                break;
            case RecorderState.Recording:
                menu.Items.Add(MenuItem("Stop and transcribe", "", () => _ = RecordingService.StopAsync()));
                menu.Items.Add(MenuItem("Pause recording", "", () => _ = RecordingService.PauseAsync()));
                break;
            case RecorderState.Paused:
                menu.Items.Add(MenuItem("Stop and transcribe", "", () => _ = RecordingService.StopAsync()));
                menu.Items.Add(MenuItem("Resume recording", "", () => _ = ResumeRecordingAsync()));
                break;
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Open voice notes", "", ShowFlyout));
        menu.Items.Add(MenuItem("Settings", "", ShowSettings));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Exit", "", ExitApp));
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        MenuFlyoutItem item = new() { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private TrayFlyoutWindow EnsureFlyout()
    {
        if (_flyout is null)
        {
            // The flyout closes itself after staying hidden for a while; the next open builds a new one.
            _flyout = new TrayFlyoutWindow();
            _flyout.Closed += (_, _) =>
            {
                _flyout = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        return _flyout;
    }

    private void ReleaseIdleResourcesIfNoWindows()
    {
        if (_flyout is null && !_isExiting)
        {
            MemoryService.ReleaseIdle();
        }
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "TrayVoiceNotes-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }
}
