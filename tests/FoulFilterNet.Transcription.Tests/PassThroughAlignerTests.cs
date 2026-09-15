using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>Records progress synchronously, unlike <see cref="Progress{T}"/>.</summary>
internal sealed class RecordingProgress : IProgress<JobProgress>
{
    public List<JobProgress> Reports { get; } = [];

    public void Report(JobProgress value) => Reports.Add(value);
}

/// <summary>
/// The whisper.cpp engine emits word timestamps itself, so the alignment stage
/// has nothing left to do. It survives as a seam: these tests pin the behaviour
/// a real forced aligner would have to preserve if DTW boundaries ever prove
/// too loose. See docs/01-python-analysis.md 9.1.
/// </summary>
public class WhenAligningATranscriptThatAlreadyHasWords
{
    private const string NoSuchAudio = "no-such-audio.wav";

    private readonly IReadOnlyList<Word> _transcribed;
    private readonly IReadOnlyList<Word> _aligned;
    private readonly RecordingProgress _progress = new();

    public WhenAligningATranscriptThatAlreadyHasWords()
    {
        var aligner = new PassThroughAligner();
        _transcribed =
        [
            new Word("well", 3.100, 3.409),
            new Word("damn", 3.410, 3.897),
            new Word("that", 3.900, 4.120),
        ];

        aligner.ShouldBeAssignableTo<IAligner>();

        // The audio is deliberately absent: a pass-through must never open it.
        File.Exists(NoSuchAudio).ShouldBeFalse();

        _aligned = aligner.AlignAsync(
            NoSuchAudio,
            [new Segment(3.1, 4.12, "well damn that")],
            _transcribed,
            _progress).GetAwaiter().GetResult();

        _aligned.ShouldNotBeNull();
        _aligned.ShouldNotBeEmpty();
    }

    [Fact]
    public void ReturnsEveryWordItWasGiven() => _aligned.Count.ShouldBe(3);

    [Fact]
    public void LeavesTheTimestampsUntouched() =>
        _aligned[1].ShouldBe(new Word("damn", 3.410, 3.897));

    [Fact]
    public void PreservesTheOrder() =>
        _aligned.Select(w => w.Text).ShouldBe(["well", "damn", "that"]);

    [Fact]
    public void DoesNotCopyTheList() => _aligned.ShouldBeSameAs(_transcribed);

    [Fact]
    public void ReportsNoProgressBecauseItDoesNoWork() => _progress.Reports.ShouldBeEmpty();
}

/// <summary>
/// The stage must be a genuine no-op rather than a failure: the pipeline has to
/// be able to substitute a pass-through for no alignment at all and see no
/// difference. An engine that produced no words simply leaves the transcript
/// word-less; inventing them is a real aligner's job, not this seam's.
/// </summary>
public class WhenAligningATranscriptWithNoWords
{
    private const string NoSuchAudio = "no-such-audio.wav";

    private readonly IReadOnlyList<Word> _aligned;

    public WhenAligningATranscriptWithNoWords()
    {
        var aligner = new PassThroughAligner();

        File.Exists(NoSuchAudio).ShouldBeFalse();

        _aligned = aligner.AlignAsync(
            NoSuchAudio,
            [new Segment(0.0, 2.0, "a segment no engine gave words for")],
            []).GetAwaiter().GetResult();

        _aligned.ShouldNotBeNull();
    }

    [Fact]
    public void ProducesNoWordsRatherThanThrowing() => _aligned.ShouldBeEmpty();
}

public class WhenThePassThroughAlignerIsCancelled
{
    private readonly PassThroughAligner _aligner = new();
    private readonly IReadOnlyList<Word> _words = [new Word("damn", 1.0, 1.4)];

    public WhenThePassThroughAlignerIsCancelled()
    {
        _words.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ObservesTheTokenLikeAnyOtherStage() =>
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _aligner.AlignAsync(
                "no-such-audio.wav",
                [],
                _words,
                progress: null,
                new CancellationToken(canceled: true)));

    [Fact]
    public async Task RunsNormallyWhileTheTokenIsLive()
    {
        var aligned = await _aligner.AlignAsync(
            "no-such-audio.wav",
            [],
            _words,
            progress: null,
            TestContext.Current.CancellationToken);

        aligned.ShouldBeSameAs(_words);
    }
}

public class WhenReleasingThePassThroughAligner
{
    private readonly PassThroughAligner _aligner = new();

    public WhenReleasingThePassThroughAligner()
    {
        _aligner.ReleaseAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task HoldsNothingToReleaseSoItIsRepeatable()
    {
        await _aligner.ReleaseAsync();
        await _aligner.ReleaseAsync();
    }

    [Fact]
    public async Task StillAlignsAfterBeingReleased()
    {
        var aligned = await _aligner.AlignAsync(
            "no-such-audio.wav",
            [],
            [new Word("damn", 1.0, 1.4)],
            progress: null,
            TestContext.Current.CancellationToken);

        aligned.ShouldHaveSingleItem();
    }
}
