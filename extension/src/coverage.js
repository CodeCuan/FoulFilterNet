// Coverage arithmetic for the Playback Gate: how much of the timeline just
// ahead of the playhead has final Hits. Pure.
//
// Coverage is the service's `coverage`, as api-client.js parses it:
// `[[from, to], ...]` in seconds. The parser already insists on ascending,
// non-overlapping intervals, but these functions do not rely on it: they sort,
// drop what is not an interval, and merge overlapping or touching intervals
// (window shares meet exactly, `[0, 25]` and `[25, 47]` are one run).
//
// Decided (W13):
// - An interval whose `to` is not above its `from` (empty or reversed), or
//   whose ends are not finite numbers, is dropped. Anything that is not an
//   array counts as no coverage.
// - Intervals within TOUCH_TOLERANCE of each other are one run, so float
//   noise on a shared boundary cannot open a gap the gate would stop at.
// - Intervals are closed: a position exactly on `to` is inside the run (with
//   nothing ahead), one exactly on `from` is inside with the whole run ahead.
// - `coveredAhead` treats a negative position as 0 (`currentTime` never is,
//   and a rounding error must not hold a video at its start). `contains` is
//   plain set arithmetic and clamps nothing.
// - Nothing here knows about `state: complete` or the video's duration. A
//   session served from the cache ends its coverage at the last word heard,
//   not at the end of the video (W10), so the gate, not this module, decides
//   that complete means covered to the end.

/** @typedef {[number, number]} Interval */

/** Gaps at most this wide (seconds) between intervals are closed. */
export const TOUCH_TOLERANCE = 1e-6;

/**
 * The coverage as sorted, merged, valid runs. Never mutates its input;
 * always returns a new array of new pairs.
 *
 * @param {unknown} coverage
 * @returns {Interval[]}
 */
export function normaliseCoverage(coverage) {
  if (!Array.isArray(coverage)) {
    return [];
  }
  /** @type {Interval[]} */
  const valid = [];
  for (const pair of coverage) {
    if (!Array.isArray(pair) || pair.length < 2) continue;
    const [from, to] = pair;
    if (!Number.isFinite(from) || !Number.isFinite(to) || !(to > from)) continue;
    valid.push([from, to]);
  }
  valid.sort((a, b) => a[0] - b[0] || a[1] - b[1]);

  /** @type {Interval[]} */
  const runs = [];
  for (const [from, to] of valid) {
    const last = runs.at(-1);
    if (last && from <= last[1] + TOUCH_TOLERANCE) {
      last[1] = Math.max(last[1], to);
    } else {
      runs.push([from, to]);
    }
  }
  return runs;
}

/**
 * The end of the covered run that contains `position`, or null when
 * `position` is not covered. A negative position counts as 0.
 *
 * @param {unknown} coverage
 * @param {number} position  Seconds.
 * @returns {number | null}
 */
export function coveredRunEnd(coverage, position) {
  if (typeof position !== 'number' || Number.isNaN(position)) {
    return null;
  }
  const p = Math.max(0, position);
  for (const [from, to] of normaliseCoverage(coverage)) {
    if (from <= p && p <= to) {
      return to;
    }
  }
  return null;
}

/**
 * Seconds of continuous coverage from `position` onwards: 0 when `position`
 * is not covered (or is at the very end of a run). A negative position counts
 * as 0; a position that is not a number has nothing ahead.
 *
 * @param {unknown} coverage
 * @param {number} position  Seconds.
 * @returns {number}
 */
export function coveredAhead(coverage, position) {
  const end = coveredRunEnd(coverage, position);
  return end === null ? 0 : end - Math.max(0, position);
}

/**
 * Whether all of `[from, to]` lies inside one covered run. A single point
 * (`from === to`) is contained when a run includes it; a reversed or
 * non-numeric span never is.
 *
 * @param {unknown} coverage
 * @param {number} from  Seconds.
 * @param {number} to  Seconds.
 * @returns {boolean}
 */
export function contains(coverage, from, to) {
  if (typeof from !== 'number' || typeof to !== 'number' || Number.isNaN(from) || Number.isNaN(to) || to < from) {
    return false;
  }
  return normaliseCoverage(coverage).some(([start, end]) => start <= from && to <= end);
}
