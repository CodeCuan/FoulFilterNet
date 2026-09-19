// The harness's self-check (W16): did every ground-truth span come out
// silent? Pure. The harness page records the programme output's loudness
// against media time while the video plays (tap.js), and this module compares
// that recording with the fixture manifest's exact spans.
//
// A sample is one reading of four analysers, in dBFS, at one media time:
//   raw        the <video>'s own sound, before any gain (is there speech?)
//   out        everything the page sends to the speakers
//   programme  `out` with the 1 kHz bleep notched away (is the video's sound shut?)
//   tone       `out` through a narrow 1 kHz band-pass (is the bleep sounding?)
//
// Rules (decided):
// - A span is judged on the samples whose media time lies inside its
//   ground-truth [start, end] - the word itself, not the service's padding.
//   Fewer than MIN_SAMPLES there means it was not played (a seek skipped it):
//   NOT PLAYED, which is not a failure.
// - silence: every sample's `out` is below SILENCE_DB.
//   bleep: every sample's `programme` is below SILENCE_DB, and the tone is
//   above TONE_DB in at least MIN_TONE_SHARE of them.
// - Silence where there was speech (`raw` above SPEECH_DB, the checked level
//   below SILENCE_DB) away from every span - more than the service's padding
//   plus EXTRA_TOLERANCE either side - is extra silence: a warning, not a
//   failure (a false positive, or the gate's mute around a hold).
// - The verdict is FAIL if any span failed, NO DATA if there are spans and
//   none was played, else PASS.

/** Below this the checked output counts as silent (dBFS). */
export const SILENCE_DB = -50;

/** Above this the video's own sound counts as speech (dBFS). */
export const SPEECH_DB = -40;

/** Above this the 1 kHz band counts as a bleep (dBFS). BLEEP_LEVEL 0.125 is about -21 dBFS RMS. */
export const TONE_DB = -35;

/** A span needs this many samples inside it to count as played. */
export const MIN_SAMPLES = 3;

/** In bleep mode, the share of a span's samples that must carry the tone. */
export const MIN_TONE_SHARE = 0.8;

/** The service's Hit padding (HitPadding.Default): 0.15 s before, 0.25 s after. */
export const PAD_BEFORE = 0.15;
export const PAD_AFTER = 0.25;

/** Extra room around a padded span before silence counts as extra. */
export const EXTRA_TOLERANCE = 0.15;

/** Samples further apart than this (media seconds) start a new run of extra silence. */
export const RUN_GAP = 0.12;

/** The quietest level reported, for digital silence (dBFS). */
export const FLOOR_DB = -120;

/** How far before a span the onset search starts (media seconds). */
export const ONSET_LOOKBACK = 0.3;

/**
 * @typedef {object} Sample
 * @property {number} t  Media seconds.
 * @property {number} raw
 * @property {number} out
 * @property {number} programme
 * @property {number} tone
 *
 * @typedef {object} Span  A ground-truth word from the manifest.
 * @property {string} phrase
 * @property {number} start
 * @property {number} end
 *
 * @typedef {'PASS' | 'FAIL' | 'NOT PLAYED'} SpanVerdict
 *
 * @typedef {object} SpanResult
 * @property {string} phrase
 * @property {number} start
 * @property {number} end
 * @property {SpanVerdict} verdict
 * @property {string} reason
 * @property {number} samples
 * @property {number | null} loudestDb  The loudest checked level inside the span.
 * @property {number | null} loudestRawDb  The loudest raw level inside the span.
 * @property {number | null} toneShare  Bleep only: the share of samples carrying the tone.
 * @property {number | null} rawOnset  Media time of the first speech at or after `start - ONSET_LOOKBACK`, if any.
 * @property {Array<{ t: number, db: number }>} leaks  Samples that were not silent.
 *
 * @typedef {object} Run
 * @property {number} from
 * @property {number} to
 * @property {number} samples
 *
 * @typedef {object} Evaluation
 * @property {'PASS' | 'FAIL' | 'NO DATA'} verdict
 * @property {boolean} pass
 * @property {'silence' | 'bleep'} method
 * @property {SpanResult[]} spans
 * @property {Run[]} extraSilence
 * @property {string[]} warnings
 * @property {number} sampleCount
 */

/**
 * RMS level of a block of samples, in dBFS, floored at FLOOR_DB.
 *
 * @param {ArrayLike<number>} block
 * @returns {number}
 */
export function rmsDb(block) {
  if (!block || block.length === 0) return FLOOR_DB;
  let sum = 0;
  for (let i = 0; i < block.length; i += 1) {
    const v = Number(block[i]);
    if (Number.isFinite(v)) sum += v * v;
  }
  const rms = Math.sqrt(sum / block.length);
  if (!(rms > 0)) return FLOOR_DB;
  return Math.max(FLOOR_DB, 20 * Math.log10(rms));
}

