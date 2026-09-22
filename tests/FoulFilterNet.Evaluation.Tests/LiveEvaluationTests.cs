using FoulFilterNet.Domain;
using FoulFilterNet.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FoulFilterNet.Evaluation.Tests;

/// <summary>
/// The harness's acceptance test: every fixture through the real pipeline on
/// the GPU, reproducing the measurement ADR-0006 made by hand.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, behind the same gate as <c>LiveWhisperTranscriberTests</c>: it needs
/// CUDA, FFmpeg and about 1.6 GB of GGML weights, none of which CI has.
/// <c>FOULFILTER_MODEL_DIR</c> says where the weights are (default: the nearest
/// <c>models</c> directory above the test assembly) and <c>FOULFILTER_TEST_MODEL</c>
/// which model (default <c>large-v3-turbo</c>). Nothing is downloaded.
/// </para>
/// <para>
/// The run happens once per process and every fact reads the same report, the
/// one place this project's test style bends, for the same reason the Whisper
/// tests bend it: a constructor per fact would transcribe the fixtures per fact.
/// </para>
/// </remarks>
public sealed class TheRealPipelineAgainstTheFixtures
{
    private const string OptIn =
        "opt-in: set RUN_GPU_TESTS=1 with CUDA, FFmpeg and GGML weights installed";

    public static bool GpuTestsEnabled =>
        (Environment.GetEnvironmentVariable("RUN_GPU_TESTS") ?? string.Empty).ToLowerInvariant()
            is "1"
                or "true"
                or "yes";

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task DetectsEveryPlantedSpan() => (await Raw()).Detected.ShouldBe(11);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task FindsEveryProfanityInTheFinalHits() => (await Final()).Recall.ShouldBe(1.0);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task CoversEveryPlantedSpanInTheFinalHits() =>
        (await Final()).Covered.ShouldBe(11);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task ReproducesTheWorstAbsoluteStartError() =>
        (await Raw()).StartError.MaxAbsolute.ShouldBe(0.242);

    /// <remarks>
    /// ADR-0006's 0.107 was measured on the CPU, and the CPU reproduces it
    /// exactly. CUDA gives 0.105: one word, the fourth "damn" of
    /// <c>repeated_hits.mp3</c>, starts at 6.700 s there instead of 6.680 s.
    /// The tolerance is that 0.002 backend difference, plus floating-point slack.
    /// </remarks>
    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task ReproducesTheMeanAbsoluteStartErrorOnEitherBackend() =>
        (await Raw()).StartError.MeanAbsolute!.Value.ShouldBe(0.107, 0.0025);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task ReproducesTheMeanAbsoluteEndError() =>
        (await Raw()).EndError.MeanAbsolute.ShouldBe(0.069);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task ReproducesTheWorstAbsoluteEndError() =>
        (await Raw()).EndError.MaxAbsolute.ShouldBe(0.207);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task ReproducesTheWorstLateStart() => (await Raw()).WorstLateStart.ShouldBe(0.070);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task ReproducesTheWorstEarlyEnd() => (await Raw()).WorstEarlyEnd.ShouldBe(0.207);

    [Fact(Skip = OptIn, SkipUnless = nameof(GpuTestsEnabled))]
    public async Task ReproducesTheStartsInsideThePadding() =>
        (await Raw()).StartsWithinTolerance.ShouldBe(10);

    private static async Task<ScoreCard> Raw() =>
        (await LiveEvaluation.Report.Value).RawWords.Summary;

    private static async Task<ScoreCard> Final() =>
        (await LiveEvaluation.Report.Value).FinalHits.Summary;
}

/// <summary>One evaluation of the fixtures, shared by every opt-in fact above.</summary>
internal static class LiveEvaluation
{
    public static readonly Lazy<Task<EvaluationReport>> Report = new(RunAsync);

    private static async Task<EvaluationReport> RunAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Transcription:Model"] =
                        Environment.GetEnvironmentVariable("FOULFILTER_TEST_MODEL")
                        ?? "large-v3-turbo",
                    ["Transcription:Language"] = "en",
                    ["Transcription:ModelDirectory"] = ModelDirectory(),
                    ["SmartCut:Enabled"] = "false",
                }
            )
            .Build();

        using var services = new ServiceCollection()
            .AddLogging()
            .AddEvaluationPipeline(configuration)
            .BuildServiceProvider();

        var manifestPath = GroundTruthManifest.Locate(AppContext.BaseDirectory);
        var fixtures = GroundTruthManifest.Load(
            manifestPath,
            GroundTruthManifest.DefaultInnocentFixtures
        );

        var work = Path.Combine(
            Path.GetTempPath(),
            "foulfilter-eval-live-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(work);
        var badWords = Path.Combine(work, "bad_words.txt");
        await File.WriteAllLinesAsync(badWords, GroundTruthManifest.Phrases(fixtures));

        var settings = new EvaluationSettings
        {
            MediaDirectory = Path.GetDirectoryName(manifestPath)!,
            BadWordsPath = badWords,
            WorkDirectory = work,
            TranscriptDirectory = Path.Combine(work, "transcripts"),
        };

        try
        {
            var evaluator = services.GetRequiredService<FixtureEvaluator>();
            var runs = new List<FixtureRun>();
            foreach (var fixture in fixtures)
            {
                runs.Add(await evaluator.EvaluateAsync(fixture, settings));
            }

            return EvaluationReport.Build(
                runs,
                new EvaluationRunInfo(),
                services.GetRequiredService<CutPadding>()
            );
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A scratch directory that outlives the run is not a failure.
            }
        }
    }

    private static string ModelDirectory()
    {
        if (
            Environment.GetEnvironmentVariable("FOULFILTER_MODEL_DIR") is { Length: > 0 } configured
        )
        {
            return configured;
        }

        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            var candidate = Path.Combine(directory.FullName, "models");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find models above {AppContext.BaseDirectory}."
        );
    }
}
