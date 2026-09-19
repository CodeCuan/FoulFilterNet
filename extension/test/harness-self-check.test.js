// The harness's self-check (W16): recording → PASS/FAIL per ground-truth span.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import {
  EXTRA_TOLERANCE,
  FLOOR_DB,
  MIN_SAMPLES,
  MIN_TONE_SHARE,
  PAD_AFTER,
  PAD_BEFORE,
  SILENCE_DB,
  SPEECH_DB,
  TONE_DB,
  evaluate,
  findFixture,
  formatReport,
  rmsDb,
  sampleTime,
} from '../harness/self-check.js';

const LOUD = -20;
const QUIET = -90;
const DAMN = Object.freeze({ phrase: 'damn', start: 2.941, end: 3.428 });

/** A sample at `t` with the given levels (defaults: speech in, speech out, no tone). */
function sample(t, levels = {}) {
  return { t, raw: LOUD, out: LOUD, programme: LOUD, tone: QUIET, ...levels };
}

/** Samples every `step` seconds from `from` to `to` inclusive. */
function every(from, to, step, levels = {}) {
  const out = [];
  for (let t = from; t <= to + 1e-9; t += step) out.push(sample(Math.round(t * 1000) / 1000, levels));
  return out;
}

/** A well-censored silence recording of the sample_video fixture. */
function censored(levels = { out: QUIET, programme: QUIET }) {
  return [
    ...every(0, 2.7, 0.02),
    ...every(2.8, 3.66, 0.02, levels),
    ...every(3.7, 5.6, 0.02),
  ];
}

describe('rmsDb', () => {
  it('is the floor for an empty block', () => {
    assert.equal(rmsDb([]), FLOOR_DB);
  });

  it('is the floor for a missing block', () => {
    assert.equal(rmsDb(undefined), FLOOR_DB);
  });

  it('is the floor for digital silence', () => {
    assert.equal(rmsDb(new Float32Array(512)), FLOOR_DB);
  });

  it('is 0 dBFS for a full-scale square wave', () => {
    assert.equal(rmsDb([1, -1, 1, -1]), 0);
  });

  it('is about -3 dBFS for a full-scale sine', () => {
    const block = Array.from({ length: 4800 }, (_, i) => Math.sin((2 * Math.PI * 1000 * i) / 48000));
    assert.ok(Math.abs(rmsDb(block) - -3.0103) < 0.01);
  });

  it('is about -21 dBFS for the bleep level', () => {
    const block = Array.from({ length: 4800 }, (_, i) => 0.125 * Math.sin((2 * Math.PI * 1000 * i) / 48000));
    assert.ok(Math.abs(rmsDb(block) - -21.07) < 0.05);
  });

  it('never goes below the floor', () => {
    assert.equal(rmsDb([1e-12, -1e-12]), FLOOR_DB);
  });

  it('ignores non-finite values', () => {
    assert.equal(rmsDb([NaN, 1, -1, Infinity]), rmsDb([0, 1, -1, 0]));
  });
});

describe('sampleTime', () => {
  it('is the middle of the analyser window at normal speed', () => {
    assert.equal(sampleTime(10, 0.02), 9.99);
  });

  it('covers twice the media at 2x', () => {
    assert.equal(sampleTime(10, 0.02, 2), 9.98);
  });

  it('treats a bad rate as 1', () => {
    assert.equal(sampleTime(10, 0.02, 0), 9.99);
    assert.equal(sampleTime(10, 0.02, NaN), 9.99);
  });

  it('treats a bad window as none', () => {
    assert.equal(sampleTime(10, -1), 10);
    assert.equal(sampleTime(10, NaN), 10);
  });
});

