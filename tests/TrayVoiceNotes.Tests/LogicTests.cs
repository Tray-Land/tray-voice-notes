using TrayVoiceNotes.Services;

namespace TrayVoiceNotes.Tests;

public class TrayClickPolicyTests
{
    [Theory]
    [InlineData(RecorderState.Idle, WhileRecordingAction.Stop, TrayRightClickResult.Start)]
    [InlineData(RecorderState.Idle, WhileRecordingAction.Pause, TrayRightClickResult.Start)]
    [InlineData(RecorderState.Recording, WhileRecordingAction.Stop, TrayRightClickResult.Stop)]
    [InlineData(RecorderState.Recording, WhileRecordingAction.Pause, TrayRightClickResult.Pause)]
    [InlineData(RecorderState.Paused, WhileRecordingAction.Pause, TrayRightClickResult.Resume)]
    [InlineData(RecorderState.Paused, WhileRecordingAction.Stop, TrayRightClickResult.Stop)]
    public void RightClickRecords(RecorderState state, WhileRecordingAction whileRecording, TrayRightClickResult expected) =>
        Assert.Equal(expected, TrayClickPolicy.Decide(state, RightClickAction.Record, whileRecording, shiftHeld: false));

    [Theory]
    [InlineData(RecorderState.Idle)]
    [InlineData(RecorderState.Recording)]
    [InlineData(RecorderState.Paused)]
    public void ShiftAlwaysShowsMenu(RecorderState state) =>
        Assert.Equal(TrayRightClickResult.ShowMenu, TrayClickPolicy.Decide(state, RightClickAction.Record, WhileRecordingAction.Stop, shiftHeld: true));

    [Fact]
    public void MenuModeShowsMenu() =>
        Assert.Equal(TrayRightClickResult.ShowMenu, TrayClickPolicy.Decide(RecorderState.Recording, RightClickAction.ShowMenu, WhileRecordingAction.Pause, shiftHeld: false));
}

public class WaveformMathTests
{
    [Fact]
    public void PeaksLandInTheirBuckets()
    {
        PeakAccumulator acc = new(totalSamples: 8, buckets: 4);
        acc.Add([0.1f, -0.5f, 0f, 0f, 0.25f, 0f, 0f, -0.1f]);
        byte[] peaks = acc.ToBytes();

        Assert.Equal(new byte[] { 255, 0, 128, 51 }, peaks);
    }

    [Fact]
    public void SilenceIsNotAmplifiedIntoNoise()
    {
        PeakAccumulator acc = new(totalSamples: 4, buckets: 2);
        acc.Add([0.001f, 0f, 0f, 0.001f]);

        Assert.All(acc.ToBytes(), b => Assert.True(b < 10));
    }

    [Fact]
    public void ExtraSamplesGoToTheLastBucket()
    {
        PeakAccumulator acc = new(totalSamples: 2, buckets: 2);
        acc.Add([0f, 0f, 0.9f]);

        Assert.Equal(255, acc.ToBytes()[1]);
    }

    [Fact]
    public void ResampleKeepsShortSpikes()
    {
        byte[] peaks = [0, 0, 0, 200, 0, 0, 0, 0];
        Assert.Equal(new byte[] { 200, 0 }, WaveformMath.Resample(peaks, 2));
    }

    [Fact]
    public void ResampleCanStretch()
    {
        Assert.Equal(new byte[] { 10, 10, 20, 20 }, WaveformMath.Resample([10, 20], 4));
    }

    [Fact]
    public void ResampleHandlesEmpty() => Assert.Empty(WaveformMath.Resample([], 10));
}

public class TextFormatTests
{
    [Fact]
    public void DurationFormats()
    {
        Assert.Equal("0:07", TextFormat.Duration(TimeSpan.FromSeconds(7)));
        Assert.Equal("12:34", TextFormat.Duration(TimeSpan.FromSeconds(754)));
        Assert.Equal("1:00:05", TextFormat.Duration(TimeSpan.FromSeconds(3605)));
    }

    [Fact]
    public void CleanTranscriptDropsMarkersAndJoins()
    {
        string text = TextFormat.CleanTranscript([" [BLANK_AUDIO]", " Hello there.", "  (music) ", " How are  you?"]);
        Assert.Equal("Hello there. How are you?", text);
    }

    [Fact]
    public void TitleCutsAtWordBoundary()
    {
        string title = TextFormat.Title("Remember to pick up the dry cleaning on the way home from the office tomorrow")!;
        Assert.EndsWith("…", title);
        Assert.True(title.Length <= 49);
        Assert.DoesNotContain("  ", title);
    }

    [Fact]
    public void TitleOfEmptyIsNull() => Assert.Null(TextFormat.Title("   "));

    [Fact]
    public void RecordedAtUsesRelativeDays()
    {
        DateTimeOffset now = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
        Assert.StartsWith("Today", TextFormat.RecordedAt(now.AddHours(-1), now));
        Assert.StartsWith("Yesterday", TextFormat.RecordedAt(now.AddDays(-1), now));
    }
}
