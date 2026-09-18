// The Playback Gate: whether the video must be held (paused) because the
// stretch just ahead of the playhead has no final Hits yet, and what the
// overlay says about it. Pure.
//
// `decideGate(input)` answers one question from a snapshot of everything
// that matters; it keeps no state. The two facts that must survive between
// calls - whether *we* hold the video, and whether the user wants it paused -
// live in gate-controller.js, which feeds them back in as `heldByUs` and
// `userPaused`. W15 builds the input on every heartbeat, page state change
// and media event, and hands the decision to the controller.
//
// Actions:
// - `hold`: the video must stay paused. Idempotent: a controller that already
//   holds does nothing.
// - `release`: drop our hold; play again only if `resume` (we paused a
//   playing video and the user has not paused since).
// - `none`: we have no business with the video. A hold we had is forgotten
//   without playing (an ad, a navigation, the end).
//
// Thresholds (decided, W13): the 8 s and 30 s of the plan are wall-clock
// safety margins - how long until the playhead reaches uncovered audio. At
// rate r the playhead eats r media seconds per second, so the media seconds
// needed are threshold x r: 16 s / 60 s at 2x, 4 s / 15 s at 0.5x. Hold when
// covered-ahead < HOLD x r (strictly), release a hold when it reaches
// RESUME x r; in between, keep doing what we do (hysteresis, so a window
// boundary cannot make it stutter). A rate that is not a positive finite
// number counts as 1.
//
// The end of the video (decided, W13):
// - `state: complete` means every window is final, so the video counts as
//   covered to its end whatever the coverage says. A cached session's
//   coverage stops at the last word heard (646.3 s of a 649.2 s video, W10);
//   comparing it with the duration would hold forever.
// - Otherwise the covered run reaching within END_TOLERANCE of the duration
//   counts as covered to the end. The duration is `input.duration` (the
//   element's), else the view's; never required.
//
// Precedence, first match wins:
//   leaving -> none; ad -> none; not filterable (idle, no element) -> none;
//   ended -> none; unfiltered -> release/none; an error -> hold (fail-closed)
//   or release/none (fail-open); no view yet (or a view for another video)
//   -> hold 'starting'; complete or covered to the end -> release/none;
//   otherwise coverage decides, and a hold of a failed/unsupported session
//   carries that reason (or lets go, fail-open).
//
// Also decided (W13):
// - A failed session's coverage is still final, so a failure part-way lets
//   the covered stretch play and holds (fail-closed) only where the
//   uncovered part starts. Failures at fetch time, the usual kind, have none.
// - `cancelled` is not a failure: the next heartbeat starts a new session,
//   so it holds like `queued` until coverage arrives.
// - A server-side error (`unreachable`, `timeout`, `http`, `bad_response`,
//   `extension`) does not matter once the view is `complete`: every Hit is
//   already known. An `audio` error (the element cannot be routed through
//   Web Audio, W14) always holds, since nothing can be censored.
// - A hold on a video the user has paused is still a hold: it is recorded,
//   so a play press is caught, but releasing it does not start the video.
// - A play press during a hold (the video found playing while we hold, or
//   the controller's `playRequested`) keeps the hold with reason
//   `still-preparing`, unless the hold is for a failure, whose reason stays.
// - The overlay is visible exactly when the action is `hold`.

import { describeError } from './connection.js';
import { isFilterable, shouldMute } from './page-state.js';
import { coveredAhead, coveredRunEnd } from './coverage.js';

/** Hold when less than this many wall-clock seconds are covered ahead. */
export const HOLD_SECONDS = 8;

/** Release a hold once this many wall-clock seconds are covered ahead. */
export const RESUME_SECONDS = 30;

/** A covered run ending this close (seconds) to the duration reaches the end. */
export const END_TOLERANCE = 0.5;

/** Error kinds that say the page cannot be filtered even with every Hit known. */
const FATAL_ERROR_KINDS = Object.freeze(['audio']);

