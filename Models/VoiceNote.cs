using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using TrayVoiceNotes.Services;

namespace TrayVoiceNotes.Models;

public enum TranscriptStatus
{
    Pending,
    Transcribing,
    Done,
    Failed,
}

/// <summary>
/// One recording: the WAV file on disk plus its transcript, notes, and cached waveform peaks.
/// Persisted in the note index; the non-serialized properties are live UI state.
/// </summary>
public sealed class VoiceNote : INotifyPropertyChanged
{
    private string _transcript = string.Empty;
    private string _notes = string.Empty;
    private TranscriptStatus _status;
    private string? _error;
    private byte[] _peaks = [];
    private double _durationSeconds;
    private bool _isPlaying;
    private double _playbackProgress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (Set(ref _durationSeconds, value))
            {
                Notify(nameof(DurationText), nameof(Subtitle));
            }
        }
    }

    public string Transcript
    {
        get => _transcript;
        set
        {
            if (Set(ref _transcript, value ?? string.Empty))
            {
                Notify(nameof(Title), nameof(Preview), nameof(HasPreview));
            }
        }
    }

    public string Notes
    {
        get => _notes;
        set
        {
            if (Set(ref _notes, value ?? string.Empty))
            {
                Notify(nameof(HasNotes));
            }
        }
    }

    public TranscriptStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Notify(nameof(Preview), nameof(HasPreview), nameof(IsBusy), nameof(StatusText), nameof(HasStatus));
            }
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value))
            {
                Notify(nameof(StatusText));
            }
        }
    }

    /// <summary>Normalized waveform peaks (0–255), computed once after recording.</summary>
    public byte[] Peaks
    {
        get => _peaks;
        set => Set(ref _peaks, value ?? []);
    }

    [JsonIgnore]
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (Set(ref _isPlaying, value))
            {
                Notify(nameof(PlayGlyph), nameof(PlayLabel));
            }
        }
    }

    /// <summary>0–1 through the file while it's the current playback item.</summary>
    [JsonIgnore]
    public double PlaybackProgress
    {
        get => _playbackProgress;
        set => Set(ref _playbackProgress, value);
    }

    [JsonIgnore]
    public string Title => TextFormat.Title(Transcript) ?? "Voice note";

    [JsonIgnore]
    public string DurationText => TextFormat.Duration(TimeSpan.FromSeconds(DurationSeconds));

    [JsonIgnore]
    public string Subtitle => $"{TextFormat.RecordedAt(CreatedAt, DateTimeOffset.Now)} · {DurationText}";

    [JsonIgnore]
    public string Preview => Status == TranscriptStatus.Done ? Transcript : string.Empty;

    [JsonIgnore]
    public bool HasPreview => Status == TranscriptStatus.Done && Transcript.Length > 0;

    [JsonIgnore]
    public bool HasNotes => Notes.Length > 0;

    [JsonIgnore]
    public bool IsBusy => Status == TranscriptStatus.Transcribing;

    [JsonIgnore]
    public bool HasStatus => Status != TranscriptStatus.Done;

    [JsonIgnore]
    public string StatusText => Status switch
    {
        TranscriptStatus.Pending => "Waiting to transcribe",
        TranscriptStatus.Transcribing => "Transcribing…",
        TranscriptStatus.Failed => Error is { Length: > 0 } ? $"Couldn't transcribe: {Error}" : "Couldn't transcribe",
        _ => string.Empty,
    };

    [JsonIgnore]
    public string PlayGlyph => IsPlaying ? "" : "";

    [JsonIgnore]
    public string PlayLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>Screen readers read list items by ToString.</summary>
    public override string ToString() => $"{Title}, {Subtitle}";

    /// <summary>Refreshes relative dates ("Today") when the list is shown again.</summary>
    public void RefreshSubtitle() => Notify(nameof(Subtitle));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void Notify(params string[] names)
    {
        foreach (string name in names)
        {
            OnPropertyChanged(name);
        }
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
