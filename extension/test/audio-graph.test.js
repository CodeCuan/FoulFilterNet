// audio-graph.js against a fake AudioContext that records every node it makes
// and every call on every gain's AudioParam.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  BLEEP_FREQUENCY,
  BLEEP_LEVEL,
  RAMP_SECONDS,
  createAudioGraphs,
  getPageAudio,
} from '../src/audio-graph.js';
import { planSchedule } from '../src/schedule.js';

class FakeParam {
  value;
  calls = [];
  constructor(value) {
    this.value = value;
  }
  setValueAtTime(value, time) {
    this.calls.push(['set', value, time]);
  }
  linearRampToValueAtTime(value, time) {
    this.calls.push(['ramp', value, time]);
  }
  cancelScheduledValues(time) {
    this.calls.push(['cancel', time]);
  }
}

class FakeNode {
  connections = [];
  constructor(kind) {
    this.kind = kind;
  }
  connect(destination) {
    this.connections.push(destination);
    return destination;
  }
}

class FakeGain extends FakeNode {
  gain = new FakeParam(1);
  constructor() {
    super('gain');
  }
}

class FakeOscillator extends FakeNode {
  type = 'square';
  frequency = { value: 440 };
  starts = 0;
  constructor() {
    super('oscillator');
  }
  start() {
    this.starts++;
  }
}

class FakeContext {
  currentTime = 50;
  state = 'running';
  destination = new FakeNode('destination');
  sources = [];
  gains = [];
  oscillators = [];
  closes = 0;
  resumes = 0;
  /** @type {unknown} set to make createMediaElementSource throw it */
  sourceError = null;
  /** @type {unknown} set to make resume() reject */
  resumeError = null;
  /** The state after resume() resolves. */
  stateAfterResume = 'running';

  createMediaElementSource(element) {
    if (this.sourceError) throw this.sourceError;
    const node = new FakeNode('source');
    node.element = element;
    this.sources.push(node);
    return node;
  }
  createGain() {
    const node = new FakeGain();
    this.gains.push(node);
    return node;
  }
  createOscillator() {
    const node = new FakeOscillator();
    this.oscillators.push(node);
    return node;
  }
  async resume() {
    this.resumes++;
    if (this.resumeError) throw this.resumeError;
    this.state = this.stateAfterResume;
  }
  close() {
    this.closes++;
  }
}

const invalidState = () => Object.assign(new Error('already connected previously'), { name: 'InvalidStateError' });

function setup() {
  const contexts = [];
  const graphs = createAudioGraphs({
    createContext: () => {
      const context = new FakeContext();
      contexts.push(context);
      return context;
    },
  });
  return { graphs, contexts };
}

/** Attach one video and return the pieces, with the param calls from building cleared. */
function attached() {
  const { graphs, contexts } = setup();
  const video = {};
  const result = graphs.attach(video);
  const context = contexts[0];
  const source = context.sources[0];
  const programme = source.connections[0];
  const split = context.oscillators[0].connections[0];
  const bleep = split.connections[0];
  programme.gain.calls.length = 0;
  bleep.gain.calls.length = 0;
  return { graphs, contexts, context, video, result, graph: result.graph, source, programme, bleep, split };
}

const hit = (start, end) => ({ start, end, phrase: 'x' });

/** A plan made at audio time 50, playhead 10, 1x. */
const planAt = (hits, overrides = {}) =>
  planSchedule({ hits, currentTime: 10, playbackRate: 1, audioNow: 50, playing: true, ...overrides });

const near = (a, b) => Math.abs(a - b) < 1e-9;

/** Param calls with times rounded to the microsecond. */
const rounded = (calls) => calls.map((c) => c.map((v) => (typeof v === 'number' ? Math.round(v * 1e6) / 1e6 : v)));

describe('audio graph: constants', () => {
  it('bleeps at 1 kHz', () => {
    assert.equal(BLEEP_FREQUENCY, 1000);
  });

  it('bleeps at FFmpeg sine amplitude', () => {
    assert.equal(BLEEP_LEVEL, 0.125);
  });

  it('ramps edges over 5 ms', () => {
    assert.equal(RAMP_SECONDS, 0.005);
  });
});