describe('findFixture', () => {
  const manifest = {
    fixtures: [
      { file: 'sample_video.mp4', kind: 'video', duration: 5.649, hits: [{ phrase: 'damn', start: 2.941, end: 3.428 }] },
      {
        file: 'repeated_hits.mp3',
        duration: 11.016,
        hits: [
          { phrase: 'damn', start: 4.651, end: 5.138 },
          { phrase: 'damn', start: 0, end: 0.487 },
        ],
      },
      { file: 'clean_speech.mp3', duration: 6.732, hits: [] },
      { file: 'broken.mp3', hits: [{ phrase: 'x', start: 2, end: 1 }, null, { phrase: 'y', start: 'a', end: 2 }] },
    ],
  };

  it('finds a fixture by file name', () => {
    assert.deepEqual(findFixture(manifest, 'sample_video.mp4'), {
      file: 'sample_video.mp4',
      duration: 5.649,
      spans: [{ phrase: 'damn', start: 2.941, end: 3.428 }],
    });
  });

  it('sorts the spans by start', () => {
    assert.deepEqual(
      findFixture(manifest, 'repeated_hits.mp3').spans.map((s) => s.start),
      [0, 4.651],
    );
  });

  it('gives no spans for a clean fixture', () => {
    assert.deepEqual(findFixture(manifest, 'clean_speech.mp3').spans, []);
  });

  it('drops malformed spans', () => {
    assert.deepEqual(findFixture(manifest, 'broken.mp3').spans, []);
  });

  it('gives a null duration when there is none', () => {
    assert.equal(findFixture(manifest, 'broken.mp3').duration, null);
  });

  it('is null for a file not in the manifest', () => {
    assert.equal(findFixture(manifest, 'other.mp4'), null);
  });

  it('reads JSON text with a byte order mark, as generate-fixtures.ps1 writes it', () => {
    const text = '\uFEFF' + JSON.stringify(manifest);
    assert.equal(findFixture(text, 'sample_video.mp4').spans.length, 1);
  });

  it('is null for text that is not JSON', () => {
    assert.equal(findFixture('not json', 'sample_video.mp4'), null);
  });

  it('is null for a manifest without fixtures', () => {
    assert.equal(findFixture({}, 'sample_video.mp4'), null);
    assert.equal(findFixture(null, 'sample_video.mp4'), null);
  });

  it('matches the file name exactly', () => {
    assert.equal(findFixture(manifest, 'SAMPLE_VIDEO.MP4'), null);
  });
});

describe('evaluate: silence, censored', () => {
  const result = evaluate({ samples: censored(), spans: [DAMN], method: 'silence' });

  it('passes', () => {
    assert.equal(result.verdict, 'PASS');
    assert.equal(result.pass, true);
  });

  it('passes the span', () => {
    assert.equal(result.spans[0].verdict, 'PASS');
  });

  it('counts the samples inside the span only', () => {
    assert.equal(result.spans[0].samples, 24);
  });

  it('reports the loudest level inside', () => {
    assert.equal(result.spans[0].loudestDb, QUIET);
  });

  it('reports the loudest raw level inside', () => {
    assert.equal(result.spans[0].loudestRawDb, LOUD);
  });

  it('has no leaks', () => {
    assert.deepEqual(result.spans[0].leaks, []);
  });

  it('has no tone share in silence mode', () => {
    assert.equal(result.spans[0].toneShare, null);
  });

  it('reports no extra silence', () => {
    assert.deepEqual(result.extraSilence, []);
  });

  it('has no warnings', () => {
    assert.deepEqual(result.warnings, []);
  });

  it('says which method it judged', () => {
    assert.equal(result.method, 'silence');
  });

  it('counts every sample', () => {
    assert.equal(result.sampleCount, censored().length);
  });

  it('keeps the phrase and times', () => {
    assert.equal(result.spans[0].phrase, 'damn');
    assert.equal(result.spans[0].start, 2.941);
    assert.equal(result.spans[0].end, 3.428);
  });

  it('finds where the speech starts', () => {
    assert.equal(result.spans[0].rawOnset, 2.66);
  });

  it('is frozen', () => {
    assert.ok(Object.isFrozen(result));
    assert.ok(Object.isFrozen(result.spans[0]));
  });
});

