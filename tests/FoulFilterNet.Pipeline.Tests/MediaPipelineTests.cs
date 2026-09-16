using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Media;
using NSubstitute;

namespace FoulFilterNet.Pipeline.Tests;

/// <summary>
/// The whole spine for the ordinary case: an audio file with one profanity in
/// it, transcribed, aligned, merged and rendered.
/// </summary>
/// <remarks>
/// The percentages asserted here are a contract, not decoration - the front end
/// renders them directly, and they are the ones in docs/01-python-analysis.md 3.
/// </remarks>
public sealed class WhenCensoringAnAudioFileEndToEnd : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenCensoringAnAudioFileEndToEnd()
    {
        _summary = _harness.Run();

        _summary.ShouldNotBeNull();
        _harness.Checkpoints.ShouldNotBeEmpty();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void WalksTheStagesAtTheExactPercentagesTheUiRenders() => _harness.Stages.ShouldBe(
    [
        ("transcribing", 5),
        ("transcribing", 40),
        ("matching", 50),
        ("aligning", 55),
        ("aligning", 75),
        ("editing", 88),
        ("completed", 100),
    ]);

    [Fact]
    public void NeverPreparesAudioForAFileThatIsAlreadyAudio() =>
        _harness.Checkpoints.ShouldNotContain(c => c.Stage == "preparing");

    [Fact]
    public void ReportsHowManySegmentsWereHeard() =>
        _harness.Checkpoints.First(c => c.Percent == 40).Detail.ShouldContain("1 segment");

    [Fact]
    public void CensorsTheOneProfanityItFound() => _summary.Hits.Count.ShouldBe(1);

    [Fact]
    public void PadsTheHitBeforeRenderingIt() => _summary.Hits[0].Start.ShouldBe(0.85, 0.001);

    [Fact]
    public void CountsTheWordsItAligned() => _summary.TranscriptWordCount.ShouldBe(1);

    [Fact]
    public void SaysItDidNotResume() => _summary.UsedCachedTranscript.ShouldBeFalse();

    [Fact]
    public async Task RendersThroughTheAudioEditor() =>
        await _harness.Editor.Received(1).CensorAudioAsync(
            _harness.InputPath,
            Arg.Any<IReadOnlyList<Hit>>(),
            CensorMethod.Silence,
            _harness.OutputPath,
            Arg.Any<CancellationToken>());

    [Fact]
    public void LeavesTheCensoredFileWhereTheJobPromisedIt() =>
        File.Exists(_harness.OutputPath).ShouldBeTrue();

    [Fact]
    public void BuildsTheTranscriptStoreOverTheDirectoryTheJobNamed() =>
        _harness.StoreDirectories.ShouldBe([_harness.TranscriptDirectory]);
}

/// <summary>
/// Finding 4's other half, and the one that silently breaks Resume: the store
/// keys a saved transcript off <see cref="Transcript.FileHash"/>, so a job that
/// builds the record with anything other than the digest it computed writes a
/// file nothing can ever find again.
/// </summary>
public sealed class WhenPersistingTheTranscriptForAFutureRun : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Transcript? _saved;
    private readonly string _digest;

    public WhenPersistingTheTranscriptForAFutureRun()
    {
        _digest = _harness.Store.ComputeHashAsync(_harness.InputPath).GetAwaiter().GetResult();

        _harness.Run();
        _saved = _harness.SavedTranscript();

        _saved.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void FilesItUnderTheDigestOfTheMediaItCameFrom() => _saved!.FileHash.ShouldBe(_digest);

    [Fact]
    public void StampsTheSchemaVersionThisBuildReads() =>
        _saved!.Version.ShouldBe(Transcript.CurrentVersion);

    [Fact]
    public void KeepsTheSegmentsItTranscribed() => _saved!.Segments.Count.ShouldBe(1);

    [Fact]
    public void KeepsTheWordsAlignmentRecoveredSoTheNextRunNeedNotAlignAgain() =>
        _saved!.Words.ShouldBe(_harness.AlignedWords);
}

