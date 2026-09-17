using System.Text;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Media;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.Pipeline;

/// <summary>
/// Builds the transcript cache for one job's transcript directory.
/// </summary>
/// <remarks>
/// A factory rather than a single injected store because
/// <see cref="JobRequest.TranscriptDirectory"/> belongs to the job: the service
/// points every job at the same cache, but the CLI and the tests do not have to.
/// </remarks>
public delegate ITranscriptStore TranscriptStoreFactory(string directory);

/// <summary>
/// The spine: prepare, transcribe or resume, optionally rescan, match, align,
/// persist, reconcile, merge, optionally refine, optionally dump, render.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>_run_job_impl</c> in <c>Legacy/src/pipeline.py</c>. Pure
/// orchestration: every engine arrives as an interface, so the whole spine is
/// specified without a GPU, a model, a network or a media file - which is the
/// reason the contracts were hoisted into <c>FoulFilterNet.Domain</c> in the
/// first place.
/// </para>
/// <para>
/// The progress percentages are a contract rather than decoration. The front end
/// renders them directly, so they are kept exactly as the stage table in
/// docs/01-python-analysis.md 3 lists them - including the Python's ordering
/// quirk of writing the debug transcript at 90 and then rendering at 88.
/// </para>
/// </remarks>
public sealed partial class MediaPipeline : IMediaPipeline
{
    /// <summary>Where a video's audio track lands, inside the job's scratch directory.</summary>
    private const string ExtractedAudioName = "extracted_audio.m4a";

    private const string DebugTranscriptName = "transcript.txt";

    /// <summary>How many tokens the debug dump puts on a line, as the Python did.</summary>
    private const int DebugTokensPerLine = 20;

    private readonly IMediaProber _prober;
    private readonly IAudioPreparer _audio;
    private readonly ITranscriber _transcriber;
    private readonly IAligner _aligner;
    private readonly ISmartCutAdvisor _smartCut;
    private readonly IMediaEditor _editor;
    private readonly TranscriptStoreFactory _stores;
    private readonly ILogger<MediaPipeline> _logger;
    private readonly int _contextRadius;

    /// <summary>
    /// One padding for both, deliberately: the reconciler's proximity tolerance
    /// is derived from the padding the merger applies, and they have to agree.
    /// </summary>
    private readonly HitPadding _padding = HitPadding.Default;

    private readonly HitMerger _merger;
    private readonly HitReconciler _reconciler;

    public MediaPipeline(
        IMediaProber prober,
        IAudioPreparer audioPreparer,
        ITranscriber transcriber,
        IAligner aligner,
        ISmartCutAdvisor smartCut,
        IMediaEditor editor,
        TranscriptStoreFactory transcriptStores,
        SmartCutOptions? smartCutOptions = null,
        ILogger<MediaPipeline>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(prober);
        ArgumentNullException.ThrowIfNull(audioPreparer);
        ArgumentNullException.ThrowIfNull(transcriber);
        ArgumentNullException.ThrowIfNull(aligner);
        ArgumentNullException.ThrowIfNull(smartCut);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(transcriptStores);

        _prober = prober;
        _audio = audioPreparer;
        _transcriber = transcriber;
        _aligner = aligner;
        _smartCut = smartCut;
        _editor = editor;
        _stores = transcriptStores;
        _logger = logger ?? NullLogger<MediaPipeline>.Instance;
        _contextRadius = (smartCutOptions ?? new SmartCutOptions()).ContextRadius;

        _merger = new HitMerger(_padding);
        _reconciler = new HitReconciler(_padding);
    }

