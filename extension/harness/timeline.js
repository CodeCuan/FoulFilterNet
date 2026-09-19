// What happened during one harness run (W16), for the report: the gate's
// holds and releases, the Watch Session's states, the badge, and how long the
// viewer waited before the video first played. Pure: the harness page notes
// entries; `summarise` reads them.

/**
 * @typedef {object} Entry
 * @property {number} at  Milliseconds since the run started.
 * @property {number | null} media  The media time then, if known.
 * @property {string} kind  'gate', 'state', 'badge', 'play', 'pause', 'seek', 'rate', 'ended', 'note'.
 * @property {string} value
 *
 * @typedef {object} Summary
 * @property {number} holds  Times the gate began a hold.
 * @property {number} releases  Times it released one.
 * @property {number | null} firstPlayMs  From the first play press to the gate's first release after a hold (or to
 *   the first `playing`, when it never held): how long the viewer waited.
 * @property {number | null} heldMs  Total time spent in holds.
 * @property {string[]} states  The Watch Session's states, in order, without repeats.
 * @property {string[]} badges  The badge texts, in order, without repeats.
 * @property {string[]} gateReasons  The hold reasons seen, in order, without repeats.
 */

/**
 * @param {() => number} now  Milliseconds.
 */
export function createTimeline(now) {
  const started = now();
  /** @type {Entry[]} */
  const entries = [];
  return {
    entries,
    /**
     * @param {string} kind
     * @param {string} value
     * @param {number | null} [media]
     */
    note(kind, value, media = null) {
      const last = entries.findLast((e) => e.kind === kind);
      if (last && last.value === value && (kind === 'gate' || kind === 'state' || kind === 'badge')) return;
      entries.push(Object.freeze({ at: Math.round(now() - started), media: Number.isFinite(media) ? media : null, kind, value }));
    },
  };
}

/** @param {string[]} values */
function withoutRepeats(values) {
  return values.filter((v, i) => i === 0 || v !== values[i - 1]);
}

/**
 * @param {Entry[]} entries
 * @returns {Summary}
 */
export function summarise(entries) {
  const list = Array.isArray(entries) ? entries : [];
  const gate = list.filter((e) => e.kind === 'gate');
  let holds = 0;
  let releases = 0;
  let heldMs = 0;
  /** @type {number | null} */
  let holdStart = null;
  for (const e of gate) {
    const holding = e.value.startsWith('hold');
    if (holding && holdStart === null) {
      holds += 1;
      holdStart = e.at;
    } else if (!holding && holdStart !== null) {
      releases += 1;
      heldMs += e.at - holdStart;
      holdStart = null;
    }
  }
  if (holdStart !== null && list.length > 0) heldMs += list[list.length - 1].at - holdStart;

  // The wait: from the first play press to the gate's first release after
  // it, when it was holding at the press or held after it; otherwise to the
  // first 'playing' after the press.
  const firstPress = list.find((e) => e.kind === 'play');
  const isHold = (/** @type {Entry} */ e) => e.value.startsWith('hold');
  const holdingAtPress = firstPress ? gate.findLast((e) => e.at <= firstPress.at)?.value.startsWith('hold') === true : false;
  const heldAfterPress = firstPress ? holdingAtPress || gate.some((e) => e.at >= firstPress.at && isHold(e)) : false;
  const firstPlaying = !firstPress
    ? undefined
    : heldAfterPress
      ? gate.find((e) => e.at >= firstPress.at && !isHold(e))
      : list.find((e) => e.kind === 'playing' && e.at >= firstPress.at);

  return Object.freeze({
    holds,
    releases,
    firstPlayMs: firstPress && firstPlaying ? firstPlaying.at - firstPress.at : null,
    heldMs: gate.length > 0 ? heldMs : null,
    states: withoutRepeats(list.filter((e) => e.kind === 'state').map((e) => e.value)),
    badges: withoutRepeats(list.filter((e) => e.kind === 'badge').map((e) => e.value)),
    gateReasons: withoutRepeats(
      gate.filter((e) => e.value.startsWith('hold')).map((e) => e.value.replace(/^hold:?\s*/, '')),
    ),
  });
}
