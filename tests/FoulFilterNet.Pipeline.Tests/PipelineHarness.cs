using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using NSubstitute;

namespace FoulFilterNet.Pipeline.Tests;

/// <summary>
/// One orchestrator wired to substituted engines over a throwaway directory.
/// </summary>
/// <remarks>
/// Nothing here loads a model, starts FFmpeg or touches the network: the whole
/// point of hoisting the engine contracts into <c>FoulFilterNet.Domain</c> was
/// that the spine could be specified without any of them. The transcript store
/// is the one real collaborator, because the Resume path is only worth asserting
/// against the file it actually writes.
/// </remarks>
internal sealed class PipelineHarness : IDisposable
{
    /// <summary>The engine calls the release policy is ordered against.</summary>
    public const string TranscribeCall = "transcribe";

    public const string RenderCall = "render";

    public const string ReleaseTranscriberCall = "release-transcriber";

    public const string ReleaseAlignerCall = "release-aligner";

    private readonly TempDirectory _root = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<JobProgress> _checkpoints = [];
    private readonly List<string> _storeDirectories = [];
    private readonly List<string> _engineCalls = [];

    public PipelineHarness(MediaKind kind = MediaKind.Audio, string inputName = "book.mp3")
    {
        Kind = kind;

        InputPath = Path.Combine(_root.Path, inputName);
        File.WriteAllText(InputPath, "pretend this is " + inputName);

        OutputPath = Path.Combine(_root.Path, "outputs", "censored_" + inputName);
        BadWordsPath = Path.Combine(_root.Path, "bad_words.txt");
        File.WriteAllLines(BadWordsPath, ["# the list", string.Empty, "damn", "hell", "go to hell"]);

        TranscriptDirectory = Path.Combine(_root.Path, "transcripts");
        ScratchDirectory = Path.Combine(_root.Path, "scratch", "job1");

        Store = new TranscriptStore(TranscriptDirectory);

        Prober.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new MediaInfo(Kind, 30.0, 16000)));

        AudioPreparer.ExtractAudioTrackAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = (string)call[1];
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "extracted audio");
                return Task.CompletedTask;
            });

        Transcriber.TranscribeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _engineCalls.Add(TranscribeCall);
                return Task.FromResult(new TranscriptionResult(Segments, TranscribedWords));
            });

        // CA2012: the ValueTask inside a When() is the call being described, not
        // work to be awaited - NSubstitute records the invocation and discards it.
#pragma warning disable CA2012
        Transcriber.When(transcriber => transcriber.ReleaseAsync()).Do(_ =>
        {
            _engineCalls.Add(ReleaseTranscriberCall);

            if (ReleaseThrows)
            {
                throw new NotSupportedException("The driver refused to unload the model.");
            }
        });

        Aligner.When(aligner => aligner.ReleaseAsync()).Do(_ => _engineCalls.Add(ReleaseAlignerCall));
