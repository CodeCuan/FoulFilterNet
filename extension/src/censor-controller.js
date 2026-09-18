// Keeps a <video>'s audio graph censoring the Hits ahead of the playhead.
// Adapter, over an injected element, graph and timers; the plan itself is
// schedule.js.
//
// Modes (W15 sets them):
// - `filter`: censor the Hits (the default).
// - `mute`: close the gain now and schedule nothing - leaving the page
//   (`shouldMute`), or anything W15 cannot vouch for.
// - `open`: open the gain now and schedule nothing - an ad, "Watch
//   unfiltered", the extension disabled.
//
// In `filter` mode it re-plans on a tick (TICK_MS) while the video is really
// playing, and on the media events that move the playhead or stop it. It
// only schedules while the media is really playing (ADR-0007): `!paused`,
// `readyState >= 3`, not `seeking`, and not stalled. Stalled means a
// `seeking`, `waiting` or `emptied` has fired and no `seeked` or `playing`
// since: during a seek or a rebuffer `currentTime` stands still for over a
// second while the audio clock runs, so a plan made then would land late. A
// stop re-plans at once with nothing scheduled, which cancels what was (the
// gain is left closed only if the playhead is inside a Hit); `playing` or
// `seeked` re-plans for real. After a navigation W12 notes the element's
// source changes: `emptied` stalls it until the new source plays.
//
// A plan is applied only when it differs from the last one applied
// (`samePlan`, within 10 ms) or the method changed, so an unchanged tick
// touches nothing. Changing mode always acts, once.
//
// `stop()` removes the listeners and the tick and leaves the gain as it is;
// W15 decides (e.g. `graph.muteNow()`).

import { HORIZON_SECONDS, planSchedule, samePlan } from './schedule.js';

/**
 * @typedef {import('./schedule.js').SchedulePlan} SchedulePlan
 * @typedef {import('./audio-graph.js').CensorMethod} CensorMethod
 *
 * @typedef {'filter' | 'mute' | 'open'} CensorMode
 *
 * @typedef {object} MediaLike  The parts of an HTMLVideoElement used here.
 * @property {number} currentTime
 * @property {number} playbackRate
 * @property {boolean} paused
 * @property {boolean} seeking
 * @property {number} readyState
 * @property {(type: string, listener: () => void) => void} addEventListener
 * @property {(type: string, listener: () => void) => void} removeEventListener
 *
 * @typedef {object} GraphLike  The parts of an AudioGraph used here.
 * @property {(plan: SchedulePlan, method: CensorMethod) => void} apply
 * @property {() => void} muteNow
 * @property {() => void} openNow
 * @property {number} currentTime
 */

/** How often (ms) the plan is refreshed while playing. */
export const TICK_MS = 100;

/** HTMLMediaElement.HAVE_FUTURE_DATA. */
const HAVE_FUTURE_DATA = 3;

/** Events that stop the media clock until `seeked`/`playing`. */
const STALLING_EVENTS = Object.freeze(['seeking', 'waiting', 'emptied']);

/** Events that end a stall. */
const RESUMING_EVENTS = Object.freeze(['seeked', 'playing']);

/** Events that only call for a fresh plan. */
const REPLAN_EVENTS = Object.freeze(['play', 'pause', 'ratechange', 'timeupdate', 'ended', 'loadedmetadata']);

/** Every event listened to. */
export const MEDIA_EVENTS = Object.freeze([...STALLING_EVENTS, ...RESUMING_EVENTS, ...REPLAN_EVENTS]);

/**
 * @param {object} options
 * @param {MediaLike} options.video
 * @param {GraphLike} options.graph
 * @param {unknown} [options.hits]
 * @param {CensorMethod} [options.method]  Default `silence`.
 * @param {number} [options.offsetMs]  Default 0.
 * @param {CensorMode} [options.mode]  Default `filter`.
 * @param {number} [options.horizon]  Audio seconds; default HORIZON_SECONDS.
 * @param {(fn: () => void, ms: number) => unknown} [options.setInterval]
 * @param {(id: unknown) => void} [options.clearInterval]
 */
