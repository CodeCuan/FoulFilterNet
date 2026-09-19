using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Logging;

namespace FoulFilterNet.Watch;

/// <summary>
/// What every <see cref="WatchSession"/> needs from outside: the adapters it
/// drives and where it may write. One instance is shared by all the sessions a
/// <see cref="WatchSessionManager"/> makes.
/// </summary>
/// <param name="Source">Fetches the audio (yt-dlp).</param>
/// <param name="Audio">Converts it to the 16 kHz analysis WAV.</param>
/// <param name="Engine">Transcribes it, window by window; shared with Jobs.</param>
/// <param name="Transcripts">The Transcript cache, looked up and saved under the video's key.</param>
/// <param name="BadWords">The Bad Words List, asked again for every snapshot.</param>
/// <param name="ScratchDirectory">Under which each session makes, and deletes, its own directory.</param>
/// <param name="Time">The clock for heartbeats, expiry and throughput.</param>
/// <param name="Logger">Where sessions log.</param>
/// <param name="HeadSeconds">
/// How much audio to convert ahead of the whole file when <paramref name="Audio"/>
/// can (<see cref="IAudioHeadPreparer"/>); 0 converts the whole file first.
/// </param>
public sealed record WatchSessionServices(
    IWebAudioSource Source,
    IAudioPreparer Audio,
    IWhisperEngine Engine,
    ITranscriptStore Transcripts,
    IBadWordsSource BadWords,
    string ScratchDirectory,
    TimeProvider Time,
    ILogger Logger,
    double HeadSeconds = WatchOptions.DefaultHeadSeconds
);

/// <summary>
/// A Watch Session: the server's work to make one Web Video safe to watch.
/// It looks the video up in the Transcript cache, and on a miss fetches its
/// audio, converts it, and transcribes it a window at a time in the order the
/// viewer's playhead asks for, serving a new <see cref="WatchSnapshot"/> after
/// every window. The complete Transcript is saved under the video's key.
/// </summary>
/// <remarks>
/// <para>
/// <b>One run, one thread of work.</b> <see cref="Start"/> runs the whole
/// session in the background; everything else - heartbeats, snapshots,
/// <see cref="Cancel"/> - may be called from any thread at any time. The run
/// transcribes one window at a time, so no window is ever in flight when the
/// next is chosen: <see cref="WindowScheduler.Next"/> is asked with the
/// finished windows and the latest playhead after each one, and a seek takes
/// effect after the window in flight. A finished window is never asked for
/// again.
/// </para>
/// <para>
/// <b>Sharing the GPU.</b> The audio is opened at
/// <see cref="InferencePriority.High"/>, so each window queues ahead of any
/// batch Job's. Sessions for different videos run concurrently with each other,
/// but the engine's lane still hears one window at a time, so two videos
/// watched at once share the GPU window by window - each at half speed. That is
/// fine for V1's one viewer; <see cref="WatchSnapshot.IsKeepingUp"/> says when
/// it is not.
/// </para>
/// <para>
/// <b>Time to first play (W17).</b> On a cache miss the model loads in the
/// background while the audio is fetched. Once it is fetched, the whole file
/// starts converting and, alongside, its first
/// <see cref="WatchSessionServices.HeadSeconds"/> are converted on their own
/// (<see cref="IAudioHeadPreparer"/>), which takes a fraction of a second. The
/// windows <see cref="TranscriptionWindows.FixedPrefixCount"/> says the head
/// fixes are heard from the head, against a provisional plan of yt-dlp's
/// duration, while the whole file converts; they are the very windows, with the
/// very samples, the whole file would give. When the whole WAV is ready its
/// real plan is swapped in (<see cref="WatchProgress.WithPlan"/>, one revision)
/// and the rest is heard from it. Only while the viewer is inside the head's
/// windows: a viewer who starts past them waits for the whole file.
/// </para>
/// <para>
/// <b>Cleanup.</b> Whatever the outcome - complete, failed, cancelled - the
/// analysis audio is disposed (which closes the WAV), then the WAV and the
/// session's scratch directory are deleted, all before <see cref="Completion"/>
/// finishes.
/// </para>
/// <para>
/// <b>The Bad Words List is read on every snapshot</b>, not once: a change is
/// applied with <see cref="WatchProgress.WithBadWords"/>, which bumps the
/// revision and re-runs the rules over what has been heard without
/// transcribing again. That holds for a complete session too.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source has no timer and its WaitHandle is never read, so "
        + "it holds nothing to release; disposing it would race Cancel(), which may come from "
        + "any thread after the run has ended. JobManager keeps its per-job sources the same way."
)]
public sealed partial class WatchSession
{
    /// <summary>How many recent windows <see cref="WatchSnapshot.RealtimeFactor"/> is measured over.</summary>
    public const int ThroughputWindowCount = 3;

