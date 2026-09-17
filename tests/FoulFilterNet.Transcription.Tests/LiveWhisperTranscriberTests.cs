using FoulFilterNet.Domain;
using FoulFilterNet.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// The measurement that answers the project's last open technical question: is
/// whisper.cpp's word-timestamp heuristic precise enough to replace the wav2vec2
/// forced aligner ADR-0001 introduced? The fixtures' spans are exact by
/// construction (<c>tests/fixtures/media/manifest.json</c>), so the difference
/// between what the GPU reports and what the manifest says is real boundary
/// error, not annotation noise. The measured numbers are recorded in
/// <c>docs/adr/0006-whisper-net-collapses-transcription-and-alignment.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, exactly as <c>LiveLlmSmartCutTests</c> is: this needs a CUDA card,
/// FFmpeg, and roughly 1.6 GB of GGML weights that are gitignored and never
/// committed. CI has none of those and must stay green, so
/// <c>RUN_GPU_TESTS=1 dotnet test</c> is what runs it.
/// </para>
/// <para>
/// <c>FOULFILTER_MODEL_DIR</c> overrides where the weights live (default: the
/// repository's <c>models</c> directory) and <c>FOULFILTER_TEST_MODEL</c> which
/// model is used (default <c>large-v3-turbo</c>). Nothing here may download:
/// the engine is built without an acquisition delegate, so a missing weights
/// file fails loudly instead of quietly fetching 1.6 GB.
/// </para>
/// <para>
/// The work sits in the facts rather than in the constructor, the one place this
/// project's test style bends: a constructor runs per fact, and transcription is
/// memoized per fixture so the model loads once for the whole class instead of
/// once per assertion.
/// </para>
/// </remarks>
public sealed class LiveWhisperTranscriberTests
{
    private const string OptIn =
        "opt-in: set RUN_GPU_TESTS=1 with CUDA, FFmpeg and GGML weights installed";

    /// <summary>
    /// The tolerance <em>is</em> the answer to the open question. It is set to
    /// the padding <c>HitMerger</c> already applies - 0.15 s before a hit and
    /// 0.25 s after it - because an error the padding absorbs cannot change
    /// which audio gets censored, and one it does not absorb can.
    /// </summary>
    private const double StartTolerance = 0.15;
    private const double EndTolerance = 0.25;

    /// <summary>Gate, read the same way the live-LLM tests read theirs.</summary>
    public static bool GpuTestsEnabled =>
        (Environment.GetEnvironmentVariable("RUN_GPU_TESTS") ?? string.Empty).ToLowerInvariant()
            is "1"
                or "true"
                or "yes";