/// <summary>Resume (ADR-0002): transcription is the expensive stage, so it is the one that is skipped.</summary>
public sealed class WhenATranscriptForThisFileIsAlreadyCached : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenATranscriptForThisFileIsAlreadyCached()
    {
        _harness.SeedTranscript();
        _summary = _harness.Run();

        _summary.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ReportsTheResumeAtItsOwnCheckpoint() =>
        _harness.Stages.ShouldContain(("transcribing", 45));

    [Fact]
    public async Task NeverStartsTheTranscriber() =>
        await _harness.Transcriber.DidNotReceive()
            .TranscribeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

    [Fact]
    public void SkipsTheTranscriptionCheckpointsEntirely() =>
        _harness.Stages.ShouldNotContain(("transcribing", 5));

    [Fact]
    public void SaysSoInTheSummary() => _summary.UsedCachedTranscript.ShouldBeTrue();

    [Fact]
    public async Task NeedsNoAlignmentBecauseTheCachedTranscriptAlreadyHasWords() =>
        await _harness.Aligner.DidNotReceive().AlignAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Segment>>(),
            Arg.Any<IReadOnlyList<Word>>(),
            Arg.Any<IProgress<JobProgress>?>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public void StillCensorsTheProfanityTheCachedTranscriptHolds() => _summary.Hits.Count.ShouldBe(1);
}

/// <summary>
/// The Rescan Pass exists to catch a word garbled at a chunk boundary, so it has
/// to run even when a transcript for this file is already on disk.
/// </summary>
public sealed class WhenARescanIsRequested : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;
    private readonly Transcript? _saved;

    public WhenARescanIsRequested()
    {
        _harness.Rescan = true;
        _harness.AlignedWords = [new Word("damn", 1.0, 1.5), new Word("hell", 3.0, 3.4)];
        _harness.SeedTranscript();

        _summary = _harness.Run();
        _saved = _harness.SavedTranscript();

        _summary.ShouldNotBeNull();
        _saved.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task TranscribesAgainRatherThanResuming() =>
        await _harness.Transcriber.Received(1)
            .TranscribeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task RunsTheSecondPassWithTheBoundariesShifted() =>
        await _harness.Transcriber.Received(1).TranscribeShiftedAsync(
            Arg.Any<string>(), RescanPassOffset, Arg.Any<CancellationToken>());

    [Fact]
    public void ReportsTheRescanAtItsOwnCheckpoint() =>
        _harness.Stages.ShouldContain(("transcribing", 42));

    [Fact]
    public void UnionsWhatBothPassesHeard() => _saved!.Segments.Count.ShouldBe(2);

    [Fact]
    public void CensorsTheWordOnlyTheSecondPassHeard() =>
        _summary.Hits.ShouldContain(hit => hit.Phrase == "hell");

    [Fact]
    public void SaysItRescanned() => _summary.Rescanned.ShouldBeTrue();

    /// <summary>The offset lives in <c>RescanPass</c>; the pipeline must not re-derive it.</summary>
    private const double RescanPassOffset = 4.0;
}

/// <summary>Video is analysed from its extracted audio track but rendered from the original file.</summary>
public sealed class WhenTheJobIsAVideo : IDisposable
{
    private readonly PipelineHarness _harness = new(MediaKind.Video, "clip.mp4");
    private readonly string _extracted;