/**
 * The media time an analyser block describes: the middle of the last
 * `windowSeconds` of audio, which at `rate` covers `windowSeconds * rate` of
 * media before `currentTime`.
 *
 * @param {number} currentTime
 * @param {number} windowSeconds
 * @param {number} [rate]
 * @returns {number}
 */
export function sampleTime(currentTime, windowSeconds, rate = 1) {
  const r = Number.isFinite(rate) && rate > 0 ? rate : 1;
  const w = Number.isFinite(windowSeconds) && windowSeconds > 0 ? windowSeconds : 0;
  return currentTime - (w * r) / 2;
}

/**
 * Read the manifest (generate-fixtures.ps1 writes it with a BOM) and find one
 * fixture's spans.
 *
 * @param {string | object} manifest  JSON text or parsed.
 * @param {string} file
 * @returns {{ file: string, duration: number | null, spans: Span[] } | null}
 */
export function findFixture(manifest, file) {
  let parsed = manifest;
  if (typeof manifest === 'string') {
    try {
      parsed = JSON.parse(manifest.replace(/^\uFEFF/, ''));
    } catch {
      return null;
    }
  }
  const fixtures = /** @type {any} */ (parsed)?.fixtures;
  if (!Array.isArray(fixtures)) return null;
  const fixture = fixtures.find((f) => f && f.file === file);
  if (!fixture) return null;
  const spans = (Array.isArray(fixture.hits) ? fixture.hits : [])
    .filter((h) => h && Number.isFinite(h.start) && Number.isFinite(h.end) && h.end > h.start)
    .map((h) => Object.freeze({ phrase: String(h.phrase ?? ''), start: h.start, end: h.end }))
    .sort((a, b) => a.start - b.start);
  return Object.freeze({
    file,
    duration: Number.isFinite(fixture.duration) ? fixture.duration : null,
    spans: Object.freeze(spans),
  });
}

/** @param {unknown} s @returns {s is Sample} */
function isSample(s) {
  const x = /** @type {any} */ (s);
  return (
    x !== null &&
    typeof x === 'object' &&
    Number.isFinite(x.t) &&
    Number.isFinite(x.raw) &&
    Number.isFinite(x.out) &&
    Number.isFinite(x.programme) &&
    Number.isFinite(x.tone)
  );
}

/**
 * @param {'silence' | 'bleep'} method
 * @param {Sample} sample
 * @returns {number}  The level that must be silent.
 */
function checkedLevel(method, sample) {
  return method === 'bleep' ? sample.programme : sample.out;
}

/** @param {number} x */
const round3 = (x) => Math.round(x * 1000) / 1000;

/**
 * Judge a recording against the ground truth.
 *
 * @param {object} input
 * @param {unknown[]} input.samples
 * @param {Span[]} input.spans
 * @param {string} [input.method]  'bleep', else silence.
 * @returns {Evaluation}
 */