    /// <remarks>
    /// The word arrives as "damn," - whisper.cpp attaches the following comma to
    /// it, and the engine deliberately leaves it there because
    /// <c>PhraseMatcher.FindHits</c> normalizes before matching. Asserting the
    /// bare spelling here would be asserting something the pipeline does not
    /// require.
    /// </remarks>
    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task HearsTheProfanityInTheSingleHitFixture() =>
        (await LiveWhisper.WordsAsync("single_hit.mp3")).ShouldContain(w =>
            Tokenizer.Normalize(w.Text) == "damn"
        );

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task ProducesWordTimestampsWithoutAForcedAligner() =>
        (await LiveWhisper.TranscribeAsync("single_hit.mp3")).HasWordTimestamps.ShouldBeTrue();

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task PlacesTheStartOfDamnWithinThePrePadding() =>
        (await Damn()).Start.ShouldBe(3.410, StartTolerance);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task PlacesTheEndOfDamnWithinThePostPadding() =>
        (await Damn()).End.ShouldBe(3.897, EndTolerance);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task HearsEveryWordOfThePhraseFixture()
    {
        var words = await LiveWhisper.WordsAsync("phrase_hit.mp3");

        words.Select(w => w.Text).ShouldContain("hell");
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task PlacesTheEndOfThePhraseWithinThePostPadding()
    {
        var hell = (await LiveWhisper.WordsAsync("phrase_hit.mp3")).Last(w =>
            w.Text.StartsWith("hell", StringComparison.Ordinal)
        );

        hell.End.ShouldBe(4.227, EndTolerance);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task FindsEveryRepetitionInTheRepeatedHitsFixture() =>
        (await LiveWhisper.WordsAsync("repeated_hits.mp3"))
            .Count(w => w.Text.StartsWith("damn", StringComparison.Ordinal))
            .ShouldBe(5);

    /// <remarks>
    /// The Rescan Pass is a <em>detection</em> pass, and the pipeline unions its
    /// segments only - a word the second pass heard has no counterpart in the
    /// first pass's timeline. So what matters here is that the second pass still
    /// finds the profanity and that its timestamps come back on the original
    /// timeline rather than four seconds late; the boundary tolerances above are
    /// deliberately not applied to it. Measured, this rescan reports "damn," at
    /// 2.720-3.940 against a truth of 3.410-3.897: an early start, because the
    /// second pass did not hear the "Well," in front of it and the word inherits
    /// the preceding token's instant. Early is the safe direction - it
    /// over-censors rather than leaving speech audible.
    /// </remarks>
    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task StillHearsTheProfanityWhenEveryChunkBoundaryMoves() =>
        (await LiveWhisper.RescanAsync("single_hit.mp3")).Words.ShouldContain(w =>
            Tokenizer.Normalize(w.Text) == "damn"
        );

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task RebasesTheRescansSegmentsOntoTheOriginalTimeline()
    {
        var shifted = await LiveWhisper.RescanAsync("single_hit.mp3");

        // Four seconds of silence went in front of the audio; a segment still
        // reporting the padded timeline would sit at about 7 s, not 3 s.
        shifted
            .Segments.First(s => s.Text.Contains("damn", StringComparison.OrdinalIgnoreCase))
            .Start.ShouldBe(3.0, 1.0);
    }

    /// <remarks>
    /// With DTW on, Whisper.net returns only the first 30 seconds of whatever it
    /// is given, and every fixture is shorter than that. This builds a file with
    /// the single-hit fixture at the start and again 75 s in, well past the
    /// first window and across a window share boundary, and expects the second
    /// "damn" at its true place within the padding tolerances.
    /// </remarks>
    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task HearsTheProfanityPastTheFirstThirtySeconds()
    {
        using var directory = new TempDirectory();
        var fixture = MediaFixtures.Path("single_hit.mp3");
        var longFile = System.IO.Path.Combine(directory.Path, "long.wav");
        await new FFmpegRunner().RunFFmpegAsync(
            [
                "-y",
                "-loglevel",
                "error",
                "-i",
                fixture,
                "-i",
                fixture,
                "-filter_complex",
                "[1]adelay=75000|75000[late];[0][late]amix=inputs=2:normalize=0",
                longFile,
            ],
            TestContext.Current.CancellationToken
        );

        var late = (await LiveWhisper.TranscribeFileAsync(longFile)).Words.Single(w =>
            w.Text.StartsWith("damn", StringComparison.Ordinal) && w.Start > 60
        );

        late.Start.ShouldBe(75 + 3.410, StartTolerance);
        late.End.ShouldBe(75 + 3.897, EndTolerance);
    }

    private static async Task<Word> Damn() =>
        (await LiveWhisper.WordsAsync("single_hit.mp3")).First(w =>
            w.Text.StartsWith("damn", StringComparison.Ordinal)
        );
}

/// <summary>
/// The real engine over the real fixtures, built once per process. Only the
/// opt-in facts above touch it, so nothing here runs in CI.
/// </summary>
internal static class LiveWhisper
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, TranscriptionResult> Heard = [];
    private static readonly Lazy<WhisperTranscriber> Transcriber = new(Build);

    public static async Task<TranscriptionResult> TranscribeAsync(string fixtureFileName)
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (!Heard.TryGetValue(fixtureFileName, out var result))
            {
                result = await Transcriber.Value.TranscribeAsync(
                    MediaFixtures.Path(fixtureFileName),
                    TestContext.Current.CancellationToken
                );
                Heard[fixtureFileName] = result;
            }

            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static Task<TranscriptionResult> TranscribeFileAsync(string path) =>
        Transcriber.Value.TranscribeAsync(path, TestContext.Current.CancellationToken);

    public static async Task<IReadOnlyList<Word>> WordsAsync(string fixtureFileName) =>
        (await TranscribeAsync(fixtureFileName)).Words;

    public static Task<TranscriptionResult> RescanAsync(string fixtureFileName) =>
        Transcriber.Value.TranscribeShiftedAsync(
            MediaFixtures.Path(fixtureFileName),
            RescanPass.DefaultOffsetSeconds,
            TestContext.Current.CancellationToken
        );

    private static WhisperTranscriber Build()
    {
        var options = new TranscriptionOptions
        {
            Model = Environment.GetEnvironmentVariable("FOULFILTER_TEST_MODEL") ?? "large-v3-turbo",
            Language = "en",
            Device = TranscriptionDevice.Auto,
        };

        // No acquisition delegate: this must resolve installed weights or fail.
        var models = new WhisperModelSource(MediaFixtures.ModelDirectory);

        return new WhisperTranscriber(
            new WhisperNetEngine(options, models, NullLogger<WhisperNetEngine>.Instance),
            new FFmpegAudioPreparer(new FFmpegRunner())
        );
    }
}

/// <summary>
/// Locates the generated media fixtures and the installed weights, both of which
/// live relative to the repository root rather than to the test assembly.
/// </summary>
internal static class MediaFixtures
{
    private static readonly Lazy<string> Media = new(() =>
        Locate(System.IO.Path.Combine("tests", "fixtures", "media"))
    );

    public static string Path(string fileName) => System.IO.Path.Combine(Media.Value, fileName);

    public static string ModelDirectory =>
        Environment.GetEnvironmentVariable("FOULFILTER_MODEL_DIR") is { Length: > 0 } configured
            ? configured
            : Locate("models");

    private static string Locate(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find {relative} above {AppContext.BaseDirectory}."
        );
    }
}
