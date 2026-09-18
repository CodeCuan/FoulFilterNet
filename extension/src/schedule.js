// Live Censoring's plan: when, on the AudioContext clock, the programme gain
// must close and open again for the Hits just ahead of the playhead. Pure.
//
// A Hit is `{start, end, phrase}` in media seconds, already padded (0.15 s
// before, 0.25 s after) and merged by the service. The media clock maps onto
// the audio clock as
//
//   audio time = audioNow + (media time - currentTime) / rate + offset
//
// which the W01 spike measured accurate to about 5 ms with no drift, but only
// while the media is really playing (ADR-0007): during a seek or a rebuffer
// `currentTime` stands still while the audio clock runs. So the caller says
// whether it is `playing` (`!paused && readyState >= 3`, not seeking or
// stalled), and nothing is scheduled otherwise.
//
// Decided (W14):
// - Every Hit's span is mapped onto the audio clock first, then the spans are
//   sorted and merged when they overlap or are less than MERGE_GAP_SECONDS
//   apart (audio seconds), so the graph's short ramps can never overlap. The
//   offset is one shift for every Hit, so merging after it is the same as
//   before it. Merging runs over every Hit, not only those in the horizon, so
//   a chain of touching Hits reaching past the horizon opens only at its end.
// - A merged span is in the plan when it starts no later than `horizon` audio
//   seconds from now (inclusive, within HORIZON_EPSILON) and ends after now.
//   Its open event is always included, even beyond the horizon: the gain must
//   not be left closed if the next tick is late.
// - A span that has already started (its close time is at or before now) is
//   `closedNow`: the gain closes at `audioNow` and only its open is scheduled.
//   A span that has already ended is dropped. So no event lies in the past.
// - Spans are half-open: at exactly its end a span is over (open now).
// - Not playing (paused, stalled, rate 0 or not finite, a clock not finite):
//   no events. The gain is `closedNow` exactly when the playhead is inside a
//   valid Hit (`isInsideHit`, no offset), so a resume cannot leak the word
//   the video was paused on before the next plan lands.
// - Hits that are not objects, or whose ends are not finite, or whose end is
//   not above its start, are ignored. A horizon that is not a positive finite
//   number is HORIZON_SECONDS; an offset that is not finite is 0.
// - `samePlan(a, b)` compares two plans within PLAN_TOLERANCE_SECONDS, so the
//   controller can skip re-applying a plan each ~100 ms tick when nothing
//   changed: both clocks advance together, and `currentTime` jitters by one
//   render quantum (2.7 ms at 48 kHz). A seek, a rate change or a Hit edge
//   crossing now all change the plan.

/**
 * @typedef {object} Hit
 * @property {number} start  Media seconds, padded.
 * @property {number} end  Media seconds, padded.
 * @property {string} [phrase]
 *
 * @typedef {object} ScheduleInput
 * @property {unknown} hits  The view's Hits.
 * @property {number} currentTime  `video.currentTime`, media seconds.
 * @property {number} playbackRate  `video.playbackRate`.
 * @property {number} audioNow  `AudioContext.currentTime`, audio seconds.
 * @property {number} [horizon]  Audio seconds ahead to schedule; default HORIZON_SECONDS.
 * @property {number} [offsetMs]  The user's offset (settings), milliseconds; positive = later.
 * @property {boolean} playing  The media is really playing (`!paused && readyState >= 3`, not seeking/stalled).
 *
 * @typedef {object} GainEvent
 * @property {number} time  Audio seconds.
 * @property {0 | 1} gain  The programme gain from then on: 0 closed, 1 open.
 *
 * @typedef {object} SchedulePlan
 * @property {number} audioNow  The audio time the plan was made at.
 * @property {boolean} closedNow  The programme gain must be closed from `audioNow`.
 * @property {readonly GainEvent[]} events  Ascending, alternating, all after `audioNow`;
 *   the first is an open when `closedNow`, else a close.
 * @property {string} key  Identifies the plan (times to the millisecond); `audioNow` is not part of it.
 */

/** How far ahead (audio seconds) Hits are scheduled. */
export const HORIZON_SECONDS = 2;

/** Spans closer than this (audio seconds) are merged into one. */
export const MERGE_GAP_SECONDS = 0.02;

