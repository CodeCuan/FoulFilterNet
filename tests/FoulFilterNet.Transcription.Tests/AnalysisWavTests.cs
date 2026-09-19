namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// A minute of 16 kHz mono audio, with the <c>LIST</c> chunk FFmpeg writes in
/// front of the data. Every frame's sample is its own index modulo 30000, so a
/// sample read back says exactly which frame it came from.
/// </summary>
public sealed class WhenOpeningAMinuteOfAnalysisAudio : IDisposable
{
    private const int Rate = 16000;

    private readonly TempDirectory _directory = new();
    private readonly AnalysisWav _sut;

    public WhenOpeningAMinuteOfAnalysisAudio()
    {
        var path = Path.Combine(_directory.Path, "analysis.wav");
        TestWav.Write(path, 60L * Rate, (frame, _) => (short)(frame % 30000), withListChunk: true);

        _sut = AnalysisWav.OpenAsync(path, TestContext.Current.CancellationToken).Result;

        _sut.Windows.Count.ShouldBe(3);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void MeasuresItsDurationFromItsFrames() => _sut.DurationSeconds.ShouldBe(60.0);

    [Fact]
    public void PlansItsWindowsTheWayTheBatchPathAlwaysHas() =>
        _sut.Windows.ShouldBe(TranscriptionWindows.Plan(60.0));

    [Fact]
    public async Task ReadsAWholeWindowOfSamples() =>
        (await _sut.ReadWindowAsync(1, TestContext.Current.CancellationToken)).Length.ShouldBe(
            28 * Rate
        );

    [Fact]
    public async Task StartsTheWindowAtItsOwnFrame() =>
        (await _sut.ReadWindowAsync(1, TestContext.Current.CancellationToken))[0]
            .ShouldBe((22 * Rate % 30000) / 32768f);

    [Fact]
    public async Task EndsTheWindowAtItsLastFrame() =>
        (await _sut.ReadWindowAsync(1, TestContext.Current.CancellationToken))[^1]
            .ShouldBe(((50 * Rate - 1) % 30000) / 32768f);

    [Fact]
    public async Task StartsTheFirstWindowAtTheFirstFrameAfterTheHeader() =>
        (await _sut.ReadWindowAsync(0, TestContext.Current.CancellationToken))[0].ShouldBe(0f);

    [Fact]
    public async Task EndsTheLastWindowWithTheFile() =>
        (await _sut.ReadWindowAsync(2, TestContext.Current.CancellationToken))[^1]
            .ShouldBe(((60 * Rate - 1) % 30000) / 32768f);

    [Fact]
    public async Task ReadsTheLastWindowWhole() =>
        (await _sut.ReadWindowAsync(2, TestContext.Current.CancellationToken)).Length.ShouldBe(
            28 * Rate
        );

    [Fact]
    public async Task ReadsTheSameWindowTheSameWayTwice()
    {
        var first = await _sut.ReadWindowAsync(1, TestContext.Current.CancellationToken);
        var second = await _sut.ReadWindowAsync(1, TestContext.Current.CancellationToken);

        second.ShouldBe(first);
    }

    [Fact]
    public async Task ReadsWindowsInAnyOrder()
    {
        _ = await _sut.ReadWindowAsync(2, TestContext.Current.CancellationToken);

        (await _sut.ReadWindowAsync(0, TestContext.Current.CancellationToken))[0].ShouldBe(0f);
    }

    [Fact]
    public async Task ReadsWindowsConcurrentlyWithoutMixingThemUp()
    {
        var reads = Enumerable
            .Range(0, 12)
            .Select(i =>
                Task.Run(() => _sut.ReadWindowAsync(i % 3, TestContext.Current.CancellationToken))
            )
            .ToArray();

        var windows = await Task.WhenAll(reads);

        windows
            .Select(w => w[0])
            .ShouldBe(
                Enumerable
                    .Range(0, 12)
                    .Select(i => new[] { 0f, 22000 / 32768f, 2000 / 32768f }[i % 3])
            );
    }

    [Fact]
    public async Task RefusesAWindowBeforeThePlan() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _sut.ReadWindowAsync(-1, TestContext.Current.CancellationToken)
        );

