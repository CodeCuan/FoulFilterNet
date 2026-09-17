using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Evaluation;
using NSubstitute;

namespace FoulFilterNet.Evaluation.Tests;

/// <summary>
/// One fixture through a stub pipeline. The two levels come from two places:
/// the final hits are what the pipeline returned, and the raw words are what
/// it persisted to the transcript store on the way.
/// </summary>
public sealed class EvaluatingOneFixture : IDisposable
{
    private readonly TempDirectory _work = new();
    private readonly IMediaPipeline _pipeline = Substitute.For<IMediaPipeline>();
    private readonly ITranscriptStore _store = Substitute.For<ITranscriptStore>();
    private readonly List<string> _storeDirectories = [];
    private readonly JobRequest _request;
    private readonly FixtureRun _run;

    public EvaluatingOneFixture()
    {
        var badWords = _work.File("bad_words.txt");
        File.WriteAllLines(badWords, ["damn", "go to hell"]);

        _pipeline
            .RunAsync(
                Arg.Any<JobRequest>(),
                Arg.Any<IProgress<JobProgress>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new JobSummary(
                    [new Hit("damn", 3.260, 4.150, 1)],
                    3,
                    UsedCachedTranscript: false,
                    Rescanned: false
                )
            );

        _store.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("abc123");
        _store
            .FindAsync("abc123", Arg.Any<CancellationToken>())
            .Returns(
                new Transcript(
                    Transcript.CurrentVersion,
                    "abc123",
                    [new Segment(3.0, 4.2, "Well, damn, that")],
                    [
                        new Word("Well,", 3.000, 3.400),
                        new Word("damn,", 3.410, 3.900),
                        new Word("that", 3.900, 4.200),
                    ]
                )
            );

        var settings = new EvaluationSettings
        {
            MediaDirectory = _work.File("media"),
            BadWordsPath = badWords,
            WorkDirectory = _work.File("work"),
            TranscriptDirectory = _work.File("transcripts"),
        };

        var evaluator = new FixtureEvaluator(
            _pipeline,
            directory =>
            {
                _storeDirectories.Add(directory);
                return _store;
            }
        );

        var fixture = new FixtureTruth(
            "single_hit.mp3",
            "audio",
            8.388,
            [new PlantedSpan("damn", 3.41, 3.897)]
        );
        _run = evaluator
            .EvaluateAsync(fixture, settings, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
        _request = (JobRequest)_pipeline.ReceivedCalls().Single().GetArguments()[0]!;
    }

    public void Dispose() => _work.Dispose();

    [Fact]
    public void RunsTheFixtureFromTheMediaDirectory() =>
        _request.InputPath.ShouldBe(_work.File(Path.Combine("media", "single_hit.mp3")));

    [Fact]
    public void NeverRendersAnEdit() => _request.Render.ShouldBeFalse();

    [Fact]
    public void UsesTheConfiguredBadWordsList() =>
        _request.BadWordsPath.ShouldBe(_work.File("bad_words.txt"));

    [Fact]
    public void UsesTheConfiguredTranscriptDirectory() =>
        _request.TranscriptDirectory.ShouldBe(_work.File("transcripts"));

    [Fact]
    public void ReadsTheWordsBackFromThatSameDirectory() =>
        _storeDirectories.ShouldBe([_work.File("transcripts")]);

    [Fact]
    public void ReportsTheRawHitAtItsWordsTimes() =>
        _run.RawHits.Single().ShouldBe(new Hit("damn", 3.410, 3.900, 1));

    [Fact]
    public void ReportsThePipelinesHitsAsFinal() => _run.FinalHits.Single().Start.ShouldBe(3.260);

    [Fact]
    public void KeepsTheWordsItScored() => _run.Words.Count.ShouldBe(3);
}

/// <summary>A directory that deletes itself.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "foulfilter-eval-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory that outlives the test run is not a failure.
        }
    }
}