describe('audio graph: the context', () => {
  it('is not created before the first attach', () => {
    assert.equal(setup().contexts.length, 0);
  });

  it('is null before the first attach', () => {
    assert.equal(setup().graphs.context, null);
  });

  it('is created by the first attach', () => {
    const { graphs, contexts } = setup();
    graphs.attach({});
    assert.equal(contexts.length, 1);
  });

  it('is exposed after the first attach', () => {
    const { graphs, contexts } = setup();
    graphs.attach({});
    assert.equal(graphs.context, contexts[0]);
  });

  it('is shared by every video', () => {
    const { graphs, contexts } = setup();
    graphs.attach({});
    graphs.attach({});
    assert.equal(contexts.length, 1);
  });

  it('is never closed', () => {
    const { graphs, contexts } = setup();
    graphs.attach({});
    graphs.attach({});
    contexts[0].sourceError = invalidState();
    graphs.attach({});
    assert.equal(contexts[0].closes, 0);
  });

  it('answers an audio error when it cannot be created', () => {
    const graphs = createAudioGraphs({
      createContext: () => {
        throw new Error('no Web Audio');
      },
    });
    const result = graphs.attach({});
    assert.deepEqual([result.ok, result.error.kind, result.error.reason], [false, 'audio', 'no-context']);
  });

  it('says why it could not be created', () => {
    const graphs = createAudioGraphs({
      createContext: () => {
        throw new Error('no Web Audio');
      },
    });
    assert.match(graphs.attach({}).error.detail, /no Web Audio/);
  });

  it('tries again after failing to be created', () => {
    let calls = 0;
    const graphs = createAudioGraphs({
      createContext: () => {
        calls++;
        if (calls === 1) throw new Error('not yet');
        return new FakeContext();
      },
    });
    graphs.attach({});
    assert.equal(graphs.attach({}).ok, true);
  });
});

describe('audio graph: the tone', () => {
  it('is one oscillator for the page', () => {
    const { graphs, contexts } = setup();
    graphs.attach({});
    graphs.attach({});
    assert.equal(contexts[0].oscillators.length, 1);
  });

  it('is started once', () => {
    const { graphs, contexts } = setup();
    graphs.attach({});
    graphs.attach({});
    graphs.attach({});
    assert.equal(contexts[0].oscillators[0].starts, 1);
  });

  it('is a sine', () => {
    assert.equal(attached().context.oscillators[0].type, 'sine');
  });

  it('is at 1 kHz', () => {
    assert.equal(attached().context.oscillators[0].frequency.value, 1000);
  });

  it('feeds each video its own bleep gain', () => {
    const { graphs, contexts } = setup();
    graphs.attach({});
    graphs.attach({});
    assert.equal(contexts[0].oscillators[0].connections[0].connections.length, 2);
  });
});

describe('audio graph: attach', () => {
  it('succeeds', () => {
    assert.equal(attached().result.ok, true);
  });

  it('makes one source for the element', () => {
    const { context, video } = attached();
    assert.deepEqual(
      context.sources.map((s) => s.element),
      [video],
    );
  });

  it('never makes a second source for the same element', () => {
    const { graphs, context, video } = attached();
    graphs.attach(video);
    graphs.attach(video);
    assert.equal(context.sources.length, 1);
  });

  it('returns the same graph on a second attach', () => {
    const { graphs, video, graph } = attached();
    assert.equal(graphs.attach(video).graph, graph);
  });

  it('makes a separate graph for another element', () => {
    const { graphs, graph } = attached();
    assert.notEqual(graphs.attach({}).graph, graph);
  });

  it('routes the source into the programme gain', () => {
    const { source } = attached();
    assert.equal(source.connections[0].kind, 'gain');
  });

  it('routes the programme gain to the destination', () => {
    const { programme, context } = attached();
    assert.deepEqual(programme.connections, [context.destination]);
  });

  it('routes the bleep gain to the destination', () => {
    const { bleep, context } = attached();
    assert.deepEqual(bleep.connections, [context.destination]);
  });

  it('starts with the programme closed', () => {
    assert.equal(attached().programme.gain.value, 0);
  });

  it('starts with the bleep silent', () => {
    assert.equal(attached().bleep.gain.value, 0);
  });

  it('keeps the tone split at unity', () => {
    assert.equal(attached().split.gain.value, 1);
  });

  it('returns a frozen graph', () => {
    assert.equal(Object.isFrozen(attached().graph), true);
  });
});