    /// <summary>A reason longer than this would flood the extension's overlay.</summary>
    private const int MaxReasonLength = 500;

    /// <summary>
    /// A window measured as taking no time at all (a fake clock, a coarse
    /// timer) is counted as this long, so the factor stays finite and
    /// serialisable.
    /// </summary>
    private static readonly TimeSpan MinimumWindowTime = TimeSpan.FromMilliseconds(1);

    private readonly Lock _gate = new();
    private readonly WatchSessionServices _services;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Queue<(double Seconds, TimeSpan Elapsed)> _recentWindows = new();

    private int _started;

    private WatchState _state = WatchState.Queued;
    private string? _reason;
    private WebAudioFailure? _failureKind;
    private string? _title;
    private double? _durationSeconds;
    private WatchProgress? _progress;
    private bool _fromCache;
    private double? _realtimeFactor;

    private double _playheadSeconds;
    private DateTimeOffset _lastHeartbeatAt;
    private DateTimeOffset? _endedAt;

    /// <summary>A session for <paramref name="video"/>, not started; it counts as heard from now.</summary>
    public WatchSession(VideoRef video, WatchSessionServices services)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(services);

        Video = video;
        _services = services;
        _logger = services.Logger;
        CreatedAt = services.Time.GetUtcNow();
        _lastHeartbeatAt = CreatedAt;
    }

    /// <summary>The Web Video this session is for.</summary>
    public VideoRef Video { get; }

    /// <summary>
    /// Unique to this session, and carried by every snapshot as
    /// <see cref="WatchSnapshot.SessionId"/>: a replacement session for the same
    /// video has a different one, which is how a client learns that the
    /// revisions it holds belong to a session that is gone.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>When the session was made.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Finishes, never faulted, once the run has ended and cleaned up (or was cancelled before it started).</summary>
    public Task Completion => _completion.Task;

    /// <summary>Where the session is now.</summary>
    public WatchState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The playhead from the latest heartbeat, in seconds; 0 until one arrives.</summary>
    public double PlayheadSeconds
    {
        get
        {
            lock (_gate)
            {
                return _playheadSeconds;
            }
        }
    }

    /// <summary>When the latest heartbeat arrived (at first, when the session was made).</summary>
    public DateTimeOffset LastHeartbeatAt
    {
        get
        {
            lock (_gate)
            {
                return _lastHeartbeatAt;
            }
        }
    }

    /// <summary>When the session reached a terminal state; null while it is working.</summary>
    public DateTimeOffset? EndedAt
    {
        get
        {
            lock (_gate)
            {
                return _endedAt;
            }
        }
    }

    /// <summary>
    /// Record that the video is being watched, with its playhead at
    /// <paramref name="positionSeconds"/>. The next window chosen starts from
    /// there.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The position is negative, NaN or infinite.</exception>
    public void RecordHeartbeat(double positionSeconds)
    {
        ThrowIfNotAPosition(positionSeconds);

        lock (_gate)
        {
            _playheadSeconds = positionSeconds;
            _lastHeartbeatAt = _services.Time.GetUtcNow();
        }
    }

    /// <summary>
    /// Start the run in the background. Only the first call does anything, and
    /// none after <see cref="Cancel"/>.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() => RunAsync(_cancellation.Token));
    }

    /// <summary>
    /// Stop the work: a download is killed, a window in flight abandoned, and
    /// the session ends <see cref="WatchState.Cancelled"/> after cleaning up.
    /// A session that has already ended keeps its state. Safe to call more
    /// than once, and from any thread.
    /// </summary>
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            // Never started: there is nothing to clean up.
            End(WatchState.Cancelled, CancelledReason);
            _completion.TrySetResult();
        }

        _cancellation.Cancel();
    }

    /// <summary>
    /// The session as it stands, with the Bad Words List as it stands: a
    /// changed list is applied first (a new revision), an unchanged one costs
    /// nothing.
    /// </summary>
    public WatchSnapshot Snapshot()
    {
        bool analysed;
        lock (_gate)
        {
            analysed = _progress is not null;
        }

        var badWords = analysed ? TryGetBadWords() : null;

        WatchProgress? progress;
        WatchSnapshot snapshot;
        lock (_gate)
        {
            if (_progress is not null && badWords is not null)
            {
                _progress = _progress.WithBadWords(badWords);
            }

            progress = _progress;
            snapshot = new WatchSnapshot(
                Video,
                _state,
                _reason,
                _failureKind,
                _title,
                _durationSeconds,
                Analysis: null,
                progress?.FinishedWindows.Count ?? 0,
                progress?.Plan.Count ?? 0,
                _realtimeFactor,
                _fromCache
            )
            {
                SessionId = Id,
            };
        }

        // Built outside the lock: the first read of a revision runs the rules,
        // which is milliseconds, and the result is kept by the progress.
        return progress is null ? snapshot : snapshot with { Analysis = progress.Snapshot };
    }

    /// <summary>
    /// Whether a manager should drop this session at <paramref name="now"/>:
    /// working with no heartbeat for <see cref="WatchOptions.IdleTimeout"/>;
    /// complete for <see cref="WatchOptions.CompletedRetention"/> since its last
    /// heartbeat or completion, whichever is later; failed or unsupported for
    /// <see cref="WatchOptions.FailedRetention"/> since it ended, however often
    /// it is polled; cancelled at once.
    /// </summary>
    public bool IsExpired(DateTimeOffset now, WatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            return _state switch
            {
                WatchState.Complete => now - Later(_endedAt, _lastHeartbeatAt)
                    >= options.CompletedRetention,
                WatchState.Failed or WatchState.Unsupported => now - (_endedAt ?? now)
                    >= options.FailedRetention,
                WatchState.Cancelled => true,
                _ => now - _lastHeartbeatAt >= options.IdleTimeout,
            };
        }
    }

    internal static void ThrowIfNotAPosition(double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(positionSeconds),
                positionSeconds,
                "The playhead must be a finite, non-negative number of seconds."
            );
        }
    }

    private const string CancelledReason = "Cancelled.";

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        string? scratch = null;
        string? wav = null;
        Task<string>? converting = null;
        var warming = Task.CompletedTask;
        using var conversion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var badWords = _services.BadWords.GetCurrent();

            if (await TryResumeAsync(badWords, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // A miss: the model is needed, and loading it takes as long again
            // as a short video's fetch, so it loads while the audio is fetched.
            warming = WarmUpAsync(cancellationToken);

            scratch = Path.Combine(_services.ScratchDirectory, $"{Video.Key}-{Guid.NewGuid():N}");

            MoveTo(WatchState.Fetching);
            var audio = await _services
                .Source.FetchAudioAsync(Video, scratch, cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _title = string.IsNullOrWhiteSpace(audio.Title) ? null : audio.Title;
                _durationSeconds = audio.DurationSeconds;
            }

            MoveTo(WatchState.Preparing);
            var source = audio.AudioPath;
            converting = Task.Run(
                () => _services.Audio.PadStartAsync(source, 0.0, conversion.Token),
                CancellationToken.None
            );

            if (_services.Audio is IAudioHeadPreparer head && WantsAHead(audio))
            {
                await HearTheHeadAsync(
                        head,
                        audio,
                        converting,
                        warming,
                        badWords,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            wav = await converting.ConfigureAwait(false);

            await warming.ConfigureAwait(false);
            var analysis = await _services
                .Engine.OpenAsync(wav, InferencePriority.High, cancellationToken)
                .ConfigureAwait(false);
            await using (analysis.ConfigureAwait(false))
            {
                lock (_gate)
                {
                    // After a head, the real plan replaces the provisional one;
                    // the windows heard from the head are in it unchanged.
                    _progress =
                        _progress?.WithPlan(analysis.Windows, analysis.DurationSeconds)
                        ?? WatchProgress.Start(
                            analysis.Windows,
                            analysis.DurationSeconds,
                            badWords
                        );
                    _durationSeconds = analysis.DurationSeconds;
                    _state = WatchState.Transcribing;
                }

                await TranscribeAsync(
                        analysis,
                        analysis.Windows.Count,
                        static () => true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
            End(WatchState.Complete, reason: null);
            LogComplete(Video.Key);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            End(WatchState.Cancelled, CancelledReason);
            LogCancelled(Video.Key);
        }
        catch (WebAudioException exception)
        {
            var reason = string.IsNullOrWhiteSpace(exception.Reason)
                ? exception.Message
                : exception.Reason;

            End(
                exception.IsUnsupported ? WatchState.Unsupported : WatchState.Failed,
                reason,
                exception.Kind
            );
            LogFetchFailed(Video.Key, exception.Kind, reason);
        }
        catch (Exception exception)
        {
            End(WatchState.Failed, exception.Message);
            LogFailed(exception, Video.Key);
        }
        finally
        {
            if (converting is not null)
            {
                // A conversion the session no longer needs is stopped, and one
                // that finished is deleted whether or not it was used.
                await conversion.CancelAsync().ConfigureAwait(false);
                try
                {
                    wav = await converting.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Its failure, if it mattered, has been reported already;
                    // the preparer deletes a render that failed.
                }
            }

            // The audio was disposed on the way out of its using block, so the
            // WAV is no longer held open.
            TryDeleteFile(wav);
            TryDeleteDirectory(scratch);

            // Never faults. Waited for so that a session that has finished has
            // nothing of its own still running on the engine.
            await warming.ConfigureAwait(false);
            _completion.TrySetResult();
        }
    }

    /// <summary>
    /// Load the model in the background, on a thread of its own because
    /// whisper.cpp loads synchronously. Never faults: a model that cannot load
    /// fails the open that follows with the same error, which is where the
    /// session reports it.
    /// </summary>
    private Task WarmUpAsync(CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                try
                {
                    await _services.Engine.WarmUpAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The session is ending anyway.
                }
                catch (Exception exception)
                {
                    LogWarmUpFailed(exception, Video.Key);
                }
            },
            CancellationToken.None
        );

    /// <summary>
    /// Complete straight from the Transcript cache, if it holds this video.
    /// </summary>
    /// <remarks>
    /// The store matches digests ignoring case (they were MD5 hex), but a
    /// YouTube ID is case-sensitive, so a Transcript saved under a key that
    /// differs only in case is a different video's and is treated as a miss.
    /// </remarks>
    private async Task<bool> TryResumeAsync(
        BadWordsList badWords,
        CancellationToken cancellationToken
    )
    {
        var cached = await _services
            .Transcripts.FindAsync(Video.Key, cancellationToken)
            .ConfigureAwait(false);

        if (cached is null || !string.Equals(cached.FileHash, Video.Key, StringComparison.Ordinal))
        {
            return false;
        }

        var progress = WatchProgress.FromTranscript(
            new TranscriptionResult(cached.Segments, cached.Words),
            badWords
        );

        lock (_gate)
        {
            _progress = progress;
            _durationSeconds = progress.DurationSeconds;
            _fromCache = true;
        }

        End(WatchState.Complete, reason: null);
        LogResumed(Video.Key);
        return true;
    }

    /// <summary>
    /// Whether to convert and hear a head first: when there is a head to
    /// speak of and yt-dlp does not say the video is no longer than it (then
    /// the whole file converts as fast as the head would).
    /// </summary>
    private bool WantsAHead(WebAudio audio) =>
        _services.HeadSeconds > 0.0
        && (audio.DurationSeconds is not { } claimed || claimed > _services.HeadSeconds);

    /// <summary>
    /// Convert the head and hear the windows it fixes from it, while
    /// <paramref name="converting"/> makes the whole WAV; stop as soon as that
    /// is ready. Leaves <see cref="_progress"/> on a provisional plan whose
    /// fixed windows are exactly the real plan's, or null when nothing was
    /// heard. A head that cannot be converted is logged and skipped: the whole
    /// file is still coming.
    /// </summary>
    private async Task HearTheHeadAsync(
        IAudioHeadPreparer preparer,
        WebAudio audio,
        Task<string> converting,
        Task warming,
        BadWordsList badWords,
        CancellationToken cancellationToken
    )
    {
        string head;
        try
        {
            head = await preparer
                .ConvertHeadAsync(audio.AudioPath, _services.HeadSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogHeadFailed(exception, Video.Key);
            return;
        }

        try
        {
            if (converting.IsCompleted)
            {
                return;
            }

            await warming.ConfigureAwait(false);
            var analysis = await _services
                .Engine.OpenAsync(head, InferencePriority.High, cancellationToken)
                .ConfigureAwait(false);
            await using (analysis.ConfigureAwait(false))
            {
                // The whole file is at least as long as its head, which is
                // all that is known of its length yet; yt-dlp's figure plans
                // the rest provisionally.
                var known = analysis.DurationSeconds;
                var provisional = Math.Max(known, audio.DurationSeconds ?? 0.0);
                var plan = TranscriptionWindows.Plan(provisional);
                var usable = FixedWindows(analysis.Windows, plan, known);

                lock (_gate)
                {
                    if (usable == 0 || _playheadSeconds >= plan[usable - 1].KeepTo)
                    {
                        // Nothing the viewer needs yet is in the head.
                        return;
                    }

                    _progress = WatchProgress.Start(plan, provisional, badWords);
                    _durationSeconds = provisional;
                    _state = WatchState.Transcribing;
                }

                LogHearingHead(Video.Key, usable, known);
                await TranscribeAsync(
                        analysis,
                        usable,
                        () => !converting.IsCompleted,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            TryDeleteFile(head);
        }
    }

    /// <summary>
    /// How many of the first windows the head fixes: those
    /// <see cref="TranscriptionWindows.FixedPrefixCount"/> allows for its
    /// length, and - checked rather than assumed - that the head reads from
    /// the same stretch of audio as <paramref name="plan"/> puts them.
    /// </summary>
    private static int FixedWindows(
        IReadOnlyList<TranscriptionWindow> head,
        IReadOnlyList<TranscriptionWindow> plan,
        double known
    )
    {
        var count = Math.Min(
            TranscriptionWindows.FixedPrefixCount(known),
            Math.Min(head.Count, plan.Count)
        );
        for (var i = 0; i < count; i++)
        {
            if (head[i].Start != plan[i].Start || head[i].End != plan[i].End)
            {
                return i;
            }
        }

        return count;
    }

    /// <summary>
    /// Windows one at a time, each chosen from the latest playhead and the
    /// windows finished so far, among the first <paramref name="usable"/> of
    /// the plan (all of them once the whole WAV is open), for as long as
    /// <paramref name="carryOn"/> says.
    /// </summary>
    private async Task TranscribeAsync(
        IAnalysisAudio analysis,
        int usable,
        Func<bool> carryOn,
        CancellationToken cancellationToken
    )
    {
        while (carryOn())
        {
            cancellationToken.ThrowIfCancellationRequested();

            WatchProgress progress;
            int? next;
            lock (_gate)
            {
                progress = _progress!;
                next = NextWindow(progress, usable, _playheadSeconds);
            }

            if (next is not { } index)
            {
                return;
            }

            var started = _services.Time.GetTimestamp();
            var heard = await analysis
                .TranscribeWindowAsync(index, cancellationToken)
                .ConfigureAwait(false);
            var elapsed = _services.Time.GetElapsedTime(started);

            lock (_gate)
            {
                // Swapped under the lock so a Bad Words List change applied by
                // a snapshot in the meantime is kept.
                _progress = _progress!.With(index, heard);
                RecordThroughput(
                    ShareSeconds(progress.Plan[index], progress.DurationSeconds),
                    elapsed
                );
            }
        }
    }

    /// <summary>
    /// The window to hear next. With only the first <paramref name="usable"/>
    /// windows available (a head), none once the viewer is past them - the
    /// whole file is what they are waiting for - and otherwise the scheduler's
    /// choice with the rest counted as not to be asked for.
    /// </summary>
    private static int? NextWindow(WatchProgress progress, int usable, double playheadSeconds)
    {
        var plan = progress.Plan;
        if (usable >= plan.Count)
        {
            return WindowScheduler.Next(plan, progress.FinishedWindows, playheadSeconds);
        }

        if (playheadSeconds >= plan[usable - 1].KeepTo)
        {
            return null;
        }

        return WindowScheduler.Next(
            plan,
            progress.FinishedWindows.Concat(Enumerable.Range(usable, plan.Count - usable)),
            playheadSeconds
        );
    }

    /// <summary>
    /// Save the stitched Transcript under the video's key. A cache that cannot
    /// be written costs the next viewing a transcription; it does not take the
    /// finished Hits away from this one, so a failure is logged, not thrown.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        WatchProgress progress;
        string baseName;
        lock (_gate)
        {
            progress = _progress!;
            baseName = _title ?? Video.Key;
        }

        var heard = progress.Snapshot.Transcript;
        var transcript = new Transcript(
            Transcript.CurrentVersion,
            Video.Key,
            heard.Segments,
            heard.Words
        );

        try
        {
            await _services
                .Transcripts.SaveAsync(transcript, baseName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSaveFailed(exception, Video.Key);
        }
    }

    /// <summary>
    /// Seconds of the timeline a window makes final: its share, clamped to the
    /// file (the first share starts at minus infinity, the last ends at plus).
    /// </summary>
    private static double ShareSeconds(TranscriptionWindow window, double durationSeconds) =>
        Math.Max(0.0, Math.Min(window.KeepTo, durationSeconds) - Math.Max(window.KeepFrom, 0.0));

    /// <summary>Called under the lock.</summary>
    private void RecordThroughput(double seconds, TimeSpan elapsed)
    {
        _recentWindows.Enqueue(
            (seconds, elapsed < MinimumWindowTime ? MinimumWindowTime : elapsed)
        );
        while (_recentWindows.Count > ThroughputWindowCount)
        {
            _recentWindows.Dequeue();
        }

        var heard = _recentWindows.Sum(w => w.Seconds);
        var took = _recentWindows.Sum(w => w.Elapsed.TotalSeconds);
        _realtimeFactor = heard / took;
    }

    private BadWordsList? TryGetBadWords()
    {
        try
        {
            return _services.BadWords.GetCurrent();
        }
        catch (Exception exception)
        {
            // The list in use stays in force: a bad edit must not stop the
            // censoring of a video already being watched.
            LogBadWordsUnreadable(exception, Video.Key);
            return null;
        }
    }

    private void MoveTo(WatchState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    private void End(WatchState state, string? reason, WebAudioFailure? failureKind = null)
    {
        lock (_gate)
        {
            if (_state.IsTerminal())
            {
                return;
            }

            _state = state;
            _reason = reason is { Length: > MaxReasonLength } ? reason[..MaxReasonLength] : reason;
            _failureKind = failureKind;
            _endedAt = _services.Time.GetUtcNow();
        }
    }

    private static DateTimeOffset Later(DateTimeOffset? first, DateTimeOffset second) =>
        first is { } value && value > second ? value : second;

    private static void TryDeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Watch Session {Key}: served from the Transcript cache"
    )]
    private partial void LogResumed(string key);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Watch Session {Key}: every window heard, Transcript saved"
    )]
    private partial void LogComplete(string key);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Watch Session {Key}: the model could not be loaded ahead of the audio; opening it will try again"
    )]
    private partial void LogWarmUpFailed(Exception exception, string key);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Watch Session {Key}: hearing the first {Windows} window(s) from the {Seconds} s head while the whole file converts"
    )]
    private partial void LogHearingHead(string key, int windows, double seconds);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Watch Session {Key}: the head of the audio could not be converted; waiting for the whole file"
    )]
    private partial void LogHeadFailed(Exception exception, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch Session {Key}: cancelled")]
    private partial void LogCancelled(string key);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Watch Session {Key}: audio could not be fetched ({Kind}): {Reason}"
    )]
    private partial void LogFetchFailed(string key, WebAudioFailure kind, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Watch Session {Key}: failed")]
    private partial void LogFailed(Exception exception, string key);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Watch Session {Key}: the Transcript could not be saved to the cache"
    )]
    private partial void LogSaveFailed(Exception exception, string key);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Watch Session {Key}: the Bad Words List could not be reread; keeping the one in use"
    )]
    private partial void LogBadWordsUnreadable(Exception exception, string key);
}
