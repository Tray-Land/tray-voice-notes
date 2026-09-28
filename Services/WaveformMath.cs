namespace TrayVoiceNotes.Services;

/// <summary>
/// Reduces audio samples to a fixed number of peak buckets for drawing a waveform, and resamples
/// those buckets to whatever bar count fits the control. No audio or WinRT dependencies.
/// </summary>
public sealed class PeakAccumulator
{
    // Quiet recordings are normalized up so they're visible, but not past this gain, or silence
    // turns into a wall of noise.
    private const float MinReferencePeak = 0.05f;

    private readonly long _totalSamples;
    private readonly float[] _peaks;
    private long _index;

    public PeakAccumulator(long totalSamples, int buckets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);
        _totalSamples = Math.Max(1, totalSamples);
        _peaks = new float[buckets];
    }

    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            long bucket = _index * _peaks.Length / _totalSamples;
            if (bucket >= _peaks.Length)
            {
                bucket = _peaks.Length - 1;
            }

            float magnitude = Math.Abs(sample);
            if (magnitude > _peaks[bucket])
            {
                _peaks[bucket] = magnitude;
            }

            _index++;
        }
    }

    /// <summary>Peaks scaled to 0–255, normalized to the loudest bucket.</summary>
    public byte[] ToBytes()
    {
        float max = MinReferencePeak;
        foreach (float p in _peaks)
        {
            max = Math.Max(max, p);
        }

        byte[] result = new byte[_peaks.Length];
        for (int i = 0; i < _peaks.Length; i++)
        {
            result[i] = (byte)Math.Clamp(MathF.Round(Math.Min(1f, _peaks[i] / max) * 255f), 0, 255);
        }

        return result;
    }
}

public static class WaveformMath
{
    /// <summary>
    /// Resamples stored peaks to <paramref name="count"/> bars, taking the max of each span so
    /// short sounds don't vanish when shrinking.
    /// </summary>
    public static byte[] Resample(ReadOnlySpan<byte> peaks, int count)
    {
        if (count <= 0 || peaks.IsEmpty)
        {
            return [];
        }

        byte[] result = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int start = (int)((long)i * peaks.Length / count);
            int end = (int)((long)(i + 1) * peaks.Length / count);
            end = Math.Max(end, start + 1);

            byte max = 0;
            for (int j = start; j < end && j < peaks.Length; j++)
            {
                max = Math.Max(max, peaks[j]);
            }

            result[i] = max;
        }

        return result;
    }
}
