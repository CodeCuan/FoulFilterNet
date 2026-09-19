// The harness's listening post (W16), over a recording fake AudioContext:
// the shape of the tap, and audio-graph.js running unchanged on top of it.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { BAND_Q, FFT_SIZE, NOTCH_Q, TONE_HZ, createTap } from '../harness/tap.js';
import { createAudioGraphs } from '../src/audio-graph.js';
import { FLOOR_DB } from '../harness/self-check.js';

/** A fake AudioContext that records nodes and connections. */
function fakeContext({ sampleRate = 48000, levels = {} } = {}) {
  const nodes = [];
  const edges = [];
  let currentTime = 1.25;
  let state = 'suspended';
  const param = (value) => ({
    value,
    setValueAtTime() {},
    linearRampToValueAtTime() {},
    cancelScheduledValues() {},
  });
  const node = (kind, extra = {}) => {
    const n = {
      kind,
      connect(target) {
        edges.push([n, target]);
        return target;
      },
      ...extra,
    };
    nodes.push(n);
    return n;
  };
  const destination = node('destination');
  const ctx = {
    sampleRate,
    destination,
    get currentTime() {
      return currentTime;
    },
    set currentTime(v) {
      currentTime = v;
    },
    get state() {
      return state;
    },
    async resume() {
      state = 'running';
    },
    createGain: () => node('gain', { gain: param(1) }),
    createAnalyser: () => {
      const a = node('analyser', {
        fftSize: 2048,
        smoothingTimeConstant: 0.8,
        getFloatTimeDomainData(buffer) {
          const level = levels[a.role] ?? 0;
          buffer.fill(level);
        },
      });
      return a;
    },
    createBiquadFilter: () => node('biquad', { type: 'lowpass', frequency: param(350), Q: param(1) }),
    createOscillator: () => node('oscillator', { type: 'sine', frequency: param(440), start() {} }),
    createMediaElementSource: (element) => node('source', { element }),
  };
  return { ctx, nodes, edges, destination };
}

/** Name the analysers so the fake can give each its own level. */
function nameAnalysers(tap) {
  for (const [role, a] of Object.entries(tap.analysers)) a.role = role;
}

const targetsOf = (edges, from) => edges.filter(([a]) => a === from).map(([, b]) => b);

describe('createTap', () => {
  const fake = fakeContext();
  const tap = createTap(() => fake.ctx);
  const { raw, out, programme, tone } = tap.analysers;

  it('makes the context once', () => {
    assert.equal(tap.real, fake.ctx);
  });

  it('gives audio-graph.js a tap as its destination', () => {
    assert.notEqual(tap.context.destination, fake.destination);
    assert.equal(tap.context.destination.kind, 'gain');
  });

  it('sends the tap to the speakers', () => {
    assert.ok(targetsOf(fake.edges, tap.context.destination).includes(fake.destination));
  });

  it('measures the tap directly as `out`', () => {
    assert.ok(targetsOf(fake.edges, tap.context.destination).includes(out));
  });

  it('measures the programme through a 1 kHz notch', () => {
    const notch = targetsOf(fake.edges, tap.context.destination).find((n) => n.kind === 'biquad' && n.type === 'notch');
    assert.equal(notch.frequency.value, TONE_HZ);
    assert.equal(notch.Q.value, NOTCH_Q);
    assert.deepEqual(targetsOf(fake.edges, notch), [programme]);
  });

  it('measures the tone through a 1 kHz band-pass', () => {
    const band = targetsOf(fake.edges, tap.context.destination).find((n) => n.kind === 'biquad' && n.type === 'bandpass');
    assert.equal(band.frequency.value, TONE_HZ);
    assert.equal(band.Q.value, BAND_Q);
    assert.deepEqual(targetsOf(fake.edges, band), [tone]);
  });

  it('sizes every analyser block and turns smoothing off', () => {
    for (const a of [raw, out, programme, tone]) {
      assert.equal(a.fftSize, FFT_SIZE);
      assert.equal(a.smoothingTimeConstant, 0);
    }
  });

  it('keeps every analyser pulled through a silent gain', () => {
    for (const a of [raw, out, programme, tone]) {
      const [sink] = targetsOf(fake.edges, a);
      assert.equal(sink.kind, 'gain');
      assert.equal(sink.gain.value, 0);
      assert.deepEqual(targetsOf(fake.edges, sink), [fake.destination]);
    }
  });

  it('feeds the raw analyser from the element source', () => {
    const video = { id: 'v' };
    const source = tap.context.createMediaElementSource(video);
    assert.equal(source.element, video);
    assert.ok(targetsOf(fake.edges, source).includes(raw));
  });

  it('forwards the clock and state', async () => {
    assert.equal(tap.context.currentTime, 1.25);
    assert.equal(tap.context.state, 'suspended');
    await tap.context.resume();
    assert.equal(tap.context.state, 'running');
  });

  it('knows how long a block is', () => {
    assert.equal(tap.windowSeconds, FFT_SIZE / 48000);
  });
});

