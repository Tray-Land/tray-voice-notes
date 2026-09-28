using NAudio.Wave;

namespace TrayVoiceNotes.Services;

internal static class AudioFileService
{
    /// <summary>Stored peak resolution; the view resamples it to the bars that fit.</summary>
    public const int PeakBuckets = 240;

    /// <summary>Reads the WAV with NAudio and reduces it to <see cref="PeakBuckets"/> peaks.</summary>
    public static byte[] ComputePeaks(string path)
    {
        try
        {
            using WaveFileReader reader = new(path);
            ISampleProvider samples = reader.ToSampleProvider();
            long total = reader.SampleCount * reader.WaveFormat.Channels;
            PeakAccumulator peaks = new(total, PeakBuckets);

            float[] buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 10];
            int read;
            while ((read = samples.Read(buffer)) > 0)
            {
                peaks.Add(buffer.AsSpan(0, read));
            }

            return peaks.ToBytes();
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
