using FoulFilterNet.Pipeline;

namespace FoulFilterNet.Watch;

/// <summary>
/// How long Watch Sessions live and where they work. Bound from the <c>Watch</c>
/// section of configuration by <see cref="WatchServiceCollectionExtensions.AddWatch(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration)"/>;
/// the host is expected to point the three paths at its own storage (W10:
/// <c>StorageOptions</c>), and the defaults are the ones it would pick.
/// </summary>
public sealed class WatchOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Watch";

    /// <summary>
    /// A session with no heartbeat for this long is cancelled and dropped. The
    /// extension beats about once a second while a watch page is open, so two
    /// minutes of silence means the tab is gone; the Transcript, if it got that
    /// far, is not saved (V1 saves only complete ones).
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A complete session is kept in memory this long after its last heartbeat
    /// (or its completion, if later), so a viewer who comes back is served the
    /// same snapshot and revision. After that the Transcript cache answers.
    /// </summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A Failed or Unsupported session is kept this long after it ended - not
    /// extended by heartbeats - so the extension can read the reason. The first
    /// heartbeat after that starts a new session, which is the retry: long
    /// enough that a tab polling once a second does not hammer yt-dlp, short
    /// enough that a fixed problem (Deno installed, network back) is picked up
    /// without a restart.
    /// </summary>
    public TimeSpan FailedRetention { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The default for <see cref="HeadSeconds"/>: two minutes.</summary>
    public const double DefaultHeadSeconds = 120.0;

    /// <summary>
    /// How much of a video's audio is converted on its own, ahead of the whole
    /// file, so its first windows can be heard while the rest converts (W17).
    /// Two minutes converts in about 0.3 s and fixes the first four windows
    /// (91 s of Coverage); the whole of a 60-minute video takes about 8.6 s.
    /// A video yt-dlp says is no longer than this is converted whole at once.
    /// 0 turns the head off.
    /// </summary>
    public double HeadSeconds { get; set; } = DefaultHeadSeconds;

    /// <summary>
    /// How long the heartbeat that starts a session may wait for its
    /// Transcript cache lookup before answering (W17). A cached video is then
    /// answered complete in the very first reply instead of queued, so the
    /// extension does not hold it for a second poll; a miss is answered as
    /// soon as it is known to be one. Later heartbeats never wait. Zero
    /// answers at once, as before.
    /// </summary>
    public TimeSpan FirstAnswerWait { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the sweeper looks for sessions to drop.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Where each session gets its own working directory for the download.
    /// Deleted when the session ends, whatever the outcome.
    /// </summary>
    public string ScratchDirectory { get; set; } =
        Path.Combine(DataLocations.DataDirectory(null), "scratch", "watch");

    /// <summary>The Transcript cache, shared with Jobs; a complete session saves under the video's key.</summary>
    public string TranscriptDirectory { get; set; } = DataLocations.TranscriptDirectory(null, null);

    /// <summary>The Bad Words List file, shared with Jobs.</summary>
    public string BadWordsPath { get; set; } = DataLocations.BadWordsPath(null, null);

    /// <summary>Throws when a value could not work.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A time span is zero or negative, or <see cref="HeadSeconds"/> is negative or not finite.</exception>
    /// <exception cref="ArgumentException">A path is blank.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CompletedRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FailedRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SweepInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(FirstAnswerWait, TimeSpan.Zero);
        if (!double.IsFinite(HeadSeconds) || HeadSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeadSeconds),
                HeadSeconds,
                "Must be a finite number of seconds, 0 or more (0 turns the head off)."
            );
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ScratchDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(TranscriptDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(BadWordsPath);
    }
}