    /// <inheritdoc />
    public async Task<JobSummary> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await RunCoreAsync(request, progress, cancellationToken);
        }
        catch (OperationCanceledException cancelled)
        {
            // An engine that observed the token itself reports the same outcome
            // as a checkpoint that observed it: the user asked for this.
            throw new JobCancelledException("Job cancelled.", cancelled);
        }
        finally
        {
            // UNLOAD_MODELS_AFTER_JOB. Unconditional, like the Smart Cut stage:
            // what releasing means belongs to the engine's policy, not to a
            // branch in here. In a finally because an abandoned job holds the
            // same VRAM a finished one does, and after the render because the
            // engines must still be usable while the file is being written.
            await ReleaseEnginesAsync();
        }
    }

    /// <summary>
    /// Hand back whatever the engines are holding, per <c>UNLOAD_MODELS_AFTER_JOB</c>.
    /// </summary>
    /// <remarks>
    /// Housekeeping rather than part of the job, as it was in the Python: a
    /// driver that refuses to unload must neither turn a finished job into a
    /// failed one nor replace the failure a failed job needs to report. Each
    /// engine is released independently for the same reason - the first one
    /// throwing must not strand the second's memory.
    /// </remarks>
    private async ValueTask ReleaseEnginesAsync()
    {
        await ReleaseAsync(_transcriber.ReleaseAsync, nameof(ITranscriber));
        await ReleaseAsync(_aligner.ReleaseAsync, nameof(IAligner));
    }

    private async ValueTask ReleaseAsync(Func<ValueTask> release, string engine)
    {
        try
        {
            await release();
        }
        catch (Exception exception)
        {
            LogReleaseFailed(exception, engine);
        }
    }

    private async Task<JobSummary> RunCoreAsync(
        JobRequest request,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        ThrowIfCancelled(cancellationToken);

        Directory.CreateDirectory(request.TranscriptDirectory);
        Directory.CreateDirectory(request.ScratchDirectory);

        var media = await _prober.ProbeAsync(request.InputPath, cancellationToken);
        if (media.Kind is MediaKind.Unknown)
        {
            throw new InvalidOperationException($"Unrecognized file type: {request.InputPath}");
        }

        // ADR-0004: widening a Hit into its idiom only ever makes sense for a
        // jump cut on audio. Silence, bleep and every video edit stay surgical,
        // because stretching them reads as a glitch rather than a censor.
        var allowWidening =
            request.CensorMethod is CensorMethod.Remove && media.Kind is MediaKind.Audio;

        // Video is analysed from its extracted audio track and rendered from the
        // original file, so the picture survives the edit untouched.
        var analysisSource = request.InputPath;
        if (media.Kind is MediaKind.Video)
        {
            Checkpoint(progress, "preparing", 2, "Extracting audio track", cancellationToken);
            analysisSource = Path.Combine(request.ScratchDirectory, ExtractedAudioName);
            await _audio.ExtractAudioTrackAsync(
                request.InputPath,
                analysisSource,
                cancellationToken
            );
        }

        var badWords = BadWordsList.FromLines(
            await File.ReadAllLinesAsync(request.BadWordsPath, cancellationToken)
        );

        var store = _stores(request.TranscriptDirectory);
        var digest = await store.ComputeHashAsync(request.InputPath, cancellationToken);
        var baseName = Path.GetFileNameWithoutExtension(request.InputPath);

        // A Rescan is a request to listen again, so the cache is not even
        // consulted - and the summary then honestly reports that nothing was
        // resumed, which the Python got wrong by reporting the lookup instead.
        var cached = request.Rescan ? null : await store.FindAsync(digest, cancellationToken);

        IReadOnlyList<Segment> segments;
        IReadOnlyList<Word> words;

        if (cached is not null)
        {
            Checkpoint(
                progress,
                "transcribing",
                45,
                "Reusing persisted transcript",
                cancellationToken
            );
            LogResumedTranscript(digest, request.InputPath);

            segments = cached.Segments;
            words = cached.Words;
        }
        else
        {
            Checkpoint(progress, "transcribing", 5, "GPU transcription started", cancellationToken);
            var heard = await _transcriber.TranscribeAsync(analysisSource, cancellationToken);
            segments = heard.Segments;
            words = heard.Words;

            Checkpoint(
                progress,
                "transcribing",
                40,
                $"{segments.Count} segments transcribed",
                cancellationToken
            );

            if (request.Rescan)
            {
                Checkpoint(
                    progress,
                    "transcribing",
                    42,
                    "Rescan pass (offset boundaries)",
                    cancellationToken
                );

                var shifted = await _transcriber.TranscribeShiftedAsync(
                    analysisSource,
                    RescanPass.DefaultOffsetSeconds,
                    cancellationToken
                );

                // Segments only. A word the second pass heard has no counterpart
                // in the first pass's timeline to be reconciled against, and the
                // fallback estimate is what covers it.
                segments = RescanPass.Union(segments, shifted.Segments);
                LogRescanGrewTranscript(segments.Count);
            }
        }

        Checkpoint(progress, "matching", 50, "Matching Bad Words List", cancellationToken);
        var candidates = PhraseMatcher.FindCandidates(segments, badWords);
        LogCandidates(candidates.Count);

        // ADR-0001: alignment is all-or-nothing. Anything flagged aligns the
        // whole transcript; nothing flagged skips the stage entirely.
        if (candidates.Count > 0 && words.Count == 0)
        {
            Checkpoint(progress, "aligning", 55, "Forced alignment started", cancellationToken);
            words = await _aligner.AlignAsync(
                analysisSource,
                segments,
                words,
                progress,
                cancellationToken
            );
            Checkpoint(progress, "aligning", 75, $"{words.Count} words aligned", cancellationToken);
        }

        // Persisted with the digest the store computed, because that is the key
        // it files the entry under; anything else writes a transcript that can
        // never be found again.
        await store.SaveAsync(
            new Transcript(Transcript.CurrentVersion, digest, segments, words),
            baseName,
            cancellationToken
        );

        var hits = Reconcile(candidates, words, badWords);

        if (_smartCut.IsEnabled && words.Count > 0 && hits.Count > 0)
        {
            hits = await RefineAsync(hits, words, allowWidening, progress, cancellationToken);
        }

        if (request.Debug)
        {
            await WriteDebugTranscriptAsync(request, segments, words, cancellationToken);
            Checkpoint(progress, "editing", 90, "Debug transcript written", cancellationToken);
        }

        if (!request.Render)
        {
            Checkpoint(
                progress,
                "completed",
                100,
                "Analysis complete (no edit requested)",
                cancellationToken
            );

            return new JobSummary(hits, words.Count, cached is not null, request.Rescan);
        }

        await RenderAsync(request, media.Kind, hits, progress, cancellationToken);

        Checkpoint(progress, "completed", 100, "Finished", cancellationToken);
        return new JobSummary(hits, words.Count, cached is not null, request.Rescan);
    }

    /// <summary>
    /// Confirm the Hits: exact times where alignment recovered the word, the
    /// segment estimate where it did not, then pad and merge into cut windows.
    /// </summary>
    private IReadOnlyList<Hit> Reconcile(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<Word> words,
        BadWordsList badWords
    )
    {
        var hits = _reconciler.Reconcile(candidates, words, badWords);

        foreach (var hit in hits)
        {
            if (hit.WordIndex is null)
            {
                LogUnalignedCandidate(hit.Phrase);
            }
        }

        var merged = _merger.Merge(hits);
        LogHitsAfterMerge(merged.Count);

        return merged;
    }

    /// <summary>One optional LLM pass per Hit; it may narrow, widen where permitted, or reject.</summary>
    /// <remarks>
    /// The advisor never throws, cancellation included - that is its contract -
    /// so the loop observes the token itself at each hit. Without that a
    /// cancelled job would grind through every remaining hit before noticing.
    /// </remarks>
    private async Task<IReadOnlyList<Hit>> RefineAsync(
        IReadOnlyList<Hit> hits,
        IReadOnlyList<Word> words,
        bool allowWidening,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var kept = new List<Hit>(hits.Count);

        for (var i = 0; i < hits.Count; i++)
        {
            Checkpoint(
                progress,
                "refining",
                78 + (10 * i / hits.Count),
                $"Smart Cut {i + 1}/{hits.Count}",
                cancellationToken
            );

            var hit = hits[i];
            var (window, centerIndex) = ContextWindow(words, hit, _contextRadius);
            var decision = await _smartCut.RefineAsync(
                window,
                hit.Phrase,
                centerIndex,
                allowWidening,
                cancellationToken
            );

            switch (decision.Outcome)
            {
                case SmartCutOutcome.Reject:
                    break;

                case SmartCutOutcome.Adjust:
                    kept.Add(
                        hit with
                        {
                            Start = Times.Round(Math.Max(0.0, decision.CutStart - _padding.Pre)),
                            End = Times.Round(decision.CutEnd + _padding.Post),
                        }
                    );
                    break;

                default:
                    kept.Add(hit);
                    break;
            }
        }

        if (kept.Count == hits.Count)
        {
            return kept;
        }

        LogSmartCutRejections(hits.Count - kept.Count);

        // Dropping a window can leave two neighbours that no longer need to be
        // separate, so the survivors go back through the merger.
        return _merger.Merge(kept);
    }

    /// <summary>
    /// The words either side of a Hit, and the target's index inside that window.
    /// </summary>
    /// <remarks>
    /// The centre is the word whose start is nearest the Hit, rather than the
    /// Hit's own word index, because a Hit that fell back to a segment estimate
    /// has no index - a fallback exists precisely because alignment lost the
    /// word. This is the Python's <c>_context_window</c>.
    /// </remarks>
    private static (IReadOnlyList<Word> Window, int CenterIndex) ContextWindow(
        IReadOnlyList<Word> words,
        Hit hit,
        int radius
    )
    {
        var center = 0;
        var nearest = double.MaxValue;

        for (var i = 0; i < words.Count; i++)
        {
            var distance = Math.Abs(words[i].Start - hit.Start);
            if (distance < nearest)
            {
                nearest = distance;
                center = i;
            }
        }

        var low = Math.Max(0, center - radius);
        var high = Math.Min(words.Count, center + radius + 1);

        var window = new Word[high - low];
        for (var i = low; i < high; i++)
        {
            window[i - low] = words[i];
        }

        return (window, center - low);
    }

    /// <summary>Render the Censor Method, then prove something was actually written.</summary>
    private async Task RenderAsync(
        JobRequest request,
        MediaKind kind,
        IReadOnlyList<Hit> hits,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        Checkpoint(
            progress,
            "editing",
            88,
            $"Rendering {Describe(request.CensorMethod)} edit",
            cancellationToken
        );

        // The editor does not hand back the path it wrote, and a blank one means
        // "you choose", so the path is resolved the way the editor would.
        var outputPath = MediaEditor.ResolveOutputPath(request.InputPath, request.OutputPath);

        if (hits.Count == 0)
        {
            // Nothing to censor, but the job still promised a file.
            CopyThrough(request.InputPath, outputPath);
        }
        else if (kind is MediaKind.Video)
        {
            // ADR-0004: cutting a video's audio would slide it out of step with
            // a picture that is stream-copied. The editor guards this as well;
            // asking for what is meant keeps the two from drifting apart.
            await _editor.CensorVideoAsync(
                request.InputPath,
                hits,
                ForVideo(request.CensorMethod),
                outputPath,
                cancellationToken
            );
        }
        else
        {
            await _editor.CensorAudioAsync(
                request.InputPath,
                hits,
                request.CensorMethod,
                outputPath,
                cancellationToken
            );
        }

        // A silent non-render must fail the job rather than complete it with a
        // download link to a file that is not there.
        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException($"Output missing after render: {outputPath}");
        }
    }

    private static CensorMethod ForVideo(CensorMethod method) =>
        method is CensorMethod.Remove ? CensorMethod.Silence : method;

    /// <summary>The wire spellings, so a renamed enum member cannot change what the UI reads.</summary>
    private static string Describe(CensorMethod method) =>
        method switch
        {
            CensorMethod.Bleep => "bleep",
            CensorMethod.Remove => "remove",
            _ => "silence",
        };

    private static void CopyThrough(string inputPath, string outputPath)
    {
        if (SamePath(inputPath, outputPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(inputPath, outputPath, overwrite: true);
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

    /// <summary>
    /// The transcript as plain text, from the aligned words when there are any
    /// and from the segment text when there are not.
    /// </summary>
    private static async Task WriteDebugTranscriptAsync(
        JobRequest request,
        IReadOnlyList<Segment> segments,
        IReadOnlyList<Word> words,
        CancellationToken cancellationToken
    )
    {
        var tokens =
            words.Count > 0
                ? words.Select(word => word.Text).ToList()
                : segments.SelectMany(segment => Tokenizer.Tokenize(segment.Text)).ToList();

        var text = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            text.Append(tokens[i]);
            text.Append((i + 1) % DebugTokensPerLine == 0 ? '\n' : ' ');
        }

        await File.WriteAllTextAsync(
            Path.Combine(request.ScratchDirectory, DebugTranscriptName),
            text.ToString(),
            cancellationToken
        );
    }

    /// <summary>
    /// Report one checkpoint, having first observed cancellation.
    /// </summary>
    /// <remarks>
    /// Checking here rather than at each engine call is what the Python did by
    /// raising from inside its progress callback, and it puts the check at every
    /// point where the job is between stages and safe to abandon.
    /// </remarks>
    private static void Checkpoint(
        IProgress<JobProgress>? progress,
        string stage,
        int percent,
        string detail,
        CancellationToken cancellationToken
    )
    {
        ThrowIfCancelled(cancellationToken);
        progress?.Report(new JobProgress(stage, percent, detail));
    }

    private static void ThrowIfCancelled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new JobCancelledException();
        }
    }

    // Source-generated rather than called through ILogger directly, so an
    // argument is never evaluated for a message that is not going to be
    // written. The messages themselves are the Python's.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reusing persisted transcript {Digest} rather than transcribing {Input} again"
    )]
    private partial void LogResumedTranscript(string digest, string input);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Rescan grew the transcript to {Count} segments"
    )]
    private partial void LogRescanGrewTranscript(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} candidate(s)")]
    private partial void LogCandidates(int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No aligned timestamps for '{Phrase}'; using the segment estimate"
    )]
    private partial void LogUnalignedCandidate(string phrase);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} hit(s) after merge")]
    private partial void LogHitsAfterMerge(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Smart Cut rejected {Count} hit(s)")]
    private partial void LogSmartCutRejections(int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Releasing the {Engine} model failed; the job's own outcome stands"
    )]
    private partial void LogReleaseFailed(Exception exception, string engine);
}
