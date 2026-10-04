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

public class NoteSearchTests
{
    [Theory]
    [InlineData("Buy milk and eggs tomorrow", "MILK", true)]
    [InlineData("Buy milk and eggs tomorrow", "eggs milk", true)]
    [InlineData("Buy milk and eggs tomorrow", "milk bread", false)]
    [InlineData("Let's meet at the café", "cafe", true)]
    [InlineData("anything", "   ", true)]
    [InlineData("", "milk", false)]
    public void Matches_Query_ExpectedResult(string transcript, string query, bool expected) =>
        Assert.Equal(expected, NoteSearch.Matches(transcript, query));
}

public class NoteExportTests
{
    [Fact]
    public void BaseName_CreatedAt_UsesDateAndTime() =>
        Assert.Equal("Voice note 2026-10-04 1530", NoteExport.BaseName(new DateTimeOffset(2026, 10, 4, 15, 30, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData("some notes", new[] { "n.wav", "transcript.txt", "notes.txt" })]
    [InlineData("  ", new[] { "n.wav", "transcript.txt" })]
    public async Task WriteZipAsync_Notes_ContainsExpectedEntries(string notes, string[] expected)
    {
        string wav = Path.GetTempFileName();
        await File.WriteAllBytesAsync(wav, [1, 2, 3, 4]);
        try
        {
            using MemoryStream output = new();
            await NoteExport.WriteZipAsync(output, wav, "n", "hello transcript", notes);

            output.Position = 0;
            using System.IO.Compression.ZipArchive zip = new(output);
            Assert.Equal(expected, zip.Entries.Select(e => e.FullName).ToArray());

            using StreamReader reader = new(zip.GetEntry("transcript.txt")!.Open());
            Assert.Equal("hello transcript", await reader.ReadToEndAsync());
            using MemoryStream audio = new();
            await zip.GetEntry("n.wav")!.Open().CopyToAsync(audio);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, audio.ToArray());
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task CopyAudioAsync_FileOpenElsewhere_StillCopies()
    {
        string wav = Path.GetTempFileName();
        await File.WriteAllBytesAsync(wav, [9, 8, 7]);
        try
        {
            await using FileStream other = new(wav, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using MemoryStream output = new();
            await NoteExport.CopyAudioAsync(wav, output);
            Assert.Equal(new byte[] { 9, 8, 7 }, output.ToArray());
        }
        finally
        {
            File.Delete(wav);
        }
    }
}

public class NoteSearchHighlightTests
{
    [Fact]
    public void FindRanges_RepeatedTerm_FindsEveryOccurrenceIgnoringCase()
    {
        var ranges = NoteSearch.FindRanges("Milk, more milk", "milk");
        Assert.Equal([(0, 4), (11, 4)], ranges);
    }

    [Fact]
    public void FindRanges_OverlappingTerms_AreMerged()
    {
        var ranges = NoteSearch.FindRanges("buttermilk", "butter milk buttermilk");
        Assert.Equal([(0, 10)], ranges);
    }

    [Fact]
    public void FindRanges_NoMatch_ReturnsEmpty() =>
        Assert.Empty(NoteSearch.FindRanges("hello", "xyz"));

    [Fact]
    public void Snippet_MatchNearStart_ReturnsTextUnchanged() =>
        Assert.Equal("Buy milk today", NoteSearch.Snippet("Buy milk today", "milk"));

    [Fact]
    public void Snippet_MatchFarIn_StartsBeforeMatchAtWordBoundary()
    {
        string text = "one two three four five six seven eight nine ten eleven twelve needle after";
        string snippet = NoteSearch.Snippet(text, "needle");

        Assert.StartsWith("…", snippet);
        Assert.Contains("needle after", snippet);
        Assert.True(snippet.Length < text.Length);
    }
}