    [Fact]
    public async Task RefusesAWindowPastThePlan() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _sut.ReadWindowAsync(3, TestContext.Current.CancellationToken)
        );

    [Fact]
    public async Task StopsWhenCancelled() =>
        await Should.ThrowAsync<OperationCanceledException>(() =>
            _sut.ReadWindowAsync(0, new CancellationToken(canceled: true))
        );

    [Fact]
    public void LeavesTheFileReadableByOthers()
    {
        using var other = new FileStream(
            Path.Combine(_directory.Path, "analysis.wav"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );

        other.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task RefusesToReadOnceClosed()
    {
        _sut.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(() =>
            _sut.ReadWindowAsync(0, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public void ToleratesBeingClosedTwice()
    {
        _sut.Dispose();

        Should.NotThrow(_sut.Dispose);
    }
}

/// <summary>
/// whisper.cpp hears mono, so a stereo file's channels are averaged the way
/// Whisper.net's own parser averages them.
/// </summary>
public sealed class WhenReadingAStereoAnalysisWav : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly AnalysisWav _sut;
    private readonly float[] _samples;

    public WhenReadingAStereoAnalysisWav()
    {
        var path = Path.Combine(_directory.Path, "stereo.wav");
        TestWav.Write(
            path,
            5 * 16000,
            (_, channel) => channel == 0 ? (short)1000 : (short)3000,
            channels: 2
        );

        _sut = AnalysisWav.OpenAsync(path, TestContext.Current.CancellationToken).Result;
        _samples = _sut.ReadWindowAsync(0, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void CountsFramesNotSamples() => _sut.DurationSeconds.ShouldBe(5.0);

    [Fact]
    public void ReadsOneSamplePerFrame() => _samples.Length.ShouldBe(5 * 16000);

    [Fact]
    public void AveragesTheChannels() => _samples.ShouldAllBe(s => s == 2000 / 32768f);
}

/// <summary>
/// The extremes of 16-bit PCM map onto [-1, 1) exactly as before.
/// </summary>
public sealed class WhenReadingFullScaleSamples : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly float[] _samples;

    public WhenReadingFullScaleSamples()
    {
        var path = Path.Combine(_directory.Path, "loud.wav");
        TestWav.Write(path, 4, (frame, _) => frame % 2 == 0 ? short.MinValue : short.MaxValue);

        using var sut = AnalysisWav.OpenAsync(path, TestContext.Current.CancellationToken).Result;
        _samples = sut.ReadWindowAsync(0, TestContext.Current.CancellationToken).Result;

        _samples.Length.ShouldBe(4);
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void MapsTheMostNegativeSampleToMinusOne() => _samples[0].ShouldBe(-1f);

    [Fact]
    public void MapsTheMostPositiveSampleJustBelowOne() => _samples[1].ShouldBe(32767 / 32768f);
}

/// <summary>A clip shorter than one window is one window, and all of it is read.</summary>
public sealed class WhenTheAnalysisWavIsShorterThanAWindow : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly AnalysisWav _sut;

    public WhenTheAnalysisWavIsShorterThanAWindow()
    {
        var path = Path.Combine(_directory.Path, "short.wav");
        TestWav.Write(path, 8 * 16000 + 8, (_, _) => 100);

        _sut = AnalysisWav.OpenAsync(path, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void KeepsTheFractionOfASecond() => _sut.DurationSeconds.ShouldBe(8.0005);

    [Fact]
    public void IsOneWindow() => _sut.Windows.ShouldHaveSingleItem();

    [Fact]
    public async Task ReadsEveryFrame() =>
        (await _sut.ReadWindowAsync(0, TestContext.Current.CancellationToken)).Length.ShouldBe(
            8 * 16000 + 8
        );
}

/// <summary>
/// A WAV with a header and no audio is a real possibility - a silent zero-length
/// source - and must not break the plan or the read.
/// </summary>
public sealed class WhenTheAnalysisWavHoldsNoAudio : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly AnalysisWav _sut;

    public WhenTheAnalysisWavHoldsNoAudio()
    {
        var path = Path.Combine(_directory.Path, "empty.wav");
        TestWav.Write(path, 0, (_, _) => 0);

        _sut = AnalysisWav.OpenAsync(path, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void LastsNoTime() => _sut.DurationSeconds.ShouldBe(0.0);

    [Fact]
    public void StillHasTheOneWindowThePlanAlwaysGives() => _sut.Windows.ShouldHaveSingleItem();

    [Fact]
    public async Task ReadsNothingFromIt() =>
        (await _sut.ReadWindowAsync(0, TestContext.Current.CancellationToken)).ShouldBeEmpty();
}

/// <summary>
/// A file cut short - its header promises more data than is there - reads what
/// there is rather than failing, as the engine always has.
/// </summary>
public sealed class WhenTheAnalysisWavIsTruncated : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly AnalysisWav _sut;

    public WhenTheAnalysisWavIsTruncated()
    {
        var path = Path.Combine(_directory.Path, "cut.wav");
        TestWav.Write(path, 60L * 16000, (_, _) => 1);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(stream.Length - 10 * 16000 * sizeof(short));
        }

        _sut = AnalysisWav.OpenAsync(path, TestContext.Current.CancellationToken).Result;
    }

    public void Dispose()
    {
        _sut.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void BelievesTheHeaderAboutTheDuration() => _sut.DurationSeconds.ShouldBe(60.0);

    [Fact]
    public async Task ReadsOnlyWhatIsThereOfTheLastWindow() =>
        (await _sut.ReadWindowAsync(2, TestContext.Current.CancellationToken)).Length.ShouldBe(
            18 * 16000
        );
}

public sealed class WhenAnAnalysisWavCannotBeUsed : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task RefusesABlankPath() =>
        await Should.ThrowAsync<ArgumentException>(() =>
            AnalysisWav.OpenAsync(" ", TestContext.Current.CancellationToken)
        );

    [Fact]
    public async Task ReportsAMissingFile() =>
        await Should.ThrowAsync<FileNotFoundException>(() =>
            AnalysisWav.OpenAsync(
                Path.Combine(_directory.Path, "nowhere.wav"),
                TestContext.Current.CancellationToken
            )
        );

    [Fact]
    public async Task RefusesAnythingButSixteenBitPcm()
    {
        var path = Path.Combine(_directory.Path, "eight.wav");
        TestWav.WriteRaw(path, new byte[1600], sampleRate: 16000, channels: 1, bitsPerSample: 8);

        (
            await Should.ThrowAsync<NotSupportedException>(() =>
                AnalysisWav.OpenAsync(path, TestContext.Current.CancellationToken)
            )
        ).Message.ShouldContain("8-bit");
    }

    [Fact]
    public async Task StopsWhenCancelledBeforeOpening()
    {
        var path = Path.Combine(_directory.Path, "fine.wav");
        TestWav.Write(path, 16000, (_, _) => 0);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            AnalysisWav.OpenAsync(path, new CancellationToken(canceled: true))
        );
    }
}

/// <summary>
/// W17: the first two minutes of a video's analysis audio, converted on their
/// own, read exactly the same samples for every window the head fixes as the
/// whole file does - which is why a Watch Session may hear those windows
/// before the whole conversion has finished. The head is the whole file's
/// first frames, as FFmpeg's <c>-t</c> writes them (measured byte-identical
/// on the 60-minute W01 video).
/// </summary>
public sealed class WhenReadingTheFixedWindowsOfAHead : IDisposable
{
    private const int Rate = 16000;

    private readonly TempDirectory _directory = new();
    private readonly AnalysisWav _whole;
    private readonly AnalysisWav _head;
    private readonly int _fixed;

    public WhenReadingTheFixedWindowsOfAHead()
    {
        var whole = Path.Combine(_directory.Path, "whole.wav");
        var head = Path.Combine(_directory.Path, "head.wav");

        // Noise, so a window read from the wrong place cannot match by chance.
        static short Sample(long frame, int channel) =>
            (short)((frame * 7919 + channel * 104729) % 65536 - 32768);
        TestWav.Write(whole, 400L * Rate + 1234, Sample, withListChunk: true);
        TestWav.Write(head, 120L * Rate, Sample, withListChunk: true);

        _whole = AnalysisWav.OpenAsync(whole, TestContext.Current.CancellationToken).Result;
        _head = AnalysisWav.OpenAsync(head, TestContext.Current.CancellationToken).Result;
        _fixed = TranscriptionWindows.FixedPrefixCount(_head.DurationSeconds);
    }

    public void Dispose()
    {
        _whole.Dispose();
        _head.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void FixesFourWindows() => _fixed.ShouldBe(4);

    [Fact]
    public void PlansThemAsTheWholeFileDoes() =>
        _head.Windows.Take(_fixed).ShouldBe(_whole.Windows.Take(_fixed));

    [Fact]
    public async Task ReadsTheSameSamplesForEachOfThem()
    {
        for (var i = 0; i < _fixed; i++)
        {
            (await _head.ReadWindowAsync(i, TestContext.Current.CancellationToken)).ShouldBe(
                await _whole.ReadWindowAsync(i, TestContext.Current.CancellationToken),
                $"window {i}"
            );
        }
    }

    [Fact]
    public async Task ReadsWholeWindowsForEachOfThem()
    {
        for (var i = 0; i < _fixed; i++)
        {
            (await _head.ReadWindowAsync(i, TestContext.Current.CancellationToken)).Length.ShouldBe(
                (int)(TranscriptionWindows.LengthSeconds * Rate)
            );
        }
    }

    [Fact]
    public void PlansTheNextWindowDifferently() =>
        _head.Windows[_fixed].ShouldNotBe(_whole.Windows[_fixed]);
}