/** Two plans whose times differ by at most this (seconds) are the same. */
export const PLAN_TOLERANCE_SECONDS = 0.01;

/** A span starting this close past the horizon still counts as inside it. */
export const HORIZON_EPSILON = 1e-9;

/**
 * The Hits that are usable, as `{start, end}` pairs in their given order.
 *
 * @param {unknown} hits
 * @returns {{ start: number, end: number }[]}
 */
export function validHits(hits) {
  if (!Array.isArray(hits)) {
    return [];
  }
  const valid = [];
  for (const hit of hits) {
    if (hit === null || typeof hit !== 'object') continue;
    const { start, end } = /** @type {Hit} */ (hit);
    if (!Number.isFinite(start) || !Number.isFinite(end) || !(end > start)) continue;
    valid.push({ start, end });
  }
  return valid;
}

/**
 * Whether media time `t` is inside a valid Hit (`start <= t < end`).
 *
 * @param {unknown} hits
 * @param {number} t
 */
export function isInsideHit(hits, t) {
  if (!Number.isFinite(t)) {
    return false;
  }
  return validHits(hits).some((hit) => hit.start <= t && t < hit.end);
}

/**
 * @param {number} audioNow
 * @param {boolean} closedNow
 * @param {GainEvent[]} events
 * @returns {SchedulePlan}
 */
function makePlan(audioNow, closedNow, events) {
  const frozen = Object.freeze(events.map((event) => Object.freeze(event)));
  const key = [closedNow ? 'closed' : 'open', ...frozen.map((e) => `${e.gain ? 'o' : 'c'}${e.time.toFixed(3)}`)].join(
    ' ',
  );
  return Object.freeze({ audioNow, closedNow, events: frozen, key });
}

/**
 * The gain automation for the next `horizon` seconds.
 *
 * @param {ScheduleInput} input
 * @returns {SchedulePlan}
 */
export function planSchedule(input) {
  const hits = validHits(input?.hits);
  const currentTime = input?.currentTime;
  const rate = input?.playbackRate;
  const audioNow = Number.isFinite(input?.audioNow) ? input.audioNow : 0;
  const horizon = Number.isFinite(input?.horizon) && input.horizon > 0 ? input.horizon : HORIZON_SECONDS;
  const offset = Number.isFinite(input?.offsetMs) ? input.offsetMs / 1000 : 0;

  const playing =
    input?.playing === true &&
    Number.isFinite(rate) &&
    rate > 0 &&
    Number.isFinite(currentTime) &&
    Number.isFinite(input?.audioNow);
  if (!playing) {
    return makePlan(audioNow, isInsideHit(hits, currentTime), []);
  }

  // Each Hit relative to now, in audio seconds.
  const spans = hits
    .map((hit) => [(hit.start - currentTime) / rate + offset, (hit.end - currentTime) / rate + offset])
    .sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  /** @type {number[][]} */
  const merged = [];
  for (const [from, to] of spans) {
    const last = merged.at(-1);
    if (last && from - last[1] < MERGE_GAP_SECONDS) {
      last[1] = Math.max(last[1], to);
    } else {
      merged.push([from, to]);
    }
  }

  let closedNow = false;
  /** @type {GainEvent[]} */
  const events = [];
  for (const [from, to] of merged) {
    if (to <= 0) continue;
    if (from > horizon + HORIZON_EPSILON) break;
    if (from <= 0) {
      closedNow = true;
    } else {
      events.push({ time: audioNow + from, gain: 0 });
    }
    events.push({ time: audioNow + to, gain: 1 });
  }
  return makePlan(audioNow, closedNow, events);
}

/**
 * Whether two plans ask for the same automation: the same state now and the
 * same events, each within `tolerance` seconds.
 *
 * @param {SchedulePlan | null | undefined} a
 * @param {SchedulePlan | null | undefined} b
 * @param {number} [tolerance]
 */
export function samePlan(a, b, tolerance = PLAN_TOLERANCE_SECONDS) {
  if (!a || !b) {
    return false;
  }
  if (a.closedNow !== b.closedNow || a.events.length !== b.events.length) {
    return false;
  }
  return a.events.every((event, i) => {
    const other = b.events[i];
    return event.gain === other.gain && Math.abs(event.time - other.time) <= tolerance;
  });
}
