// Applies the Playback Gate's decisions to a <video>: pauses it for a hold,
// plays it again only when releasing a hold of our own, and tells the user's
// pauses and play presses apart from the ones it causes. Adapter, over an
// injected element and clock; the decisions themselves are gate.js.
//
// What it remembers (and W15 feeds back into decideGate):
// - `heldByUs`: we hold the video.
// - `userPaused`: while we hold, releasing must not start the video - the
//   user paused it during the hold, or it was already paused when the hold
//   began (autoplay off, or paused by the user before).
// - `playRequested`: a play was pressed during the current hold.
//
// Our own pause()/play() fire `pause`/`play` events later (a task, not at
// once), so each call records that its event is expected; the next event of
// that type within SELF_EVENT_MS is ours and ignored. Any other `pause` is
// the user's (or YouTube's, which amounts to the same thing: not ours to
// undo). Any other `play` is a play press: during a hold the video is paused
// again at once, before a word can be heard, and `onPlayWhileHeld` lets W15
// re-decide and update the overlay.
//
// `apply` is idempotent: holding a held video, or releasing a video we do
// not hold, calls nothing on the element.

/**
 * @typedef {import('./gate.js').GateDecision} GateDecision
 *
 * @typedef {object} VideoLike  The parts of an HTMLVideoElement used here.
 * @property {boolean} paused
 * @property {() => (Promise<void> | void)} play
 * @property {() => void} pause
 * @property {(type: string, listener: (event: Event) => void) => void} addEventListener
 * @property {(type: string, listener: (event: Event) => void) => void} removeEventListener
 *
 * @typedef {'paused' | 'played' | 'held' | 'released' | 'nothing'} ApplyResult
 *   paused: pause() was called; held: a hold began on a video already paused;
 *   played: play() was called; released: a hold ended without playing.
 */

/** How long (ms) after our own pause()/play() its event counts as ours. */
export const SELF_EVENT_MS = 1000;

/**
 * @param {object} options
 * @param {VideoLike} options.video
 * @param {() => number} [options.now]  Milliseconds; default `Date.now`.
 * @param {() => void} [options.onPlayWhileHeld]  A play press was caught during a hold (and undone).
 * @param {() => void} [options.onUserPause]  The user paused the video (not us).
 * @param {(error: unknown) => void} [options.onPlayError]  Our play() was refused (e.g. autoplay policy).
 */
export function createGateController({ video, now = Date.now, onPlayWhileHeld, onUserPause, onPlayError }) {
  let heldByUs = false;
  let resumeOnRelease = false;
  let playRequested = false;
  /** @type {number | null} */
  let pauseExpectedUntil = null;
  /** @type {number | null} */
  let playExpectedUntil = null;
  let stopped = false;

  function pauseVideo() {
    pauseExpectedUntil = now() + SELF_EVENT_MS;
    video.pause();
  }

  function playVideo() {
    playExpectedUntil = now() + SELF_EVENT_MS;
    try {
      const result = video.play();
      if (result && typeof result.then === 'function') {
        result.then(undefined, refused);
      }
    } catch (error) {
      refused(error);
    }
  }

  /** @param {unknown} error */
  function refused(error) {
    playExpectedUntil = null;
    // A pause() while play() is pending rejects it with AbortError: our own doing.
    if (/** @type {any} */ (error)?.name === 'AbortError') return;
    onPlayError?.(error);
  }

  /**
   * Whether an event of this kind was expected from our own call, consuming
   * the expectation.
   *
   * @param {'pause' | 'play'} type
   */
  function ours(type) {
    const until = type === 'pause' ? pauseExpectedUntil : playExpectedUntil;
    if (type === 'pause') pauseExpectedUntil = null;
    else playExpectedUntil = null;
    return until !== null && now() <= until;
  }

  function onPause() {
    if (stopped || ours('pause')) return;
    resumeOnRelease = false;
    playRequested = false;
    onUserPause?.();
  }

  function onPlay() {
    if (stopped || ours('play')) return;
    if (!heldByUs) return;
    resumeOnRelease = true;
    playRequested = true;
    if (!video.paused) pauseVideo();
    onPlayWhileHeld?.();
  }

  video.addEventListener('pause', onPause);
  video.addEventListener('play', onPlay);

  return {
    /**
     * Carry out a decision.
     *
     * @param {Pick<GateDecision, 'action'> & Partial<GateDecision>} decision
     * @returns {ApplyResult}
     */
    apply(decision) {
      if (stopped) return 'nothing';
      switch (decision?.action) {
        case 'hold': {
          if (!heldByUs) {
            heldByUs = true;
            playRequested = false;
            resumeOnRelease = !video.paused;
            if (!video.paused) {
              pauseVideo();
              return 'paused';
            }
            return 'held';
          }
          if (!video.paused) {
            // Someone started it under our hold without a play event reaching us yet.
            resumeOnRelease = true;
            pauseVideo();
            return 'paused';
          }
          return 'nothing';
        }
        case 'release': {
          if (!heldByUs) return 'nothing';
          const resume = resumeOnRelease && decision.resume !== false;
          heldByUs = false;
          resumeOnRelease = false;
          playRequested = false;
          if (resume && video.paused) {
            playVideo();
            return 'played';
          }
          return 'released';
        }
        case 'none': {
          if (!heldByUs) return 'nothing';
          heldByUs = false;
          resumeOnRelease = false;
          playRequested = false;
          return 'released';
        }
        default:
          return 'nothing';
      }
    },

    /** We hold the video. */
    get heldByUs() {
      return heldByUs;
    },

    /** While we hold: releasing will not start the video. False when not holding. */
    get userPaused() {
      return heldByUs && !resumeOnRelease;
    },

    /** A play was pressed during the current hold. */
    get playRequested() {
      return heldByUs && playRequested;
    },

    /** Stop listening. Idempotent; later events and decisions do nothing. */
    stop() {
      if (stopped) return;
      stopped = true;
      video.removeEventListener('pause', onPause);
      video.removeEventListener('play', onPlay);
    },
  };
}