describe('audio graph: an element attached before', () => {
  function refused() {
    const { graphs, contexts } = setup();
    graphs.attach({}); // make the context
    contexts[0].sourceError = invalidState();
    const video = {};
    return { graphs, context: contexts[0], video, result: graphs.attach(video) };
  }

  it('is not ok', () => {
    assert.equal(refused().result.ok, false);
  });

  it('is an audio error', () => {
    assert.equal(refused().result.error.kind, 'audio');
  });

  it('has the reason already-connected', () => {
    assert.equal(refused().result.error.reason, 'already-connected');
  });

  it('says to reload the page', () => {
    assert.match(refused().result.error.detail, /Reload the page/);
  });

  it('builds no graph nodes for it', () => {
    const { context } = refused();
    // The first video's two gains and the tone split only.
    assert.equal(context.gains.length, 3);
  });

  it('is remembered: the source is not tried again', () => {
    const { graphs, context, video } = refused();
    let tries = 0;
    context.createMediaElementSource = () => {
      tries++;
      return new FakeNode('source');
    };
    graphs.attach(video);
    assert.equal(tries, 0);
  });

  it('answers the same error again', () => {
    const { graphs, video, result } = refused();
    assert.equal(graphs.attach(video), result);
  });

  it('does not affect another element', () => {
    const { graphs, context } = refused();
    context.sourceError = null;
    assert.equal(graphs.attach({}).ok, true);
  });

  it('is refused on the very first attach too', () => {
    const graphs = createAudioGraphs({
      createContext: () => Object.assign(new FakeContext(), { sourceError: invalidState() }),
    });
    assert.equal(graphs.attach({}).error.reason, 'already-connected');
  });
});

describe('audio graph: any other refusal', () => {
  function failing() {
    const { graphs, contexts } = setup();
    graphs.attach({});
    contexts[0].sourceError = new TypeError('not a media element');
    const video = {};
    return { graphs, context: contexts[0], video, result: graphs.attach(video) };
  }

  it('is an audio error', () => {
    assert.equal(failing().result.error.kind, 'audio');
  });

  it('has the reason failed', () => {
    assert.equal(failing().result.error.reason, 'failed');
  });

  it('carries the browser message', () => {
    assert.match(failing().result.error.detail, /not a media element/);
  });

  it('is tried again on the next attach', () => {
    const { graphs, context, video } = failing();
    context.sourceError = null;
    assert.equal(graphs.attach(video).ok, true);
  });
});

describe('audio graph: apply, nothing to censor', () => {
  it('cancels the programme from the plan time first', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([]));
    assert.deepEqual(programme.gain.calls[0], ['cancel', 50]);
  });

  it('cancels the bleep from the plan time', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([]));
    assert.deepEqual(bleep.gain.calls[0], ['cancel', 50]);
  });

  it('opens the programme at the plan time', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([]));
    assert.deepEqual(programme.gain.calls, [
      ['cancel', 50],
      ['set', 1, 50],
    ]);
  });

  it('silences the bleep at the plan time', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([]), 'bleep');
    assert.deepEqual(bleep.gain.calls, [
      ['cancel', 50],
      ['set', 0, 50],
    ]);
  });

  it('uses the plan time, not the context clock', () => {
    const { graph, programme, context } = attached();
    context.currentTime = 50.004;
    graph.apply(planAt([]));
    assert.deepEqual(programme.gain.calls[0], ['cancel', 50]);
  });
});