describe('evaluate: silence, one leak', () => {
  const samples = censored().map((s) => (s.t === 3.2 ? { ...s, out: -30 } : s));
  const result = evaluate({ samples, spans: [DAMN], method: 'silence' });

  it('fails', () => {
    assert.equal(result.verdict, 'FAIL');
    assert.equal(result.pass, false);
  });

  it('fails the span', () => {
    assert.equal(result.spans[0].verdict, 'FAIL');
  });

  it('lists the leak', () => {
    assert.deepEqual(result.spans[0].leaks, [{ t: 3.2, db: -30 }]);
  });

  it('says how many samples were audible', () => {
    assert.match(result.spans[0].reason, /1 of 24 samples audible/);
  });

  it('reports the loudest level', () => {
    assert.equal(result.spans[0].loudestDb, -30);
  });
});

describe('evaluate: exactly at the silence threshold', () => {
  it('counts the threshold itself as audible', () => {
    const samples = censored().map((s) => (s.t === 3.2 ? { ...s, out: SILENCE_DB } : s));
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'FAIL');
  });

  it('counts just below it as silent', () => {
    const samples = censored().map((s) => (s.t === 3.2 ? { ...s, out: SILENCE_DB - 0.1 } : s));
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'PASS');
  });
});

describe('evaluate: nothing censored at all', () => {
  const result = evaluate({ samples: every(0, 5.6, 0.02), spans: [DAMN], method: 'silence' });

  it('fails', () => {
    assert.equal(result.verdict, 'FAIL');
  });

  it('lists every sample in the span as a leak', () => {
    assert.equal(result.spans[0].leaks.length, result.spans[0].samples);
  });
});