export function createCensorController({
  video,
  graph,
  hits = [],
  method = 'silence',
  offsetMs = 0,
  mode = 'filter',
  horizon = HORIZON_SECONDS,
  setInterval = globalThis.setInterval,
  clearInterval = globalThis.clearInterval,
}) {
  let currentHits = hits;
  let currentMethod = normaliseMethod(method);
  let currentOffset = offsetMs;
  let currentMode = normaliseMode(mode);
  let stalled = false;
  let stopped = false;
  /** @type {unknown} */
  let timer = null;
  /** @type {{ mode: CensorMode, plan: SchedulePlan | null, method: CensorMethod } | null} */
  let applied = null;
  let applyCount = 0;

  function reallyPlaying() {
    return !stalled && !video.paused && !video.seeking && video.readyState >= HAVE_FUTURE_DATA;
  }

  function startTicking() {
    if (timer === null) timer = setInterval(update, TICK_MS);
  }

  function stopTicking() {
    if (timer !== null) {
      clearInterval(timer);
      timer = null;
    }
  }

  function update() {
    if (stopped) return;
    if (currentMode !== 'filter') {
      stopTicking();
      if (applied?.mode !== currentMode) {
        if (currentMode === 'mute') graph.muteNow();
        else graph.openNow();
        applied = { mode: currentMode, plan: null, method: currentMethod };
        applyCount++;
      }
      return;
    }

    const playing = reallyPlaying();
    if (playing) startTicking();
    else stopTicking();

    const plan = planSchedule({
      hits: currentHits,
      currentTime: video.currentTime,
      playbackRate: video.playbackRate,
      audioNow: graph.currentTime,
      horizon,
      offsetMs: currentOffset,
      playing,
    });
    if (applied?.mode === 'filter' && applied.method === currentMethod && samePlan(applied.plan, plan)) {
      return;
    }
    graph.apply(plan, currentMethod);
    applied = { mode: 'filter', plan, method: currentMethod };
    applyCount++;
  }

  function onStall() {
    stalled = true;
    update();
  }

  function onResume() {
    stalled = false;
    update();
  }

  for (const type of STALLING_EVENTS) video.addEventListener(type, onStall);
  for (const type of RESUMING_EVENTS) video.addEventListener(type, onResume);
  for (const type of REPLAN_EVENTS) video.addEventListener(type, update);

  update();

  return {
    /** New Hits (a view's `hits`); re-plans at once. */
    setHits(/** @type {unknown} */ hits) {
      currentHits = hits;
      update();
    },

    /** `silence` or `bleep` (anything else is silence); re-plans at once. */
    setMethod(/** @type {CensorMethod} */ method) {
      currentMethod = normaliseMethod(method);
      update();
    },

    /** The user's offset, milliseconds; re-plans at once. */
    setOffset(/** @type {number} */ ms) {
      currentOffset = ms;
      update();
    },

    /** `filter`, `mute` or `open` (anything else is `mute`); acts at once. */
    setMode(/** @type {CensorMode} */ mode) {
      currentMode = normaliseMode(mode);
      update();
    },

    /** Re-plan now, e.g. after the context was resumed. */
    refresh() {
      update();
    },

    /** The current mode. */
    get mode() {
      return currentMode;
    },

    /** The last plan applied in `filter` mode, or null. */
    get plan() {
      return applied?.plan ?? null;
    },

    /** How many times the graph was told something (apply, muteNow, openNow); for debugging. */
    get applyCount() {
      return applyCount;
    },

    /** Whether the tick is running. */
    get ticking() {
      return timer !== null;
    },

    /** Stop listening and ticking. Idempotent; leaves the gain as it is. */
    stop() {
      if (stopped) return;
      stopped = true;
      stopTicking();
      for (const type of STALLING_EVENTS) video.removeEventListener(type, onStall);
      for (const type of RESUMING_EVENTS) video.removeEventListener(type, onResume);
      for (const type of REPLAN_EVENTS) video.removeEventListener(type, update);
    },
  };
}

/** @param {unknown} method @returns {CensorMethod} */
function normaliseMethod(method) {
  return method === 'bleep' ? 'bleep' : 'silence';
}

/** @param {unknown} mode @returns {CensorMode} */
function normaliseMode(mode) {
  return mode === 'filter' || mode === 'open' ? mode : 'mute';
}
