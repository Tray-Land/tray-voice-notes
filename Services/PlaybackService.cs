using Microsoft.UI.Dispatching;
using NAudio.Wave;
using TrayVoiceNotes.Models;

namespace TrayVoiceNotes.Services;

/// <summary>
/// Plays one note at a time through NAudio. The output device and file handle exist only while
/// something is loaded; the progress timer runs only while audio is actually playing.
/// </summary>
internal static class PlaybackService
{
    private static WaveOutEvent? _output;
    private static WaveFileReader? _reader;
    private static VoiceNote? _current;
    private static DispatcherQueueTimer? _progressTimer;

    public static VoiceNote? Current => _current;

    public static TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;

    /// <summary>Plays <paramref name="note"/>, or pauses it if it's the one playing.</summary>
    public static void Toggle(VoiceNote note)
    {
        if (_current == note && note.IsPlaying)
        {
            Pause();
        }
        else
        {
            Play(note);
        }
    }

    public static void Play(VoiceNote note)
    {
        if (RecordingService.State != RecorderState.Idle)
        {
            return;
        }

        if (_current != note)
        {
            Unload();
            try
            {
                _reader = new WaveFileReader(NoteStore.AudioPath(note));
                _output = new WaveOutEvent();
                _output.Init(_reader);
                _output.PlaybackStopped += Output_PlaybackStopped;
            }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or NAudio.MmException)
            {
                Unload();
                return;
            }

            _current = note;
            if (note.PlaybackProgress >= 0.999)
            {
                note.PlaybackProgress = 0;
            }

            _reader.CurrentTime = TimeSpan.FromSeconds(note.PlaybackProgress * _reader.TotalTime.TotalSeconds);
        }
        else if (_reader is not null && _reader.Position >= _reader.Length)
        {
            _reader.Position = 0;
        }

        _output!.Play();
        note.IsPlaying = true;
        StartProgressTimer();
    }

    public static void Pause()
    {
        if (_current is null || _output is null)
        {
            return;
        }

        _output.Pause();
        _current.IsPlaying = false;
        _progressTimer?.Stop();
        UpdateProgress();
    }

    /// <summary>Moves the playhead; plays from there if the note was already playing.</summary>
    public static void Seek(VoiceNote note, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (_current != note || _reader is null)
        {
            // Not loaded: remember where to start from.
            note.PlaybackProgress = fraction;
            return;
        }

        _reader.CurrentTime = TimeSpan.FromSeconds(fraction * _reader.TotalTime.TotalSeconds);
        UpdateProgress();
    }

    public static void StopIfCurrent(VoiceNote note)
    {
        if (_current == note)
        {
            Unload();
        }
    }

    public static void Unload()
    {
        _progressTimer?.Stop();
        if (_output is not null)
        {
            _output.PlaybackStopped -= Output_PlaybackStopped;
            _output.Dispose();
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
        if (_current is not null)
        {
            _current.IsPlaying = false;
            _current = null;
        }
    }

    // NAudio posts this to the synchronization context it was created on (the UI thread here).
    private static void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        bool finished = _reader is null || _reader.Position >= _reader.Length;
        _current.PlaybackProgress = finished ? 1 : _current.PlaybackProgress;
        VoiceNote done = _current;

        // Let go of the device and file at the end; nothing holds audio resources while idle.
        Unload();
        if (finished)
        {
            done.PlaybackProgress = 0;
        }
    }

    private static void StartProgressTimer()
    {
        if (_progressTimer is null)
        {
            _progressTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(50);
            _progressTimer.Tick += (_, _) => UpdateProgress();
        }

        _progressTimer.Start();
    }

    private static void UpdateProgress()
    {
        if (_current is null || _reader is null || _reader.TotalTime <= TimeSpan.Zero)
        {
            return;
        }

        _current.PlaybackProgress = Math.Clamp(_reader.CurrentTime / _reader.TotalTime, 0, 1);
    }
}
