namespace FoulFilterNet.Watch;

/// <summary>
/// Where a <see cref="WatchSession"/> is in making one Web Video safe to watch.
/// </summary>
/// <remarks>
/// <para>
/// A miss walks <see cref="Queued"/> → <see cref="Fetching"/> →
/// <see cref="Preparing"/> → <see cref="Transcribing"/> → <see cref="Complete"/>.
/// A Transcript cache hit goes straight from <see cref="Queued"/> to
/// <see cref="Complete"/>. Any working state can end in <see cref="Failed"/>,
/// <see cref="Unsupported"/> or <see cref="Cancelled"/>.
/// </para>
/// <para>
/// The plan drew <c>Resolving</c> and <c>Downloading</c> as two states. ADR-0007
/// collapsed them into one yt-dlp call (W08), so they are one state here.
/// </para>
/// </remarks>
public enum WatchState
{
    /// <summary>Created, not started yet.</summary>
    Queued,

    /// <summary>yt-dlp is reading the watch page and downloading the audio.</summary>
    Fetching,

    /// <summary>
    /// Converting the download to the 16 kHz analysis WAV and opening it with
    /// the engine, which loads the model if it is not resident.
    /// </summary>
    Preparing,

    /// <summary>Transcribing window by window, in the playhead's order.</summary>
    Transcribing,

    /// <summary>Every window is heard (or the Transcript came from the cache): the Hits are final.</summary>
    Complete,

    /// <summary>The audio could not be fetched or transcribed; the snapshot's reason says why.</summary>
    Failed,

    /// <summary>The video is out of scope for V1 (a livestream or a premiere); the reason says which.</summary>
    Unsupported,

    /// <summary>Stopped by a cancel or by idle expiry before it finished.</summary>
    Cancelled,
}

/// <summary>Questions about a <see cref="WatchState"/>.</summary>
public static class WatchStates
{
    /// <summary>The session has stopped working, one way or another.</summary>
    public static bool IsTerminal(this WatchState state) =>
        state
            is WatchState.Complete
                or WatchState.Failed
                or WatchState.Unsupported
                or WatchState.Cancelled;

    /// <summary>The session stopped without a usable result: Failed or Unsupported.</summary>
    public static bool IsFailure(this WatchState state) =>
        state is WatchState.Failed or WatchState.Unsupported;
}
