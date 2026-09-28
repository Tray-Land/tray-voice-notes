using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using TrayVoiceNotes.Models;

namespace TrayVoiceNotes.Services;

/// <summary>
/// Records the microphone straight to a 16 kHz mono 16-bit WAV, the format Whisper takes, so
/// nothing needs converting before transcription. Pausing closes the device (the microphone
/// indicator goes away) and keeps the file open; resuming appends to it.
/// </summary>
internal static class RecordingService
{
    private static readonly WaveFormat Format = new(16000, 16, 1);
    private static readonly TimeSpan MinimumLength = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private static readonly Lock WriterLock = new();
    private static readonly Stopwatch Clock = new();

    private static WaveInEvent? _device;
    private static TaskCompletionSource? _deviceStopped;
    private static WaveFileWriter? _writer;
    private static string? _currentId;

    /// <summary>Raised on the UI thread.</summary>
    public static event EventHandler? StateChanged;

    /// <summary>Peak input level 0–1, raised on the audio thread about 20 times a second.</summary>
    public static event EventHandler<float>? LevelChanged;

    public static RecorderState State { get; private set; }

    public static TimeSpan Elapsed => Clock.Elapsed;

    public static IReadOnlyList<(int Number, string Name)> GetMicrophones()
    {
        List<(int, string)> list = [(-1, "Default microphone")];
        try
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                list.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Enumeration is best-effort; the default device still works.
        }

        return list;
    }

    /// <summary>Starts a new recording. Throws when there's no usable microphone.</summary>
    public static void Start()
    {
        if (State != RecorderState.Idle)
        {
            return;
        }

        PlaybackService.Pause();
        _currentId = Guid.NewGuid().ToString("N");
        _writer = new WaveFileWriter(NoteStore.AudioPath(_currentId), Format);
        try
        {
            OpenDevice();
        }
        catch
        {
            DiscardFile();
            throw;
        }

        Clock.Restart();
        SetState(RecorderState.Recording);
    }

    public static async Task PauseAsync()
    {
        if (State != RecorderState.Recording)
        {
            return;
        }

        Clock.Stop();
        SetState(RecorderState.Paused);
        await CloseDeviceAsync();
    }

    public static void Resume()
    {
        if (State != RecorderState.Paused)
        {
            return;
        }

        OpenDevice();
        Clock.Start();
        SetState(RecorderState.Recording);
    }

    /// <summary>Finishes the recording, adds it to the list, and queues it for transcription.</summary>
    public static async Task<VoiceNote?> StopAsync()
    {
        if (State == RecorderState.Idle)
        {
            return null;
        }

        Clock.Stop();
        SetState(RecorderState.Idle);
        await CloseDeviceAsync();

        TimeSpan length;
        lock (WriterLock)
        {
            length = _writer?.TotalTime ?? TimeSpan.Zero;
            _writer?.Dispose();
            _writer = null;
        }

        string id = _currentId!;
        _currentId = null;
        if (length < MinimumLength)
        {
            // An accidental double right-click; not worth a list entry.
            DeleteAudio(id);
            return null;
        }

        VoiceNote note = new() { Id = id, DurationSeconds = length.TotalSeconds };
        note.Peaks = await Task.Run(() => AudioFileService.ComputePeaks(NoteStore.AudioPath(id)));
        NoteStore.Add(note);
        TranscriptionService.Enqueue(note);
        return note;
    }

    /// <summary>Stops and throws the recording away.</summary>
    public static async Task CancelAsync()
    {
        if (State == RecorderState.Idle)
        {
            return;
        }

        Clock.Stop();
        SetState(RecorderState.Idle);
        await CloseDeviceAsync();
        DiscardFile();
    }

    private static void OpenDevice()
    {
        int deviceNumber = SettingsService.MicrophoneDevice;

        // A stale device number (headset unplugged) falls back to the default microphone.
        if (deviceNumber >= WaveInEvent.DeviceCount)
        {
            deviceNumber = -1;
        }

        WaveInEvent device = new()
        {
            DeviceNumber = deviceNumber,
            WaveFormat = Format,
            BufferMilliseconds = 50,
        };

        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        device.DataAvailable += Device_DataAvailable;
        device.RecordingStopped += (_, _) => stopped.TrySetResult();
        try
        {
            device.StartRecording();
        }
        catch
        {
            device.DataAvailable -= Device_DataAvailable;
            device.Dispose();
            throw;
        }

        _device = device;
        _deviceStopped = stopped;
    }

    private static async Task CloseDeviceAsync()
    {
        WaveInEvent? device = _device;
        Task stopped = _deviceStopped?.Task ?? Task.CompletedTask;
        _device = null;
        _deviceStopped = null;
        if (device is null)
        {
            return;
        }

        device.StopRecording();

        // The last buffers arrive before RecordingStopped; wait so the file keeps them.
        await Task.WhenAny(stopped, Task.Delay(StopTimeout));
        device.DataAvailable -= Device_DataAvailable;
        device.Dispose();
    }

    private static void Device_DataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (WriterLock)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        EventHandler<float>? handler = LevelChanged;
        if (handler is null)
        {
            return;
        }

        int peak = 0;
        foreach (short s in MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded)))
        {
            peak = Math.Max(peak, Math.Abs((int)s));
        }

        handler(null, Math.Min(1f, peak / (float)short.MaxValue));
    }

    private static void DiscardFile()
    {
        lock (WriterLock)
        {
            _writer?.Dispose();
            _writer = null;
        }

        if (_currentId is { } id)
        {
            _currentId = null;
            DeleteAudio(id);
        }
    }

    private static void DeleteAudio(string id)
    {
        try
        {
            File.Delete(NoteStore.AudioPath(id));
        }
        catch (IOException)
        {
            // Harmless leftover.
        }
    }

    private static void SetState(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke(null, EventArgs.Empty);
    }
}