#pragma warning restore CA2012

        Transcriber.TranscribeShiftedAsync(
                Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new TranscriptionResult(RescanSegments, [])));

        Aligner.AlignAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Segment>>(),
                Arg.Any<IReadOnlyList<Word>>(),
                Arg.Any<IProgress<JobProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(AlignedWords));

        Advisor.IsEnabled.Returns(_ => SmartCutEnabled);
        Advisor.RefineAsync(
                Arg.Any<IReadOnlyList<Word>>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Refinements.Add(new Refinement(
                    (IReadOnlyList<Word>)call[0], (string)call[1], (int)call[2], (bool)call[3]));

                OnRefine?.Invoke();
                return Task.FromResult(Decide(Refinements.Count - 1));
            });

        Editor.CensorAudioAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Hit>>(),
                Arg.Any<CensorMethod>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call => WriteRendered((string)call[3]));

        Editor.CensorVideoAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Hit>>(),
                Arg.Any<CensorMethod>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call => WriteRendered((string)call[3]));
    }

    public IMediaProber Prober { get; } = Substitute.For<IMediaProber>();

    public IAudioPreparer AudioPreparer { get; } = Substitute.For<IAudioPreparer>();

    public ITranscriber Transcriber { get; } = Substitute.For<ITranscriber>();

    public IAligner Aligner { get; } = Substitute.For<IAligner>();

    public ISmartCutAdvisor Advisor { get; } = Substitute.For<ISmartCutAdvisor>();

    public IMediaEditor Editor { get; } = Substitute.For<IMediaEditor>();

    /// <summary>The real store, over <see cref="TranscriptDirectory"/>.</summary>
    public TranscriptStore Store { get; }

    public MediaKind Kind { get; set; }

    public string InputPath { get; }

    public string OutputPath { get; set; }

    public string BadWordsPath { get; }

    public string TranscriptDirectory { get; }

    public string ScratchDirectory { get; }

    public CensorMethod CensorMethod { get; set; } = CensorMethod.Silence;

    public bool Debug { get; set; }

    public bool Rescan { get; set; }

    public bool Render { get; set; } = true;

    /// <summary>What the transcriber heard. One profanity, so one candidate.</summary>
    public IReadOnlyList<Segment> Segments { get; set; } = [new Segment(1.0, 1.5, "damn")];

    /// <summary>Words the transcriber itself produced; empty means alignment has work to do.</summary>
    public IReadOnlyList<Word> TranscribedWords { get; set; } = [];

    /// <summary>What the Rescan Pass hears on its second look.</summary>
    public IReadOnlyList<Segment> RescanSegments { get; set; } = [new Segment(3.0, 3.4, "hell")];

    public IReadOnlyList<Word> AlignedWords { get; set; } = [new Word("damn", 1.0, 1.5)];

    public bool SmartCutEnabled { get; set; }

    /// <summary>Verdict per hit, by index. Anything unlisted keeps its timestamps.</summary>
    public Dictionary<int, SmartCutDecision> Decisions { get; } = [];

    /// <summary>Every call the pipeline made to the advisor, in order.</summary>
    public List<Refinement> Refinements { get; } = [];

    /// <summary>Runs inside the advisor, so a test can cancel mid-refinement.</summary>
    public Action? OnRefine { get; set; }

    /// <summary>False makes the editor claim success without writing anything.</summary>
    public bool EditorRenders { get; set; } = true;

    /// <summary>True makes releasing the model fail, the way a wedged driver would.</summary>
    public bool ReleaseThrows { get; set; }

    /// <summary>
    /// The engine calls the release policy is defined in terms of, in the order
    /// the pipeline made them.
    /// </summary>
    public IReadOnlyList<string> EngineCalls => _engineCalls;

    /// <summary>Every progress checkpoint the pipeline reported, in order.</summary>
    public IReadOnlyList<JobProgress> Checkpoints => _checkpoints;

    /// <summary>The checkpoints as the pairs the UI contract is written in.</summary>
    public IReadOnlyList<(string Stage, int Percent)> Stages =>
        [.. _checkpoints.Select(c => (c.Stage, c.Percent))];

    /// <summary>Which directories the pipeline asked for a transcript store over.</summary>
    public IReadOnlyList<string> StoreDirectories => _storeDirectories;

    public JobRequest Request => new()
    {
        InputPath = InputPath,
        OutputPath = OutputPath,
        BadWordsPath = BadWordsPath,
        TranscriptDirectory = TranscriptDirectory,
        ScratchDirectory = ScratchDirectory,
        CensorMethod = CensorMethod,
        Debug = Debug,
        Rescan = Rescan,
        Render = Render,
    };

    public MediaPipeline Build() => new(
        Prober,
        AudioPreparer,
        Transcriber,
        Aligner,
        Advisor,
        Editor,
        directory =>
        {
            _storeDirectories.Add(directory);
            return new TranscriptStore(directory);
        });

    public JobSummary Run() => Build()
        .RunAsync(Request, new Recorder(this), _cancellation.Token)
        .GetAwaiter().GetResult();

    /// <summary>Runs and hands back whatever the job threw.</summary>
    public Exception RunExpectingFailure()
    {
        try
        {
            Run();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The job was expected to fail and did not.");
    }

    /// <summary>Cancel before the job starts.</summary>
    public void Cancel() => _cancellation.Cancel();

    /// <summary>Cancel the moment the pipeline reports this percentage.</summary>
    public void CancelAt(int percent) => CancelWhen(progress => progress.Percent == percent);

    public void CancelWhen(Func<JobProgress, bool> predicate) => CancelCondition = predicate;

    /// <summary>Put a transcript in the cache, keyed by the input file's real digest.</summary>
    public Transcript SeedTranscript(IReadOnlyList<Segment>? segments = null, IReadOnlyList<Word>? words = null)
    {
        var digest = Store.ComputeHashAsync(InputPath).GetAwaiter().GetResult();
        var transcript = new Transcript(
            Transcript.CurrentVersion, digest, segments ?? Segments, words ?? AlignedWords);

        Store.SaveAsync(transcript, "seed").GetAwaiter().GetResult();
        return transcript;
    }

    /// <summary>Whatever the job left in the cache, read back through the store.</summary>
    public Transcript? SavedTranscript() =>
        Store.FindAsync(Store.ComputeHashAsync(InputPath).GetAwaiter().GetResult())
            .GetAwaiter().GetResult();

    public void Dispose()
    {
        _cancellation.Dispose();
        _root.Dispose();
    }

    private Func<JobProgress, bool>? CancelCondition { get; set; }

    private SmartCutDecision Decide(int hitIndex) =>
        Decisions.TryGetValue(hitIndex, out var decision) ? decision : SmartCutDecision.KeepOriginal;

    private Task WriteRendered(string outputPath)
    {
        _engineCalls.Add(RenderCall);

        if (EditorRenders)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "censored");
        }

        return Task.CompletedTask;
    }

    private void Record(JobProgress progress)
    {
        _checkpoints.Add(progress);

        if (CancelCondition?.Invoke(progress) == true)
        {
            _cancellation.Cancel();
        }
    }

    /// <summary>One call to the advisor, as the pipeline made it.</summary>
    internal sealed record Refinement(
        IReadOnlyList<Word> ContextWindow,
        string Phrase,
        int CenterIndex,
        bool AllowWidening);

    private sealed class Recorder(PipelineHarness harness) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => harness.Record(value);
    }
}