describe('evaluate: span edges', () => {
  it('includes a sample exactly at the start', () => {
    const samples = [...censored().filter((s) => s.t !== 2.94), sample(2.941, { out: -30 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'FAIL');
  });

  it('includes a sample exactly at the end', () => {
    const samples = [...censored(), sample(3.428, { out: -30 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'FAIL');
  });

  it('ignores a loud sample just before the start', () => {
    const samples = [...censored(), sample(2.94, { out: -30 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'PASS');
  });

  it('ignores a loud sample just after the end', () => {
    const samples = [...censored(), sample(3.429, { out: -30 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'PASS');
  });
});

describe('evaluate: the analyser window straddles a span edge', () => {
  // Found running the harness (W16): at 2x each analyser block covers about
  // 42 ms of media, so a sample stamped just inside the span's end also heard
  // the audio after it, and a Hit ending 2 ms after the word failed.
  const half = 0.021;
  const straddling = (t, levels) => ({ ...sample(t, levels), w: 2 * half });

  it('does not judge a sample whose block reaches past the end', () => {
    const samples = [...censored(), straddling(3.42, { out: -45 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'PASS');
  });

  it('does not judge a sample whose block starts before the start', () => {
    const samples = [...censored(), straddling(2.95, { out: -45 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'PASS');
  });

  it('judges a sample whose block lies wholly inside', () => {
    const samples = [...censored(), straddling(3.2, { out: -45 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'FAIL');
  });

  it('judges a block that ends exactly at the end', () => {
    const samples = [...censored(), straddling(DAMN.end - half, { out: -45 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'FAIL');
  });

  it('treats a sample with no width as a point', () => {
    const samples = [...censored(), sample(3.42, { out: -45 })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'FAIL');
  });

  it('treats a bad width as a point', () => {
    const samples = [...censored(), { ...sample(3.42, { out: -45 }), w: NaN }];
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'FAIL');
  });

  it('does not count the straddling samples', () => {
    const samples = [...censored(), straddling(3.42, { out: QUIET })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).spans[0].samples, 24);
  });
});

describe('evaluate: a span that was not played', () => {
  const samples = [...every(0, 1, 0.02), ...every(4, 5.6, 0.02)];
  const result = evaluate({ samples, spans: [DAMN] });

  it('marks it NOT PLAYED', () => {
    assert.equal(result.spans[0].verdict, 'NOT PLAYED');
  });

  it('says how many samples there were', () => {
    assert.match(result.spans[0].reason, /only 0 sample/);
  });

  it('is NO DATA when no span was played', () => {
    assert.equal(result.verdict, 'NO DATA');
    assert.equal(result.pass, false);
  });

  it('has no loudest level', () => {
    assert.equal(result.spans[0].loudestDb, null);
  });

  it(`needs ${MIN_SAMPLES} samples to count as played`, () => {
    const few = [sample(3.0, { out: QUIET }), sample(3.1, { out: QUIET })];
    assert.equal(evaluate({ samples: few, spans: [DAMN] }).spans[0].verdict, 'NOT PLAYED');
    const enough = [...few, sample(3.2, { out: QUIET })];
    assert.equal(evaluate({ samples: enough, spans: [DAMN] }).spans[0].verdict, 'PASS');
  });
});

describe('evaluate: a seek skipped one span of two', () => {
  const second = { phrase: 'damn', start: 6.79, end: 7.277 };
  const samples = [...every(0, 1, 0.02), ...every(6, 8, 0.02).map((s) => (s.t >= 6.64 && s.t <= 7.52 ? { ...s, out: QUIET } : s))];
  const result = evaluate({ samples, spans: [DAMN, second] });

  it('passes overall', () => {
    assert.equal(result.verdict, 'PASS');
  });

  it('marks the skipped one NOT PLAYED', () => {
    assert.equal(result.spans[0].verdict, 'NOT PLAYED');
  });

  it('passes the played one', () => {
    assert.equal(result.spans[1].verdict, 'PASS');
  });
});

describe('evaluate: samples out of order (a seek back)', () => {
  const samples = censored().reverse();

  it('judges them by media time, not arrival', () => {
    assert.equal(evaluate({ samples, spans: [DAMN] }).verdict, 'PASS');
  });
});

describe('evaluate: bleep', () => {
  const bleeped = censored({ out: -21, programme: QUIET, tone: -24 });

  it('passes a silent programme with the tone sounding', () => {
    const result = evaluate({ samples: bleeped, spans: [DAMN], method: 'bleep' });
    assert.equal(result.verdict, 'PASS');
    assert.equal(result.spans[0].reason, 'programme silent, bleep sounding');
  });

  it('reports the tone share', () => {
    assert.equal(evaluate({ samples: bleeped, spans: [DAMN], method: 'bleep' }).spans[0].toneShare, 1);
  });

  it('judges the programme, not the whole output', () => {
    assert.equal(evaluate({ samples: bleeped, spans: [DAMN], method: 'bleep' }).spans[0].loudestDb, QUIET);
  });

  it('fails when the programme is audible under the bleep', () => {
    const samples = bleeped.map((s) => (s.t === 3.2 ? { ...s, programme: -30 } : s));
    assert.equal(evaluate({ samples, spans: [DAMN], method: 'bleep' }).verdict, 'FAIL');
  });

  it('fails when there is no bleep', () => {
    const samples = censored({ out: QUIET, programme: QUIET, tone: QUIET });
    const result = evaluate({ samples, spans: [DAMN], method: 'bleep' });
    assert.equal(result.verdict, 'FAIL');
    assert.match(result.spans[0].reason, /bleep heard in only 0%/);
  });

  it(`needs the tone in ${MIN_TONE_SHARE * 100}% of the span`, () => {
    const inside = bleeped.filter((s) => s.t >= DAMN.start && s.t <= DAMN.end);
    const quietCount = Math.ceil(inside.length * (1 - MIN_TONE_SHARE)) + 1;
    const quietTimes = new Set(inside.slice(0, quietCount).map((s) => s.t));
    const samples = bleeped.map((s) => (quietTimes.has(s.t) ? { ...s, tone: QUIET } : s));
    assert.equal(evaluate({ samples, spans: [DAMN], method: 'bleep' }).verdict, 'FAIL');
  });

  it(`counts a tone just above ${TONE_DB} dBFS`, () => {
    const samples = censored({ out: -21, programme: QUIET, tone: TONE_DB + 0.1 });
    assert.equal(evaluate({ samples, spans: [DAMN], method: 'bleep' }).verdict, 'PASS');
  });

  it('fails a bleep recording judged as silence (the tone is audible)', () => {
    assert.equal(evaluate({ samples: bleeped, spans: [DAMN], method: 'silence' }).verdict, 'FAIL');
  });

  it('treats an unknown method as silence', () => {
    assert.equal(evaluate({ samples: censored(), spans: [DAMN], method: 'remove' }).method, 'silence');
  });
});

describe('evaluate: extra silence', () => {
  const samples = censored().map((s) => (s.t >= 1.0 && s.t <= 1.4 ? { ...s, out: QUIET, programme: QUIET } : s));
  const result = evaluate({ samples, spans: [DAMN] });

  it('still passes', () => {
    assert.equal(result.verdict, 'PASS');
  });

  it('reports the run', () => {
    assert.deepEqual(result.extraSilence, [{ from: 1, to: 1.4, samples: 21 }]);
  });

  it('warns about it', () => {
    assert.ok(result.warnings.some((w) => w.startsWith('Extra silence at 1.00–1.40s')));
  });
});

describe('evaluate: silence around a span', () => {
  it('allows the padding and tolerance before the span', () => {
    const from = DAMN.start - PAD_BEFORE - EXTRA_TOLERANCE;
    const samples = [...censored(), sample(from + 0.001, { out: QUIET })];
    assert.deepEqual(evaluate({ samples, spans: [DAMN] }).extraSilence, []);
  });

  it('allows the padding and tolerance after the span', () => {
    const to = DAMN.end + PAD_AFTER + EXTRA_TOLERANCE;
    const samples = [...censored(), sample(to - 0.001, { out: QUIET })];
    assert.deepEqual(evaluate({ samples, spans: [DAMN] }).extraSilence, []);
  });

  it('reports silence just beyond them', () => {
    const to = DAMN.end + PAD_AFTER + EXTRA_TOLERANCE;
    const samples = [...censored(), sample(to + 0.01, { out: QUIET })];
    assert.equal(evaluate({ samples, spans: [DAMN] }).extraSilence.length, 1);
  });

  it('does not count a natural pause (no speech in the source) as extra', () => {
    const samples = censored().map((s) => (s.t >= 1.0 && s.t <= 1.4 ? { ...s, raw: QUIET, out: QUIET } : s));
    assert.deepEqual(evaluate({ samples, spans: [DAMN] }).extraSilence, []);
  });

  it(`needs speech above ${SPEECH_DB} dBFS in the source`, () => {
    const samples = censored().map((s) => (s.t >= 1.0 && s.t <= 1.4 ? { ...s, raw: SPEECH_DB, out: QUIET } : s));
    assert.deepEqual(evaluate({ samples, spans: [DAMN] }).extraSilence, []);
  });

  it('splits runs separated by a gap', () => {
    const samples = censored().map((s) =>
      (s.t >= 0.5 && s.t <= 0.6) || (s.t >= 1.5 && s.t <= 1.6) ? { ...s, out: QUIET } : s,
    );
    assert.equal(evaluate({ samples, spans: [DAMN] }).extraSilence.length, 2);
  });

  it('judges the programme in bleep mode', () => {
    const samples = censored({ out: -21, programme: QUIET, tone: -24 }).map((s) =>
      s.t >= 1.0 && s.t <= 1.2 ? { ...s, out: -21, programme: QUIET, tone: -24 } : s,
    );
    assert.equal(evaluate({ samples, spans: [DAMN], method: 'bleep' }).extraSilence.length, 1);
  });
});

describe('evaluate: no speech in a span', () => {
  const samples = censored().map((s) => (s.t >= 2.6 && s.t <= 3.7 ? { ...s, raw: QUIET } : s));
  const result = evaluate({ samples, spans: [DAMN] });

  it('still passes', () => {
    assert.equal(result.spans[0].verdict, 'PASS');
  });

  it('warns that it proves little', () => {
    assert.ok(result.warnings.some((w) => w.includes('no speech measured')));
  });

  it('finds no onset', () => {
    assert.equal(result.spans[0].rawOnset, null);
  });
});

describe('evaluate: odd input', () => {
  it('passes a file with no spans', () => {
    const result = evaluate({ samples: every(0, 1, 0.02), spans: [] });
    assert.equal(result.verdict, 'PASS');
    assert.deepEqual(result.spans, []);
  });

  it('ignores malformed samples', () => {
    const samples = [...censored(), null, { t: 3.2 }, { t: NaN, raw: 0, out: 0, programme: 0, tone: 0 }, 'x'];
    const result = evaluate({ samples, spans: [DAMN] });
    assert.equal(result.verdict, 'PASS');
    assert.equal(result.sampleCount, censored().length);
  });

  it('ignores malformed spans', () => {
    const result = evaluate({ samples: censored(), spans: [DAMN, null, { phrase: 'x', start: 3, end: 2 }] });
    assert.equal(result.spans.length, 1);
  });

  it('copes with no samples at all', () => {
    const result = evaluate({ samples: undefined, spans: [DAMN] });
    assert.equal(result.verdict, 'NO DATA');
    assert.equal(result.sampleCount, 0);
  });

  it('copes with no spans at all', () => {
    assert.equal(evaluate({ samples: censored(), spans: undefined }).verdict, 'PASS');
  });
});

describe('formatReport', () => {
  const lines = formatReport(evaluate({ samples: censored(), spans: [DAMN] }), 'sample_video.mp4');

  it('starts with the title, method and sample count', () => {
    assert.equal(lines[0], `sample_video.mp4: silence, ${censored().length} samples`);
  });

  it('has a PASS line for the span', () => {
    assert.match(lines[1], /^ {2}PASS\s+"damn" 2\.941–3\.428s: silent throughout \(24 samples, loudest -90\.0 dBFS, speech from 2\.660s\)$/);
  });

  it('ends with the verdict', () => {
    assert.equal(lines.at(-1), 'PASS');
  });

  it('has a FAIL line for a leaking span', () => {
    const samples = censored().map((s) => (s.t === 3.2 ? { ...s, out: -30 } : s));
    const failed = formatReport(evaluate({ samples, spans: [DAMN] }));
    assert.match(failed[1], /^ {2}FAIL/);
    assert.equal(failed.at(-1), 'FAIL');
  });

  it('lists the warnings', () => {
    const samples = censored().map((s) => (s.t >= 1.0 && s.t <= 1.4 ? { ...s, out: QUIET } : s));
    assert.ok(formatReport(evaluate({ samples, spans: [DAMN] })).some((l) => l.startsWith('  WARNING Extra silence')));
  });

  it('says when there is no ground truth', () => {
    assert.ok(formatReport(evaluate({ samples: censored(), spans: [] })).includes('  (no ground-truth spans for this file)'));
  });

  it('has a NOT PLAYED line', () => {
    const report = formatReport(evaluate({ samples: every(0, 1, 0.02), spans: [DAMN] }));
    assert.match(report[1], /NOT PLAYED/);
    assert.equal(report.at(-1), 'NO DATA');
  });
});