/**
 * @typedef {import('./api-client.js').WatchView} WatchView
 * @typedef {import('./page-state.js').PageState} PageState
 *
 * @typedef {object} GateError  Why the video cannot be filtered at the moment (W15 passes it in).
 * @property {string} kind  An api-client error kind, `extension`, or `audio` (Web Audio refused the element).
 * @property {number} [status]
 * @property {string} [detail]
 *
 * @typedef {object} GateInput
 * @property {PageState} page  The page watcher's state.
 * @property {WatchView | null} view  The latest view for this generation; null before the first answer.
 * @property {GateError | null} [error]  The latest heartbeat failed, or the audio graph could not be built.
 * @property {boolean} [unfiltered]  The user chose "Watch unfiltered" for this video.
 * @property {'closed' | 'open'} [failPolicy]  Default `closed`.
 * @property {number} position  `video.currentTime`, seconds.
 * @property {number} [playbackRate]  `video.playbackRate`; default 1.
 * @property {number | null} [duration]  `video.duration`; NaN/Infinity/null fall back to the view's.
 * @property {boolean} paused  `video.paused`.
 * @property {boolean} ended  `video.ended`.
 * @property {boolean} heldByUs  The controller holds the video.
 * @property {boolean} [userPaused]  The user wants it paused: releasing must not play.
 * @property {boolean} [playRequested]  A play was pressed during the current hold.
 * @property {string} [serverUrl]  For error sentences.
 *
 * @typedef {'hold' | 'release' | 'none'} GateAction
 *
 * @typedef {'starting' | 'preparing' | 'still-preparing' | 'not-keeping-up' | 'failed' | 'unsupported' | 'error'
 *   | 'covered' | 'complete' | 'covered-to-end' | 'unfiltered' | 'fail-open'
 *   | 'leaving' | 'ad' | 'not-watching' | 'ended'} GateReason
 *
 * @typedef {object} GateDecision
 * @property {GateAction} action
 * @property {GateReason} reason
 * @property {boolean} heldByUs  Whether we hold once the decision is applied.
 * @property {boolean} resume  For `release`: play again. False for every other action.
 *
 * @typedef {object} OverlayModel
 * @property {boolean} visible
 * @property {string} title
 * @property {string} detail
 * @property {number | null} percent  0-99 while transcribing towards the resume threshold, else null.
 * @property {boolean} showUnfiltered  Offer the "Watch unfiltered" button.
 */

/**
 * @param {GateAction} action
 * @param {GateReason} reason
 * @param {boolean} [resume]
 * @returns {GateDecision}
 */
function decision(action, reason, resume = false) {
  return Object.freeze({ action, reason, heldByUs: action === 'hold', resume: action === 'release' && resume });
}

/**
 * Drop our hold if we have one (playing again unless the user paused),
 * else leave the video alone.
 *
 * @param {GateInput} input
 * @param {GateReason} reason
 */
function letGo(input, reason) {
  return input.heldByUs ? decision('release', reason, !input.userPaused) : decision('none', reason);
}

/**
 * Hold, turning a preparing hold into `still-preparing` when play was pressed
 * during it.
 *
 * @param {GateInput} input
 * @param {GateReason} reason
 */
function hold(input, reason) {
  const pressed = input.heldByUs && (!input.paused || input.playRequested === true);
  const preparing = reason === 'starting' || reason === 'preparing' || reason === 'not-keeping-up';
  return decision('hold', pressed && preparing ? 'still-preparing' : reason);
}

/**
 * A positive finite playback rate, else 1.
 *
 * @param {unknown} rate
 */
export function effectiveRate(rate) {
  return typeof rate === 'number' && Number.isFinite(rate) && rate > 0 ? rate : 1;
}

/**
 * The duration to measure "the end" against, or null when unknown.
 *
 * @param {GateInput} input
 * @returns {number | null}
 */
function knownDuration(input) {
  for (const d of [input.duration, input.view?.duration]) {
    if (typeof d === 'number' && Number.isFinite(d) && d > 0) return d;
  }
  return null;
}