    public WhenTheJobIsAVideo()
    {
        _extracted = Path.Combine(_harness.ScratchDirectory, "extracted_audio.m4a");
        _harness.Run();

        _harness.Checkpoints.ShouldNotBeEmpty();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ExtractsTheAudioTrackFirst() => _harness.Stages[0].ShouldBe(("preparing", 2));

    [Fact]
    public async Task PutsTheExtractedTrackInTheJobsScratchDirectory() =>
        await _harness.AudioPreparer.Received(1).ExtractAudioTrackAsync(
            _harness.InputPath, _extracted, Arg.Any<CancellationToken>());

    [Fact]
    public async Task TranscribesTheExtractedTrackRatherThanTheVideo() =>
        await _harness.Transcriber.Received(1)
            .TranscribeAsync(_extracted, Arg.Any<CancellationToken>());

    [Fact]
    public async Task AlignsAgainstTheExtractedTrackToo() =>
        await _harness.Aligner.Received(1).AlignAsync(
            _extracted,
            Arg.Any<IReadOnlyList<Segment>>(),
            Arg.Any<IReadOnlyList<Word>>(),
            Arg.Any<IProgress<JobProgress>?>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public async Task RendersFromTheOriginalFileSoThePictureSurvives() =>
        await _harness.Editor.Received(1).CensorVideoAsync(
            _harness.InputPath,
            Arg.Any<IReadOnlyList<Hit>>(),
            Arg.Any<CensorMethod>(),
            _harness.OutputPath,
            Arg.Any<CancellationToken>());

    [Fact]
    public async Task NeverRendersThroughTheAudioPath() =>
        await _harness.Editor.DidNotReceive().CensorAudioAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Hit>>(),
            Arg.Any<CensorMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
}

/// <summary>
/// ADR-0004: cutting a video's audio out would slide it out of step with a
/// picture that is being copied verbatim, so Remove on video is the silence
/// render. The editor guards this too; the pipeline asks for what it means.
/// </summary>
public sealed class WhenAskedToRemoveProfanityFromAVideo : IDisposable
{
    private readonly PipelineHarness _harness = new(MediaKind.Video, "clip.mp4");

    public WhenAskedToRemoveProfanityFromAVideo()
    {
        _harness.CensorMethod = CensorMethod.Remove;
        _harness.Run();

        _harness.Checkpoints.ShouldNotBeEmpty();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task RendersSilenceInstead() =>
        await _harness.Editor.Received(1).CensorVideoAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Hit>>(),
            CensorMethod.Silence,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public void StillFinishes() => _harness.Stages.ShouldContain(("completed", 100));
}

/// <summary>
/// ADR-0004 again, from the other side: widening a Hit into its idiom is only
/// ever safe when the edit is a jump cut on audio. Everywhere else Smart Cut may
/// still reject a false positive, because skipping never lengthens an edit.
/// </summary>
public sealed class WhenDecidingWhetherSmartCutMayWiden
{
    private readonly bool _removeOnAudio;
    private readonly bool _silenceOnAudio;
    private readonly bool _bleepOnAudio;
    private readonly bool _removeOnVideo;
    private readonly bool _silenceOnVideo;
    private readonly bool _bleepOnVideo;

    public WhenDecidingWhetherSmartCutMayWiden()
    {
        _removeOnAudio = WideningFor(CensorMethod.Remove, MediaKind.Audio);
        _silenceOnAudio = WideningFor(CensorMethod.Silence, MediaKind.Audio);
        _bleepOnAudio = WideningFor(CensorMethod.Bleep, MediaKind.Audio);
        _removeOnVideo = WideningFor(CensorMethod.Remove, MediaKind.Video);
        _silenceOnVideo = WideningFor(CensorMethod.Silence, MediaKind.Video);
        _bleepOnVideo = WideningFor(CensorMethod.Bleep, MediaKind.Video);
    }

    [Fact]
    public void PermitsItForACutOutOnAudio() => _removeOnAudio.ShouldBeTrue();

    [Fact]
    public void RefusesItForSilenceOnAudio() => _silenceOnAudio.ShouldBeFalse();

    [Fact]
    public void RefusesItForBleepOnAudio() => _bleepOnAudio.ShouldBeFalse();

    [Fact]
    public void RefusesItForACutOutOnVideo() => _removeOnVideo.ShouldBeFalse();

    [Fact]
    public void RefusesItForSilenceOnVideo() => _silenceOnVideo.ShouldBeFalse();

    [Fact]
    public void RefusesItForBleepOnVideo() => _bleepOnVideo.ShouldBeFalse();

    private static bool WideningFor(CensorMethod method, MediaKind kind)
    {
        using var harness = new PipelineHarness(kind, kind == MediaKind.Video ? "clip.mp4" : "book.mp3")
        {
            CensorMethod = method,
            SmartCutEnabled = true,
        };

        harness.Run();

        return harness.Refinements.Single().AllowWidening;
    }
}

/// <summary>
/// A rejection is the one thing Smart Cut can do that changes which windows get
/// cut, so the survivors go back through the merger rather than being shipped as
/// they were.
/// </summary>
public sealed class WhenSmartCutRejectsAHit : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenSmartCutRejectsAHit()
    {
        _harness.SmartCutEnabled = true;
        _harness.Segments =
            [new Segment(1.0, 1.5, "damn"), new Segment(5.0, 5.4, "hell"), new Segment(9.0, 9.4, "damn")];
        _harness.AlignedWords =
            [new Word("damn", 1.0, 1.5), new Word("hell", 5.0, 5.4), new Word("damn", 9.0, 9.4)];
        _harness.Decisions[1] = SmartCutDecision.Reject;

        _summary = _harness.Run();

        _summary.ShouldNotBeNull();
        _harness.Refinements.Count.ShouldBe(3);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void DropsTheHitItRejected() => _summary.Hits.Count.ShouldBe(2);

    [Fact]
    public void KeepsTheOnesItDidNotReject() =>
        _summary.Hits.ShouldAllBe(hit => hit.Phrase == "damn");

    [Fact]
    public void ReMergesTheSurvivors() => _summary.Hits[0].Start.ShouldBe(0.70, 0.001);

    [Fact]
    public void ReportsOneRefiningCheckpointPerHit() =>
        _harness.Stages.Count(stage => stage.Stage == "refining").ShouldBe(3);

    [Fact]
    public void SpacesThoseCheckpointsAcrossTheRefiningBand() =>
        _harness.Stages.Where(s => s.Stage == "refining").Select(s => s.Percent).ShouldBe([78, 81, 84]);

    [Fact]
    public void AsksAboutEveryHitBeforeDecidingAnything() =>
        _harness.Refinements.Select(r => r.Phrase).ShouldBe(["damn", "hell", "damn"]);
}

/// <summary>An adjustment moves a window; it does not create or drop one, so nothing is re-merged.</summary>
public sealed class WhenSmartCutNarrowsAHit : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenSmartCutNarrowsAHit()
    {
        _harness.SmartCutEnabled = true;
        _harness.Decisions[0] = SmartCutDecision.Adjust(3.0, 3.6);

        _summary = _harness.Run();

        _summary.Hits.ShouldHaveSingleItem();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void MovesTheWindowToWhereTheModelSaidTheSpeechWas() =>
        _summary.Hits[0].Start.ShouldBe(2.85, 0.001);

    [Fact]
    public void KeepsTheAsymmetricPaddingRoundTheNewWindow() =>
        _summary.Hits[0].End.ShouldBe(3.85, 0.001);

    [Fact]
    public void LeavesThePhraseAlone() => _summary.Hits[0].Phrase.ShouldBe("damn");
}

/// <summary>
/// The flag resolves to an implementation, never to a branch in here - but an
/// advisor that reports itself disabled is not worth a round trip per hit.
/// </summary>
public sealed class WhenSmartCutIsNotEnabled : IDisposable
{
    private readonly PipelineHarness _harness = new();

    public WhenSmartCutIsNotEnabled()
    {
        _harness.SmartCutEnabled = false;
        _harness.Run();

        _harness.Checkpoints.ShouldNotBeEmpty();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void NeverAsksTheAdvisorAnything() => _harness.Refinements.ShouldBeEmpty();

    [Fact]
    public void ReportsNoRefiningCheckpoints() =>
        _harness.Checkpoints.ShouldNotContain(c => c.Stage == "refining");
}

/// <summary>
/// A fallback Hit exists precisely because alignment lost the word, so it has no
/// word index to centre a context window on. The Python picked the word whose
/// start was nearest the hit, and that still works.
/// </summary>
public sealed class WhenSmartCutRefinesAHitAlignmentLost : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly PipelineHarness.Refinement _refinement;

    public WhenSmartCutRefinesAHitAlignmentLost()
    {
        _harness.SmartCutEnabled = true;
        _harness.Segments = [new Segment(0.0, 0.5, "hello"), new Segment(5.0, 5.4, "damn")];

        // Alignment recovered the clean speech and lost the profanity entirely,
        // so the only hit is a segment estimate with no word index at all.
        _harness.AlignedWords =
        [
            new Word("hello", 0.0, 0.5),
            new Word("there", 1.0, 1.4),
            new Word("everyone", 4.8, 5.2),
        ];

        _harness.Run();
        _refinement = _harness.Refinements.Single();

        _refinement.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void CentresTheWindowOnTheWordNearestTheHit() =>
        _refinement.ContextWindow[_refinement.CenterIndex].Text.ShouldBe("everyone");

    [Fact]
    public void StillHandsTheModelTheSurroundingWords() =>
        _refinement.ContextWindow.Count.ShouldBe(3);

    [Fact]
    public void AsksAboutThePhraseTheSegmentEstimateNamed() =>
        _refinement.Phrase.ShouldBe("damn");
}

/// <summary>ADR-0001: align the whole transcript, or none of it.</summary>
public sealed class WhenNothingInTheTranscriptIsOnTheBadWordsList : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenNothingInTheTranscriptIsOnTheBadWordsList()
    {
        _harness.Segments = [new Segment(1.0, 1.5, "a perfectly clean sentence")];
        _summary = _harness.Run();

        _summary.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task SkipsAlignmentAltogether() =>
        await _harness.Aligner.DidNotReceive().AlignAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Segment>>(),
            Arg.Any<IReadOnlyList<Word>>(),
            Arg.Any<IProgress<JobProgress>?>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public void ReportsNoAligningCheckpoints() =>
        _harness.Checkpoints.ShouldNotContain(c => c.Stage == "aligning");

    [Fact]
    public void FindsNothingToCensor() => _summary.Hits.ShouldBeEmpty();

    [Fact]
    public async Task NeverCallsTheEditorBecauseThereIsNothingToEdit() =>
        await _harness.Editor.DidNotReceive().CensorAudioAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Hit>>(),
            Arg.Any<CensorMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public void StillProducesTheOutputFileTheJobPromised() =>
        File.Exists(_harness.OutputPath).ShouldBeTrue();

    [Fact]
    public void CopiesTheInputThroughUnchanged() =>
        File.ReadAllText(_harness.OutputPath).ShouldBe(File.ReadAllText(_harness.InputPath));

    [Fact]
    public void StillFinishes() => _harness.Stages[^1].ShouldBe(("completed", 100));
}

/// <summary>
/// The other half of all-or-nothing: an engine that already produced word
/// timestamps leaves alignment with nothing to do, however many Candidates there
/// are.
/// </summary>
public sealed class WhenTheTranscriberAlreadyProducedWords : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenTheTranscriberAlreadyProducedWords()
    {
        _harness.TranscribedWords = [new Word("damn", 1.0, 1.5)];
        _summary = _harness.Run();

        _summary.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task DoesNotAlignAgain() =>
        await _harness.Aligner.DidNotReceive().AlignAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Segment>>(),
            Arg.Any<IReadOnlyList<Word>>(),
            Arg.Any<IProgress<JobProgress>?>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public void UsesTheWordTimesTheTranscriberGave() => _summary.Hits[0].End.ShouldBe(1.75, 0.001);

    [Fact]
    public void StillCountsThoseWords() => _summary.TranscriptWordCount.ShouldBe(1);
}

/// <summary>An unrecognised upload is a rejection, and it costs nothing to find out.</summary>
public sealed class WhenTheFileIsNotMediaThisApplicationCanProcess : IDisposable
{
    private readonly PipelineHarness _harness = new(MediaKind.Unknown);
    private readonly Exception _failure;

    public WhenTheFileIsNotMediaThisApplicationCanProcess()
    {
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void FailsTheJob() => _failure.ShouldBeOfType<InvalidOperationException>();

    [Fact]
    public void NamesTheFileItCouldNotRead() => _failure.Message.ShouldContain(_harness.InputPath);

    [Fact]
    public async Task NeverReachesTheGpu() =>
        await _harness.Transcriber.DidNotReceive()
            .TranscribeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

    [Fact]
    public void ReportsNothingBecauseNothingWasDone() => _harness.Checkpoints.ShouldBeEmpty();
}

/// <summary>
/// The editor does not hand back the path it wrote, so a render that quietly
/// produces nothing would otherwise be reported as a finished job with a
/// download link to a file that is not there.
/// </summary>
public sealed class WhenTheRenderProducesNoFile : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenTheRenderProducesNoFile()
    {
        _harness.EditorRenders = false;
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void FailsTheJob() => _failure.ShouldBeOfType<InvalidOperationException>();

    [Fact]
    public void NamesTheOutputThatNeverAppeared() => _failure.Message.ShouldContain(_harness.OutputPath);

    [Fact]
    public void NeverReportsTheJobComplete() =>
        _harness.Checkpoints.ShouldNotContain(c => c.Stage == "completed");
}

/// <summary>
/// The editor's <c>outputPath</c> is not nullable and it has its own idea of
/// where an unnamed render lands, so the pipeline resolves the path the same way
/// rather than assuming the default naming.
/// </summary>
public sealed class WhenTheJobNamesNoOutputPath : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly string _expected;

    public WhenTheJobNamesNoOutputPath()
    {
        _harness.OutputPath = string.Empty;
        _expected = MediaEditor.ResolveOutputPath(_harness.InputPath, null);

        _harness.Run();

        _expected.ShouldEndWith("_CENSORED.mp3");
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task RendersWhereTheEditorWouldHavePutIt() =>
        await _harness.Editor.Received(1).CensorAudioAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Hit>>(),
            Arg.Any<CensorMethod>(),
            _expected,
            Arg.Any<CancellationToken>());

    [Fact]
    public void VerifiesThatSamePathAfterwards() => File.Exists(_expected).ShouldBeTrue();
}

/// <summary>Analysis without an edit: what would be cut, reported, and nothing written.</summary>
public sealed class WhenNoRenderWasRequested : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenNoRenderWasRequested()
    {
        _harness.Render = false;
        _summary = _harness.Run();

        _summary.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StillReportsWhatItWouldHaveCut() => _summary.Hits.Count.ShouldBe(1);

    [Fact]
    public async Task NeverCallsTheEditor() =>
        await _harness.Editor.DidNotReceive().CensorAudioAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Hit>>(),
            Arg.Any<CensorMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public void WritesNoOutputFile() => File.Exists(_harness.OutputPath).ShouldBeFalse();

    [Fact]
    public void StillFinishes() => _harness.Stages[^1].ShouldBe(("completed", 100));
}

/// <summary>The debug dump is written from the aligned words when there are any.</summary>
public sealed class WhenDebugIsRequested : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly string _dump;

    public WhenDebugIsRequested()
    {
        _harness.Debug = true;
        _dump = Path.Combine(_harness.ScratchDirectory, "transcript.txt");

        _harness.Run();

        File.Exists(_dump).ShouldBeTrue();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void WritesTheTranscriptBesideTheJobsOtherWorkingFiles() =>
        File.ReadAllText(_dump).Trim().ShouldBe("damn");

    [Fact]
    public void ReportsItAtTheDocumentedCheckpoint() =>
        _harness.Stages.ShouldContain(("editing", 90));

    [Fact]
    public void StillRenders() => _harness.Stages.ShouldContain(("editing", 88));
}

/// <summary>
/// Cancellation is observed at every checkpoint, and always arrives as the one
/// exception the worker treats as "the user asked for this".
/// </summary>
public sealed class WhenTheJobIsCancelledBeforeItStarts : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenTheJobIsCancelledBeforeItStarts()
    {
        _harness.Cancel();
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SurfacesAsACancelledJob() => _failure.ShouldBeOfType<JobCancelledException>();

    [Fact]
    public async Task NeverProbesTheFile() =>
        await _harness.Prober.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
}

public sealed class WhenTheJobIsCancelledDuringTranscription : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenTheJobIsCancelledDuringTranscription()
    {
        _harness.CancelAt(5);
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SurfacesAsACancelledJob() => _failure.ShouldBeOfType<JobCancelledException>();

    [Fact]
    public void StopsAtTheNextCheckpoint() => _harness.Stages[^1].ShouldBe(("transcribing", 5));

    [Fact]
    public void NeverMatchesTheBadWordsList() =>
        _harness.Checkpoints.ShouldNotContain(c => c.Stage == "matching");
}

public sealed class WhenTheJobIsCancelledAfterMatching : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenTheJobIsCancelledAfterMatching()
    {
        _harness.CancelAt(50);
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SurfacesAsACancelledJob() => _failure.ShouldBeOfType<JobCancelledException>();

    [Fact]
    public async Task NeverStartsTheAligner() =>
        await _harness.Aligner.DidNotReceive().AlignAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Segment>>(),
            Arg.Any<IReadOnlyList<Word>>(),
            Arg.Any<IProgress<JobProgress>?>(),
            Arg.Any<CancellationToken>());
}

public sealed class WhenTheJobIsCancelledAfterAlignment : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenTheJobIsCancelledAfterAlignment()
    {
        _harness.CancelAt(75);
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SurfacesAsACancelledJob() => _failure.ShouldBeOfType<JobCancelledException>();

    [Fact]
    public async Task NeverRenders() =>
        await _harness.Editor.DidNotReceive().CensorAudioAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Hit>>(),
            Arg.Any<CensorMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public void LeavesNoOutputBehind() => File.Exists(_harness.OutputPath).ShouldBeFalse();
}

/// <summary>
/// The advisor swallows cancellation by contract - it must never throw - so the
/// refinement loop has to check the token itself, or a cancelled job grinds
/// through every remaining hit before noticing.
/// </summary>
public sealed class WhenTheJobIsCancelledMidRefinement : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenTheJobIsCancelledMidRefinement()
    {
        _harness.SmartCutEnabled = true;
        _harness.Segments =
            [new Segment(1.0, 1.5, "damn"), new Segment(5.0, 5.4, "hell"), new Segment(9.0, 9.4, "damn")];
        _harness.AlignedWords =
            [new Word("damn", 1.0, 1.5), new Word("hell", 5.0, 5.4), new Word("damn", 9.0, 9.4)];
        _harness.OnRefine = _harness.Cancel;

        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SurfacesAsACancelledJob() => _failure.ShouldBeOfType<JobCancelledException>();

    [Fact]
    public void StopsAskingAboutTheRemainingHits() => _harness.Refinements.Count.ShouldBe(1);

    [Fact]
    public async Task NeverRenders() =>
        await _harness.Editor.DidNotReceive().CensorAudioAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Hit>>(),
            Arg.Any<CensorMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
}

/// <summary>An engine that observes the token itself must not report as a failure either.</summary>
public sealed class WhenAnEngineObservesTheCancellationItself : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenAnEngineObservesTheCancellationItself()
    {
        _harness.Transcriber.TranscribeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<TranscriptionResult>>(_ => throw new OperationCanceledException());

        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsStillReportedAsACancelledJob() => _failure.ShouldBeOfType<JobCancelledException>();

    [Fact]
    public void KeepsTheOriginalCancellationAsTheCause() =>
        _failure.InnerException.ShouldBeOfType<OperationCanceledException>();
}

public sealed class WhenTheJobRequestIsNull : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly MediaPipeline _sut;

    public WhenTheJobRequestIsNull()
    {
        _sut = _harness.Build();

        _sut.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task IsRejected() =>
        await Should.ThrowAsync<ArgumentNullException>(() => _sut.RunAsync(null!));
}