describe('createTap: read', () => {
  it('reads all four levels at the media time of the block', () => {
    const fake = fakeContext({ levels: { raw: 0.1, out: 0, programme: 0, tone: 0.125 } });
    const tap = createTap(() => fake.ctx);
    nameAnalysers(tap);
    const s = tap.read({ currentTime: 3, playbackRate: 1 });
    assert.equal(s.t, 3 - FFT_SIZE / 48000 / 2);
    assert.ok(Math.abs(s.raw - -20) < 1e-3);
    assert.equal(s.out, FLOOR_DB);
    assert.equal(s.programme, FLOOR_DB);
    assert.ok(Math.abs(s.tone - -18.06) < 0.01);
  });

  it('allows for the playback rate', () => {
    const fake = fakeContext();
    const tap = createTap(() => fake.ctx);
    assert.equal(tap.read({ currentTime: 3, playbackRate: 2 }).t, 3 - FFT_SIZE / 48000);
  });

  it('says how much media the block covered', () => {
    const fake = fakeContext();
    const tap = createTap(() => fake.ctx);
    assert.equal(tap.read({ currentTime: 3, playbackRate: 1 }).w, FFT_SIZE / 48000);
  });

  it('covers twice the media at 2x', () => {
    const fake = fakeContext();
    const tap = createTap(() => fake.ctx);
    assert.equal(tap.read({ currentTime: 3, playbackRate: 2 }).w, (2 * FFT_SIZE) / 48000);
  });

  it('treats a bad rate as 1', () => {
    const fake = fakeContext();
    const tap = createTap(() => fake.ctx);
    assert.equal(tap.read({ currentTime: 3, playbackRate: 0 }).w, FFT_SIZE / 48000);
  });

  it('uses the context sample rate', () => {
    const fake = fakeContext({ sampleRate: 44100 });
    const tap = createTap(() => fake.ctx);
    assert.equal(tap.read({ currentTime: 3, playbackRate: 1 }).t, 3 - FFT_SIZE / 44100 / 2);
  });
});

describe('audio-graph.js on the tap', () => {
  const fake = fakeContext();
  const tap = createTap(() => fake.ctx);
  const graphs = createAudioGraphs({ createContext: () => tap.context });
  const video = { id: 'video' };
  const result = graphs.attach(video);

  it('attaches', () => {
    assert.equal(result.ok, true);
  });

  it('connects the programme gain to the tap, not the speakers', () => {
    const source = fake.nodes.find((n) => n.kind === 'source');
    const programme = targetsOf(fake.edges, source).find((n) => n.kind === 'gain');
    assert.deepEqual(targetsOf(fake.edges, programme), [tap.context.destination]);
  });

  it('connects the bleep gain to the tap too', () => {
    const tapInputs = fake.edges.filter(([, b]) => b === tap.context.destination).map(([a]) => a);
    assert.equal(tapInputs.length, 2);
  });

  it('still measures the raw source', () => {
    const source = fake.nodes.find((n) => n.kind === 'source');
    assert.ok(targetsOf(fake.edges, source).includes(tap.analysers.raw));
  });

  it('runs the graph on the real clock', () => {
    fake.ctx.currentTime = 7.5;
    assert.equal(result.graph.currentTime, 7.5);
  });
});
