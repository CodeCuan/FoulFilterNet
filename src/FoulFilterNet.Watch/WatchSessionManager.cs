using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.Watch;

/// <summary>
/// Every Watch Session the service is running, one per Web Video: the
/// extension's heartbeats start them and keep them alive, and
/// <see cref="SweepExpired"/> drops the ones nobody is watching.
/// </summary>
/// <remarks>
/// <para>
/// <b>One session per video.</b> Sessions are keyed by
/// <see cref="VideoRef.Key"/>, and the lookup and the creation happen under
/// one lock, so two tabs on the same video - or two heartbeats racing - share
/// one session and one download.
/// </para>
/// <para>
/// <b>Lifetime.</b> A session is dropped when <see cref="WatchSession.IsExpired"/>
/// says so: no heartbeat for <see cref="WatchOptions.IdleTimeout"/> while it
/// works (the work is cancelled), <see cref="WatchOptions.CompletedRetention"/>
/// after a complete one was last watched, and
/// <see cref="WatchOptions.FailedRetention"/> after a failure. The same rule is
/// applied when a heartbeat or a lookup finds a session the sweeper has not
/// reached yet, so behaviour does not depend on when the sweeper last ran. A
/// heartbeat for a video whose session was dropped starts a new one: for a
/// failure that is the retry, and for a complete video it is a Transcript
/// cache hit. A new session starts its revisions again.
/// </para>
/// <para>
/// <b>Concurrency.</b> Sessions for different videos run side by side, but
/// every one of them opens its audio at <see cref="InferencePriority.High"/>
/// on the one shared engine, whose lane hears a single window at a time. So
/// concurrent sessions take turns on the GPU window by window. For V1's single
/// viewer that is fine; each snapshot's <see cref="WatchSnapshot.IsKeepingUp"/>
/// reports when it is not.
/// </para>
/// </remarks>
public sealed class WatchSessionManager : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WatchSession> _sessions = new(StringComparer.Ordinal);
    private readonly WatchSessionServices _services;
    private readonly WatchOptions _options;
    private readonly TimeProvider _time;

    private bool _disposed;

    /// <summary>A manager whose sessions use these adapters and options.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An option's time span is not positive.</exception>
    /// <exception cref="ArgumentException">An option's path is blank.</exception>
    public WatchSessionManager(
        IWebAudioSource source,
        IAudioPreparer audio,
        IWhisperEngine engine,
        ITranscriptStore transcripts,
        IBadWordsSource badWords,
        WatchOptions options,
        TimeProvider? time = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(badWords);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _time = time ?? TimeProvider.System;
        _services = new WatchSessionServices(
            source,
            audio,
            engine,
            transcripts,
            badWords,
            options.ScratchDirectory,
            _time,
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WatchSession>()
        );
    }

    /// <summary>The options the sessions live by.</summary>
    public WatchOptions Options => _options;

    /// <summary>How many sessions are held, whatever their state.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// The extension is watching <paramref name="video"/> with its playhead at
    /// <paramref name="positionSeconds"/>: record it, starting a session if
    /// there is none (or the one there has expired), and return the snapshot.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The position is negative, NaN or infinite.</exception>
    /// <exception cref="ObjectDisposedException">The manager has been disposed.</exception>
    public WatchSnapshot Heartbeat(VideoRef video, double positionSeconds)
    {
        ArgumentNullException.ThrowIfNull(video);
        WatchSession.ThrowIfNotAPosition(positionSeconds);

        WatchSession session;
        WatchSession? expired = null;
        var created = false;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (
                !_sessions.TryGetValue(video.Key, out var existing)
                || existing.IsExpired(_time.GetUtcNow(), _options)
            )
            {
                expired = existing;
                existing = new WatchSession(video, _services);
                _sessions[video.Key] = existing;
                created = true;
            }

            session = existing;

            // Before it starts, so the first window is chosen from this playhead.
            session.RecordHeartbeat(positionSeconds);
        }

        // Outside the lock: cancelling runs callbacks, and starting queues work.
        expired?.Cancel();
        if (created)
        {
            session.Start();
        }

        return session.Snapshot();
    }

    /// <summary>
    /// The snapshot of <paramref name="video"/>'s session, or null when there is
    /// none (or it has expired). Starts nothing and does not count as a
    /// heartbeat.
    /// </summary>
    public WatchSnapshot? Find(VideoRef video)
    {
        ArgumentNullException.ThrowIfNull(video);

        WatchSession? session;
        lock (_gate)
        {
            if (
                !_sessions.TryGetValue(video.Key, out session)
                || session.IsExpired(_time.GetUtcNow(), _options)
            )
            {
                return null;
            }
        }

        return session.Snapshot();
    }

    /// <summary>
    /// Cancel <paramref name="video"/>'s session and drop it. False when there
    /// was none. The work stops and cleans up in the background; the next
    /// heartbeat starts a new session.
    /// </summary>
    public bool Cancel(VideoRef video)
    {
        ArgumentNullException.ThrowIfNull(video);

        WatchSession? session;
        lock (_gate)
        {
            if (!_sessions.Remove(video.Key, out session))
            {
                return false;
            }
        }

        session.Cancel();
        return true;
    }

    /// <summary>
    /// Drop every expired session, cancelling any still working. Returns how
    /// many were dropped. The sweeper calls this on a timer.
    /// </summary>
    public int SweepExpired()
    {
        var now = _time.GetUtcNow();
        List<WatchSession> dropped = [];

        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                if (session.IsExpired(now, _options))
                {
                    dropped.Add(session);
                }
            }

            foreach (var session in dropped)
            {
                _sessions.Remove(session.Video.Key);
            }
        }

        foreach (var session in dropped)
        {
            session.Cancel();
        }

        return dropped.Count;
    }

    /// <summary>Cancel every session without waiting for them to clean up.</summary>
    public void Dispose() => _ = TakeAllAndCancel();

    /// <summary>Cancel every session and wait for each to clean up (delete its download and WAV).</summary>
    public async ValueTask DisposeAsync()
    {
        var sessions = TakeAllAndCancel();
        await Task.WhenAll(sessions.Select(s => s.Completion)).ConfigureAwait(false);
    }

    private List<WatchSession> TakeAllAndCancel()
    {
        List<WatchSession> sessions;
        lock (_gate)
        {
            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Cancel();
        }

        return sessions;
    }
}
