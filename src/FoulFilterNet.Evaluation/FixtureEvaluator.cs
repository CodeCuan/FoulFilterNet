using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Pipeline;

namespace FoulFilterNet.Evaluation;

/// <summary>Where one evaluation reads from and writes to.</summary>
public sealed record EvaluationSettings
{
    public required string MediaDirectory { get; init; }

    public required string BadWordsPath { get; init; }

    public required string WorkDirectory { get; init; }

    public required string TranscriptDirectory { get; init; }

    public bool Rescan { get; init; }
}

/// <summary>What the pipeline reported for one fixture, at both levels.</summary>
public sealed record FixtureRun(
    FixtureTruth Fixture,
    IReadOnlyList<Word> Words,
    IReadOnlyList<Hit> RawHits,
    IReadOnlyList<Hit> FinalHits,
    bool UsedCachedTranscript,
    double ElapsedSeconds);

/// <summary>Runs one fixture through the real pipeline and collects what it reported at both levels.</summary>
/// <remarks>
/// <para>
/// The pipeline runs exactly as a job would, with <see cref="JobRequest.Render"/>
/// off: the final hits are its answer, after reconciliation, padding, merging
/// and (if enabled) Smart Cut.
/// </para>
/// <para>
/// The raw words come from the transcript the pipeline persisted on the way,
/// read back from the same store - so they are the words it matched against,
/// not a second transcription that could disagree with the first. They are
/// matched with <see cref="PhraseMatcher.FindHits"/>, which is what ADR-0006
/// measured.
/// </para>
/// </remarks>
public sealed class FixtureEvaluator
{
    private readonly IMediaPipeline _pipeline;
    private readonly TranscriptStoreFactory _stores;

    public FixtureEvaluator(IMediaPipeline pipeline, TranscriptStoreFactory stores)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(stores);

        _pipeline = pipeline;
        _stores = stores;
    }

    public async Task<FixtureRun> EvaluateAsync(
        FixtureTruth fixture,
        EvaluationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(settings);

        var input = Path.Combine(settings.MediaDirectory, fixture.File);
        var name = Path.GetFileNameWithoutExtension(fixture.File);

        var request = new JobRequest
        {
            InputPath = input,
            OutputPath = Path.Combine(settings.WorkDirectory, "output", fixture.File),
            BadWordsPath = settings.BadWordsPath,
            TranscriptDirectory = settings.TranscriptDirectory,
            ScratchDirectory = Path.Combine(settings.WorkDirectory, "scratch", name),
            Rescan = settings.Rescan,
            Render = false,
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var summary = await _pipeline.RunAsync(request, progress: null, cancellationToken);
        clock.Stop();

        var store = _stores(settings.TranscriptDirectory);
        var digest = await store.ComputeHashAsync(input, cancellationToken);
        var transcript = await store.FindAsync(digest, cancellationToken)
            ?? throw new InvalidOperationException(
                $"The pipeline did not persist a transcript for {fixture.File} in {settings.TranscriptDirectory}.");

        var badWords = BadWordsList.FromLines(await File.ReadAllLinesAsync(settings.BadWordsPath, cancellationToken));

        return new FixtureRun(
            fixture,
            transcript.Words,
            PhraseMatcher.FindHits(transcript.Words, badWords),
            summary.Hits,
            summary.UsedCachedTranscript,
            Times.Round(clock.Elapsed.TotalSeconds));
    }
}
