using System.Net.Http;

namespace TrayVoiceNotes.Services;

public sealed record WhisperModelInfo(string Name, string DisplayName, int SizeMB);

/// <summary>
/// Whisper ggml models, downloaded on demand from the whisper.cpp model repository into
/// <c>LocalFolder\Models</c>. Everything after the download runs locally.
/// </summary>
internal static class ModelService
{
    public const string DefaultModel = "base.en";

    private const string DownloadRoot = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    // Downloads are hundreds of MB; the cancellation token bounds them, not a timeout.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public static IReadOnlyList<WhisperModelInfo> Models { get; } =
    [
        new("tiny.en", "Tiny (English)", 75),
        new("base.en", "Base (English)", 142),
        new("small.en", "Small (English)", 466),
        new("tiny", "Tiny (multilingual)", 75),
        new("base", "Base (multilingual)", 142),
        new("small", "Small (multilingual)", 466),
        new("large-v3-turbo-q5_0", "Large v3 Turbo (multilingual)", 547),
    ];

    public static string ModelsFolder { get; } = Directory.CreateDirectory(
        Path.Combine(Path.GetDirectoryName(NoteStore.RecordingsFolder)!, "Models")).FullName;

    public static string Sanitize(string? name) =>
        Models.Any(m => m.Name == name) ? name! : DefaultModel;

    public static WhisperModelInfo Get(string name) => Models.First(m => m.Name == Sanitize(name));

    public static bool IsMultilingual(string name) => !name.EndsWith(".en", StringComparison.Ordinal);

    public static string PathFor(string name) => Path.Combine(ModelsFolder, $"ggml-{name}.bin");

    public static bool IsDownloaded(string name) => File.Exists(PathFor(name));

    public static void Delete(string name) => TryDelete(PathFor(name));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // In use by a transcription; it'll go next time.
        }
    }

    /// <summary>Downloads the model if it isn't there yet. Progress is 0–1, on a background thread.</summary>
    public static async Task<string> EnsureAsync(string name, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string path = PathFor(name);
        if (File.Exists(path))
        {
            return path;
        }

        await DownloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            string partial = path + ".part";
            using HttpResponseMessage response = await Http.GetAsync(
                $"{DownloadRoot}ggml-{name}.bin", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;

            try
            {
                await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (FileStream target = new(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    byte[] buffer = new byte[1 << 16];
                    long read = 0;
                    int lastPercent = -1;
                    int n;
                    while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                        read += n;
                        if (total is > 0)
                        {
                            int percent = (int)(read * 100 / total.Value);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                progress?.Report(percent / 100.0);
                            }
                        }
                    }
                }
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            DownloadLock.Release();
        }
    }
}