/**
 * The view, if it is about the video on the page.
 *
 * @param {GateInput} input
 * @returns {WatchView | null}
 */
function currentView(input) {
  const view = input.view ?? null;
  return view !== null && view.videoId === input.page?.videoId ? view : null;
}

/**
 * Media seconds covered ahead of the playhead, Infinity when the covered run
 * reaches the end of the video.
 *
 * @param {WatchView} view
 * @param {GateInput} input
 * @returns {number}
 */
export function mediaAhead(view, input) {
  if (view.state === 'complete') {
    return Infinity;
  }
  const end = coveredRunEnd(view.coverage, input.position);
  if (end === null) {
    return 0;
  }
  const duration = knownDuration(input);
  if (duration !== null && end >= duration - END_TOLERANCE) {
    return Infinity;
  }
  return coveredAhead(view.coverage, input.position);
}

/**
 * What the gate does now.
 *
 * @param {GateInput} input
 * @returns {GateDecision}
 */
export function decideGate(input) {
  const page = input.page;
  if (!page || shouldMute(page)) return decision('none', 'leaving');
  if (page.phase === 'watching' && page.adShowing) return decision('none', 'ad');
  if (!isFilterable(page)) return decision('none', 'not-watching');
  if (input.ended) return decision('none', 'ended');
  if (input.unfiltered) return letGo(input, 'unfiltered');

  const failOpen = input.failPolicy === 'open';
  const view = currentView(input);

  if (input.error) {
    const ignorable = view?.state === 'complete' && !FATAL_ERROR_KINDS.includes(input.error.kind);
    if (!ignorable) return failOpen ? letGo(input, 'fail-open') : hold(input, 'error');
  }

  if (view === null) return hold(input, 'starting');

  const ahead = mediaAhead(view, input);
  if (ahead === Infinity) {
    return letGo(input, view.state === 'complete' ? 'complete' : 'covered-to-end');
  }

  const rate = effectiveRate(input.playbackRate);
  const holding = input.heldByUs ? ahead < RESUME_SECONDS * rate : ahead < HOLD_SECONDS * rate;
  if (!holding) return letGo(input, 'covered');

  if (view.state === 'failed' || view.state === 'unsupported') {
    return failOpen ? letGo(input, 'fail-open') : hold(input, view.state);
  }
  return hold(input, view.keepingUp === false ? 'not-keeping-up' : 'preparing');
}

/** @type {Readonly<OverlayModel>} */
export const HIDDEN_OVERLAY = Object.freeze({
  visible: false,
  title: '',
  detail: '',
  percent: null,
  showUnfiltered: false,
});

/** What each in-progress state is doing, for the overlay. */
const STATE_DETAIL = Object.freeze({
  queued: 'Waiting for FoulFilterNet to start on it.',
  fetching: 'Fetching the audio from YouTube.',
  preparing: 'Preparing the audio.',
  transcribing: 'Listening ahead of you.',
  cancelled: 'Starting again.',
});

/** Advice for a failed fetch, by the service's `failure_kind`. */
const FAILURE_ADVICE = Object.freeze({
  not_installed: 'yt-dlp is not installed where FoulFilterNet runs. Install it and put it on the PATH.',
  js_runtime_missing: 'yt-dlp needs Deno to read YouTube. Install Deno where FoulFilterNet runs.',
  unavailable: 'YouTube says this video is not available.',
  sign_in_required:
    'YouTube wants a signed-in viewer for this video (age-restricted, private or members-only), which FoulFilterNet cannot be.',
  unsupported: 'yt-dlp cannot fetch this kind of video.',
  network: 'FoulFilterNet could not download the audio. Check that its machine is online.',
  failed: 'yt-dlp could not fetch the audio. Updating yt-dlp often fixes this.',
});

/** The sentences for an error, by kind. */
const ERROR_TITLES = Object.freeze({
  unreachable: 'FoulFilterNet is not reachable',
  timeout: 'FoulFilterNet is not answering',
  extension: 'FoulFilter lost touch with the extension',
  audio: 'FoulFilter cannot filter the sound on this page',
});

