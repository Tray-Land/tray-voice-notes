namespace TrayVoiceNotes.Services;

public enum RecorderState
{
    Idle,
    Recording,
    Paused,
}

/// <summary>What right-clicking the tray icon does when the app isn't recording.</summary>
public enum RightClickAction
{
    Record = 0,
    ShowMenu = 1,
}

/// <summary>What right-clicking the tray icon does while a recording is running.</summary>
public enum WhileRecordingAction
{
    Stop = 0,
    Pause = 1,
}

public enum TrayRightClickResult
{
    ShowMenu,
    Start,
    Stop,
    Pause,
    Resume,
}

/// <summary>
/// Decides what a right-click on the tray icon means. Pure logic, no WinRT, so it's covered by
/// the linked-file tests.
/// </summary>
public static class TrayClickPolicy
{
    public static TrayRightClickResult Decide(
        RecorderState state, RightClickAction action, WhileRecordingAction whileRecording, bool shiftHeld)
    {
        // Shift+right-click always reaches the menu, so Settings and Exit stay one gesture away.
        if (shiftHeld || action == RightClickAction.ShowMenu)
        {
            return TrayRightClickResult.ShowMenu;
        }

        return state switch
        {
            RecorderState.Idle => TrayRightClickResult.Start,
            RecorderState.Recording => whileRecording == WhileRecordingAction.Pause
                ? TrayRightClickResult.Pause
                : TrayRightClickResult.Stop,

            // Paused in pause mode: right-click resumes. Paused from the flyout in stop mode:
            // right-click finishes the recording, as it would have while running.
            _ => whileRecording == WhileRecordingAction.Pause
                ? TrayRightClickResult.Resume
                : TrayRightClickResult.Stop,
        };
    }
}