export function evaluate({ samples, spans, method: requested }) {
  /** @type {'silence' | 'bleep'} */
  const method = requested === 'bleep' ? 'bleep' : 'silence';
  const valid = (Array.isArray(samples) ? samples : []).filter(isSample).sort((a, b) => a.t - b.t);
  const truth = (Array.isArray(spans) ? spans : []).filter(
    (s) => s && Number.isFinite(s.start) && Number.isFinite(s.end) && s.end > s.start,
  );
  /** @type {string[]} */
  const warnings = [];

  const results = truth.map((span) => {
    const inside = valid.filter((s) => s.t >= span.start && s.t <= span.end);
    const onsetSample = valid.find((s) => s.t >= span.start - ONSET_LOOKBACK && s.t <= span.end && s.raw > SPEECH_DB);
    const base = {
      phrase: span.phrase,
      start: span.start,
      end: span.end,
      samples: inside.length,
      rawOnset: onsetSample ? round3(onsetSample.t) : null,
    };
    if (inside.length < MIN_SAMPLES) {
      return Object.freeze({
        ...base,
        verdict: /** @type {SpanVerdict} */ ('NOT PLAYED'),
        reason: `only ${inside.length} sample(s) inside the span`,
        loudestDb: null,
        loudestRawDb: null,
        toneShare: null,
        leaks: Object.freeze([]),
      });
    }

    const loudestDb = Math.max(...inside.map((s) => checkedLevel(method, s)));
    const loudestRawDb = Math.max(...inside.map((s) => s.raw));
    const leaks = inside
      .filter((s) => checkedLevel(method, s) >= SILENCE_DB)
      .map((s) => Object.freeze({ t: round3(s.t), db: Math.round(checkedLevel(method, s) * 10) / 10 }));
    const toneShare = method === 'bleep' ? inside.filter((s) => s.tone > TONE_DB).length / inside.length : null;

    /** @type {SpanVerdict} */
    let verdict = 'PASS';
    let reason = 'silent throughout';
    if (leaks.length > 0) {
      verdict = 'FAIL';
      reason = `${leaks.length} of ${inside.length} samples audible (loudest ${loudestDb.toFixed(1)} dBFS)`;
    } else if (method === 'bleep' && /** @type {number} */ (toneShare) < MIN_TONE_SHARE) {
      verdict = 'FAIL';
      reason = `bleep heard in only ${Math.round(/** @type {number} */ (toneShare) * 100)}% of samples`;
    } else if (method === 'bleep') {
      reason = 'programme silent, bleep sounding';
    }
    if (verdict === 'PASS' && loudestRawDb <= SPEECH_DB) {
      warnings.push(
        `"${span.phrase}" at ${span.start}s: no speech measured in the source there, so its silence proves little.`,
      );
    }
    return Object.freeze({ ...base, verdict, reason, loudestDb, loudestRawDb, toneShare, leaks: Object.freeze(leaks) });
  });

  const extraSilence = findExtraSilence(valid, truth, method);
  for (const run of extraSilence) {
    warnings.push(`Extra silence at ${run.from.toFixed(2)}–${run.to.toFixed(2)}s (${run.samples} samples) away from every span.`);
  }

  const played = results.filter((r) => r.verdict !== 'NOT PLAYED');
  const failed = results.some((r) => r.verdict === 'FAIL');
  const verdict = failed ? 'FAIL' : truth.length > 0 && played.length === 0 ? 'NO DATA' : 'PASS';

  return Object.freeze({
    verdict,
    pass: verdict === 'PASS',
    method,
    spans: Object.freeze(results),
    extraSilence: Object.freeze(extraSilence),
    warnings: Object.freeze(warnings),
    sampleCount: valid.length,
  });
}

/**
 * Runs of silenced speech outside every padded span.
 *
 * @param {Sample[]} sorted
 * @param {Span[]} spans
 * @param {'silence' | 'bleep'} method
 * @returns {Run[]}
 */
function findExtraSilence(sorted, spans, method) {
  const allowed = spans.map((s) => [s.start - PAD_BEFORE - EXTRA_TOLERANCE, s.end + PAD_AFTER + EXTRA_TOLERANCE]);
  const isExtra = (/** @type {Sample} */ s) =>
    s.raw > SPEECH_DB && checkedLevel(method, s) < SILENCE_DB && !allowed.some(([a, b]) => s.t >= a && s.t <= b);

  /** @type {Run[]} */
  const runs = [];
  /** @type {{ from: number, to: number, samples: number } | null} */
  let current = null;
  for (const s of sorted) {
    if (!isExtra(s)) continue;
    if (current !== null && s.t - current.to <= RUN_GAP) {
      current.to = s.t;
      current.samples += 1;
    } else {
      if (current !== null) runs.push(current);
      current = { from: s.t, to: s.t, samples: 1 };
    }
  }
  if (current !== null) runs.push(current);
  return runs.map((r) => Object.freeze({ from: round3(r.from), to: round3(r.to), samples: r.samples }));
}

/**
 * The evaluation as lines for the page and the console: one PASS/FAIL line
 * per span, then the warnings, then the verdict.
 *
 * @param {Evaluation} evaluation
 * @param {string} [title]
 * @returns {string[]}
 */
export function formatReport(evaluation, title = 'FoulFilter harness') {
  const lines = [`${title}: ${evaluation.method}, ${evaluation.sampleCount} samples`];
  if (evaluation.spans.length === 0) lines.push('  (no ground-truth spans for this file)');
  for (const s of evaluation.spans) {
    const loud = s.loudestDb === null ? '' : `, loudest ${s.loudestDb.toFixed(1)} dBFS`;
    const onset = s.rawOnset === null ? '' : `, speech from ${s.rawOnset.toFixed(3)}s`;
    lines.push(
      `  ${s.verdict.padEnd(10)} "${s.phrase}" ${s.start.toFixed(3)}–${s.end.toFixed(3)}s: ${s.reason} (${s.samples} samples${loud}${onset})`,
    );
  }
  for (const w of evaluation.warnings) lines.push(`  WARNING ${w}`);
  lines.push(`${evaluation.verdict}`);
  return lines;
}