/**
 * Append the server's own words to advice, unless they add nothing.
 *
 * @param {string} advice
 * @param {string | null | undefined} reason
 */
function withReason(advice, reason) {
  const said = typeof reason === 'string' ? reason.trim() : '';
  return said === '' || advice.includes(said) ? advice : `${advice} (${said})`;
}

/**
 * @param {GateError} error
 * @param {string} serverUrl
 */
function errorDetail(error, serverUrl) {
  if (error.kind === 'audio') {
    return 'Its sound is already taken (FoulFilter was reloaded, or another extension got there first). Reload the page to filter it.';
  }
  return describeError(error, serverUrl);
}

/**
 * How far the covered run ahead is towards the resume threshold, 0-99
 * (100 would mean released), while transcribing; else null.
 *
 * @param {WatchView | null} view
 * @param {GateInput} input
 * @returns {number | null}
 */
function towardsResume(view, input) {
  if (view === null || view.state !== 'transcribing') {
    return null;
  }
  const needed = RESUME_SECONDS * effectiveRate(input.playbackRate);
  const ahead = Math.min(mediaAhead(view, input), needed);
  return Math.min(99, Math.max(0, Math.floor((ahead / needed) * 100)));
}

/**
 * What the overlay shows for a decision made from `input` (visible only on
 * `hold`). Pass the decision `decideGate(input)` returned; it is recomputed
 * if omitted.
 *
 * @param {GateInput} input
 * @param {GateDecision} [decided]
 * @returns {OverlayModel}
 */
export function gateOverlay(input, decided = decideGate(input)) {
  if (decided.action !== 'hold') {
    return HIDDEN_OVERLAY;
  }
  const view = currentView(input);
  const serverUrl = input.serverUrl ?? 'its address';
  const paused = input.userPaused ? ' You paused the video, so it will stay paused until you press play.' : '';

  /**
   * @param {string} title
   * @param {string} detail
   * @param {number | null} [percent]
   * @returns {OverlayModel}
   */
  const shown = (title, detail, percent = null) =>
    Object.freeze({ visible: true, title, detail, percent, showUnfiltered: true });

  switch (decided.reason) {
    case 'starting':
      return shown('FoulFilter is starting', `Asking FoulFilterNet about this video.${paused}`);
    case 'preparing':
      return shown(
        'FoulFilter is preparing this video',
        `${STATE_DETAIL[view?.state ?? 'queued'] ?? STATE_DETAIL.queued} Playback starts when enough is ready.${paused}`,
        towardsResume(view, input),
      );
    case 'still-preparing':
      return shown(
        'Not yet: FoulFilter is still preparing this video',
        'Playback starts by itself as soon as enough is ready.',
        towardsResume(view, input),
      );
    case 'not-keeping-up':
      return shown(
        'FoulFilter is not keeping up',
        `FoulFilterNet is transcribing more slowly than the video plays. Playback goes on when it has caught up.${paused}`,
        towardsResume(view, input),
      );
    case 'failed': {
      const advice = FAILURE_ADVICE[view?.failureKind ?? 'failed'] ?? FAILURE_ADVICE.failed;
      const detail = view?.failureKind ? withReason(advice, view.reason) : (view?.reason ?? advice);
      return shown('FoulFilter could not prepare this video', detail);
    }
    case 'unsupported':
      return shown(
        'FoulFilter cannot filter this video',
        view?.reason?.trim() ? view.reason.trim() : 'Livestreams and premieres cannot be filtered.',
      );
    case 'error': {
      const error = input.error ?? { kind: 'unknown' };
      return shown(ERROR_TITLES[error.kind] ?? 'FoulFilterNet had a problem', errorDetail(error, serverUrl));
    }
    default:
      return shown('FoulFilter is holding this video', `Playback starts when it is safe.${paused}`);
  }
}
