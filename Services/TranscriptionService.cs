using Microsoft.UI.Dispatching;
using TrayVoiceNotes.Models;
using Whisper.net;

namespace TrayVoiceNotes.Services;

/// <summary>
/// Transcribes finished recordings with a local Whisper model, one at a time, in the order they
/// were queued. The model is loaded for a batch and released right after, so the few hundred MB
/// it takes only exist while there's work to do.
/// </summary>
internal static class TranscriptionService
{
    private static readonly Queue<VoiceNote> Pending = new();
    private static DispatcherQueue? _dispatcher;
    private static bool _isRunning;
    private static VoiceNote? _active;
    private static CancellationTokenSource? _activeCts;

    /// <summary>Raised on the UI thread whenever <see cref="StatusText"/> changes.</summary>
    public static event EventHandler? StatusChanged;

    /// <summary>A short line for the tray tooltip and flyout, or null when idle.</summary>
    public static string? StatusText { get; private set; }

    /// <summary>0–1 while the model downloads; null otherwise.</summary>
    public static double? DownloadProgress { get; private set; }

    /// <summary>Set when the last download or model load failed; cleared on the next success.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Queues a note. Call on the UI thread.</summary>
    public static void Enqueue(VoiceNote note)
    {
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        if (_active == note || Pending.Contains(note))
        {
            return;
        }

        note.Status = TranscriptStatus.Pending;
        note.Error = null;
        Pending.Enqueue(note);
        if (!_isRunning)
        {
            _ = RunAsync();
        }
    }

    /// <summary>Drops a queued note, or abandons it if it's being transcribed.</summary>
    public static void Cancel(VoiceNote note)
    {
        if (_active == note)
        {
            _activeCts?.Cancel();
            return;
        }

        if (Pending.Contains(note))
        {
            List<VoiceNote> keep = Pending.Where(n => n != note).ToList();
            Pending.Clear();
            keep.ForEach(Pending.Enqueue);
        }
    }

    /// <summary>Retries everything waiting, e.g. after the model finished downloading elsewhere.</summary>
    public static void RetryFailed()
    {
        foreach (VoiceNote note in NoteStore.Notes.Where(n => n.Status == TranscriptStatus.Failed).Reverse())
        {
            Enqueue(note);
        }
    }

    private static async Task RunAsync()
    {
        _isRunning = true;
        WhisperFactory? factory = null;
        string model = SettingsService.WhisperModel;
        try
        {
            while (Pending.Count > 0)
            {
                VoiceNote note = Pending.Peek();

                // The model might have changed in Settings since the last batch.
                if (factory is null || model != SettingsService.WhisperModel)
                {
                    factory?.Dispose();
                    factory = null;
                    model = SettingsService.WhisperModel;
                    factory = await LoadModelAsync(model);
                    if (factory is null)
                    {
                        // Download or load failed: everything waiting fails with the same reason
                        // and can be retried from the flyout.
                        while (Pending.Count > 0)
                        {
                            VoiceNote failed = Pending.Dequeue();
                            failed.Status = TranscriptStatus.Failed;
                            failed.Error = LastError;
                        }

                        break;
                    }
                }

                Pending.Dequeue();
                await TranscribeAsync(factory, model, note);
            }
        }
        finally
        {
            factory?.Dispose();
            _isRunning = false;
            SetStatus(null);

            // The model and native buffers are the biggest thing this app ever holds.
            MemoryService.ReleaseIdle(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<WhisperFactory?> LoadModelAsync(string model)
    {
        try
        {
            if (!ModelService.IsDownloaded(model))
            {
                WhisperModelInfo info = ModelService.Get(model);
                SetStatus($"Downloading {info.DisplayName} model", 0);
                Progress<double> progress = new(p => SetStatus($"Downloading {info.DisplayName} model", p));
                await ModelService.EnsureAsync(model, progress, CancellationToken.None);
            }

            SetStatus("Loading speech model");
            string path = ModelService.PathFor(model);
            WhisperFactory factory = await Task.Run(() => WhisperFactory.FromPath(path));
            LastError = null;
            return factory;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or TaskCanceledException)
        {
            LastError = "The speech model couldn't be downloaded. Check your connection and try again.";
        }
        catch (Exception ex)
        {
            // A corrupt download fails to load; remove it so the next try downloads it again.
            ModelService.Delete(model);
            LastError = $"The speech model couldn't be loaded ({ex.GetType().Name}).";
        }

        SetStatus(null);
        return null;
    }

    private static async Task TranscribeAsync(WhisperFactory factory, string model, VoiceNote note)
    {
        _active = note;
        _activeCts = new CancellationTokenSource();
        note.Status = TranscriptStatus.Transcribing;
        SetStatus("Transcribing");
        string audioPath = NoteStore.AudioPath(note);
        CancellationToken token = _activeCts.Token;

        try
        {
            string text = await Task.Run(async () =>
            {
                WhisperProcessorBuilder builder = factory.CreateBuilder();
                builder = ModelService.IsMultilingual(model) ? builder.WithLanguageDetection() : builder.WithLanguage("en");
                await using WhisperProcessor processor = builder.Build();
                await using FileStream audio = File.OpenRead(audioPath);

                List<string> segments = [];
                await foreach (SegmentData segment in processor.ProcessAsync(audio, token))
                {
                    segments.Add(segment.Text);
                }

                return TextFormat.CleanTranscript(segments);
            }, token);

            note.Transcript = text;
            note.Status = TranscriptStatus.Done;
        }
        catch (OperationCanceledException)
        {
            // Deleted while transcribing; nothing to report.
        }
        catch (Exception ex)
        {
            note.Error = ex is FileNotFoundException ? "the audio file is missing" : ex.Message;
            note.Status = TranscriptStatus.Failed;
        }
        finally
        {
            _active = null;
            _activeCts.Dispose();
            _activeCts = null;
        }
    }

    private static void SetStatus(string? text, double? downloadProgress = null)
    {
        void Apply()
        {
            StatusText = text;
            DownloadProgress = downloadProgress;
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }

        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcher.TryEnqueue(Apply);
        }
    }
}