describe('audio graph: apply, silence', () => {
  it('ramps the programme down to close at the close time', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(11, 12)]), 'silence');
    assert.deepEqual(rounded(programme.gain.calls.slice(2, 4)), [
      ['set', 1, 50.995],
      ['ramp', 0, 51],
    ]);
  });

  it('ramps the programme up after the open time', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(11, 12)]), 'silence');
    assert.deepEqual(rounded(programme.gain.calls.slice(4)), [
      ['set', 0, 52],
      ['ramp', 1, 52.005],
    ]);
  });

  it('makes exactly the calls for one hit', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(11, 12)]), 'silence');
    assert.equal(programme.gain.calls.length, 6);
  });

  it('keeps the bleep at 0 throughout', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(11, 12)]), 'silence');
    const values = bleep.gain.calls.filter((c) => c[0] !== 'cancel').map((c) => c[1]);
    assert.deepEqual(new Set(values), new Set([0]));
  });

  it('is the default method', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(11, 12)]));
    const values = bleep.gain.calls.filter((c) => c[0] !== 'cancel').map((c) => c[1]);
    assert.deepEqual(new Set(values), new Set([0]));
  });

  it('treats an unknown method as silence', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(11, 12)]), 'remove');
    const values = bleep.gain.calls.filter((c) => c[0] !== 'cancel').map((c) => c[1]);
    assert.deepEqual(new Set(values), new Set([0]));
  });

  it('closes the programme at once inside a hit', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(9, 11)]));
    assert.deepEqual(programme.gain.calls[1], ['set', 0, 50]);
  });

  it('then only opens it inside a hit', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(9, 11)]));
    assert.deepEqual(rounded(programme.gain.calls.slice(2)), [
      ['set', 0, 51],
      ['ramp', 1, 51.005],
    ]);
  });

  it('starts a close ramp no earlier than the plan time', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(10.002, 11)]));
    assert.deepEqual(rounded(programme.gain.calls.slice(2, 4)), [
      ['set', 1, 50],
      ['ramp', 0, 50.002],
    ]);
  });

  it('schedules each of several hits', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(10.5, 10.8), hit(11.2, 11.4)]));
    assert.equal(programme.gain.calls.filter((c) => c[0] === 'ramp').length, 4);
  });

  it('schedules nothing when paused', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(11, 12)], { playing: false }));
    assert.equal(programme.gain.calls.length, 2);
  });

  it('closes when paused inside a hit', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(9, 11)], { playing: false }));
    assert.deepEqual(programme.gain.calls, [
      ['cancel', 50],
      ['set', 0, 50],
    ]);
  });

  it('cancels before every re-apply', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(11, 12)]));
    graph.apply(planAt([hit(11, 12)], { audioNow: 50.1, currentTime: 10.1 }));
    assert.deepEqual(
      programme.gain.calls.filter((c) => c[0] === 'cancel'),
      [
        ['cancel', 50],
        ['cancel', 50.1],
      ],
    );
  });

  it('puts every programme value in [0, 1]', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(9, 10.2), hit(10.5, 10.8), hit(11, 13)]));
    const values = programme.gain.calls.filter((c) => c[0] !== 'cancel').map((c) => c[1]);
    assert.equal(
      values.every((v) => v === 0 || v === 1),
      true,
    );
  });

  it('never schedules before the plan time', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(9, 10.2), hit(10.001, 10.1), hit(10.5, 10.8)]));
    assert.equal(
      programme.gain.calls.every((c) => c.at(-1) >= 50),
      true,
    );
  });
});

describe('audio graph: apply, bleep', () => {
  it('opens the tone while the programme closes', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(11, 12)]), 'bleep');
    assert.deepEqual(rounded(bleep.gain.calls.slice(2, 4)), [
      ['set', 0, 50.995],
      ['ramp', 0.125, 51],
    ]);
  });

  it('closes the tone while the programme opens', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(11, 12)]), 'bleep');
    assert.deepEqual(rounded(bleep.gain.calls.slice(4)), [
      ['set', 0.125, 52],
      ['ramp', 0, 52.005],
    ]);
  });

  it('mirrors the programme at exactly the same instants', () => {
    const { graph, programme, bleep } = attached();
    graph.apply(planAt([hit(9, 10.3), hit(10.5, 10.8), hit(11.2, 11.9)]), 'bleep');
    const times = (calls) => calls.map((c) => c.at(-1));
    assert.deepEqual(times(bleep.gain.calls), times(programme.gain.calls));
  });

  it('mirrors the programme value at every instant', () => {
    const { graph, programme, bleep } = attached();
    graph.apply(planAt([hit(9, 10.3), hit(10.5, 10.8)]), 'bleep');
    const pairs = programme.gain.calls
      .map((c, i) => [c, bleep.gain.calls[i]])
      .filter(([c]) => c[0] !== 'cancel')
      .map(([p, b]) => near(b[1], BLEEP_LEVEL * (1 - p[1])));
    assert.equal(
      pairs.every(Boolean),
      true,
    );
  });

  it('sounds the tone at once inside a hit', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(9, 11)]), 'bleep');
    assert.deepEqual(bleep.gain.calls[1], ['set', 0.125, 50]);
  });

  it('sounds the tone when paused inside a hit', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(9, 11)], { playing: false }), 'bleep');
    assert.deepEqual(bleep.gain.calls[1], ['set', 0.125, 50]);
  });

  it('keeps the tone silent outside hits', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([]), 'bleep');
    assert.deepEqual(bleep.gain.calls[1], ['set', 0, 50]);
  });

  it('stops a tone when switched to silence', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(9, 11)]), 'bleep');
    graph.apply(planAt([hit(9, 11)]), 'silence');
    assert.deepEqual(bleep.gain.calls.slice(-4, -2), [
      ['cancel', 50],
      ['set', 0, 50],
    ]);
  });
});

