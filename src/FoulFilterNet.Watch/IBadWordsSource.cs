using FoulFilterNet.Domain;

namespace FoulFilterNet.Watch;

/// <summary>
/// The Bad Words List as it stands now. A Watch Session asks on every
/// snapshot rather than once at start, so an edit to the list reaches the
/// viewer on the next heartbeat - including a session long since complete -
/// without transcribing anything again (<see cref="WatchProgress.WithBadWords"/>).
/// </summary>
/// <remarks>
/// It is asked about once a second per watched video, so an implementation must
/// make "nothing changed" cheap. Returning the same instance, or an instance
/// with the same phrases, is not a change: the session's revision only moves
/// when the phrases do.
/// </remarks>
public interface IBadWordsSource
{
    /// <summary>The current list.</summary>
    /// <exception cref="InvalidOperationException">
    /// There is no usable list and never has been, so there is nothing to
    /// censor with. A session that meets this at its start fails with the
    /// message as its reason.
    /// </exception>
    BadWordsList GetCurrent();
}