describe('audio graph: muteNow and openNow', () => {
  it('muteNow cancels the programme at the context clock', () => {
    const { graph, programme, context } = attached();
    context.currentTime = 77;
    graph.muteNow();
    assert.deepEqual(programme.gain.calls[0], ['cancel', 77]);
  });

  it('muteNow closes the programme', () => {
    const { graph, programme, context } = attached();
    context.currentTime = 77;
    graph.muteNow();
    assert.deepEqual(programme.gain.calls[1], ['set', 0, 77]);
  });

  it('muteNow silences the bleep', () => {
    const { graph, bleep, context } = attached();
    context.currentTime = 77;
    graph.muteNow();
    assert.deepEqual(bleep.gain.calls, [
      ['cancel', 77],
      ['set', 0, 77],
    ]);
  });

  it('muteNow silences a tone that was sounding', () => {
    const { graph, bleep } = attached();
    graph.apply(planAt([hit(9, 11)]), 'bleep');
    graph.muteNow();
    assert.deepEqual(bleep.gain.calls.at(-1), ['set', 0, 50]);
  });

  it('openNow cancels the programme at the context clock', () => {
    const { graph, programme, context } = attached();
    context.currentTime = 80;
    graph.openNow();
    assert.deepEqual(programme.gain.calls[0], ['cancel', 80]);
  });

  it('openNow opens the programme', () => {
    const { graph, programme, context } = attached();
    context.currentTime = 80;
    graph.openNow();
    assert.deepEqual(programme.gain.calls[1], ['set', 1, 80]);
  });

  it('openNow silences the bleep', () => {
    const { graph, bleep, context } = attached();
    context.currentTime = 80;
    graph.openNow();
    assert.deepEqual(bleep.gain.calls, [
      ['cancel', 80],
      ['set', 0, 80],
    ]);
  });

  it('openNow drops a scheduled close', () => {
    const { graph, programme } = attached();
    graph.apply(planAt([hit(11, 12)]));
    graph.openNow();
    assert.deepEqual(programme.gain.calls.slice(-2), [
      ['cancel', 50],
      ['set', 1, 50],
    ]);
  });
});

describe('audio graph: clock, state and resume', () => {
  it('reads the context clock', () => {
    const { graph, context } = attached();
    context.currentTime = 123.25;
    assert.equal(graph.currentTime, 123.25);
  });

  it('reads the context state', () => {
    const { graph, context } = attached();
    context.state = 'suspended';
    assert.equal(graph.state, 'suspended');
  });

  it('resumes the context', async () => {
    const { graph, context } = attached();
    await graph.resume();
    assert.equal(context.resumes, 1);
  });

  it('resolves to the state after resuming', async () => {
    const { graph, context } = attached();
    context.state = 'suspended';
    assert.equal(await graph.resume(), 'running');
  });

  it('resolves to the state when resume is refused', async () => {
    const { graph, context } = attached();
    context.state = 'suspended';
    context.resumeError = new Error('not allowed');
    assert.equal(await graph.resume(), 'suspended');
  });

  it('does not reject when resume is refused', async () => {
    const { graph, context } = attached();
    context.resumeError = new Error('not allowed');
    await assert.doesNotReject(graph.resume());
  });
});

describe('audio graph: getPageAudio', () => {
  it('returns the same instance every time', () => {
    const first = getPageAudio(() => new FakeContext());
    assert.equal(getPageAudio(() => new FakeContext()), first);
  });

  it('ignores a later factory', () => {
    getPageAudio(() => new FakeContext()); // made by the first call in this file, if not already
    let later = 0;
    getPageAudio(() => {
      later++;
      return new FakeContext();
    }).attach({});
    assert.equal(later, 0);
  });
});
