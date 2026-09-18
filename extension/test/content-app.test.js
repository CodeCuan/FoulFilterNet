// content-app.js end to end with fakes: a fake YouTube page (EventTarget
// document and window, as page.test.js), a fake <video> whose play()/pause()
// fire their events, a fake messenger standing in for the service worker and
// the service, a hand-moved clock for every timer, and a fake page audio
// whose graphs record what they are told. The real page watcher, session
// reducer, gate, gate controller and censor controller run in between.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { startContent } from '../src/content-app.js';
import { PLAYER_SELECTOR, VIDEO_SELECTOR } from '../src/page.js';
import { DEFAULT_SETTINGS } from '../src/settings.js';
import { HEARTBEAT_MS, SETTLED_HEARTBEAT_MS } from '../src/session.js';

const A = 'YwARwww5aFo';
const B = 'jNQXAC9IVRw';
const url = (id) => `https://www.youtube.com/watch?v=${id}`;
const HOME = 'https://www.youtube.com/';

const flush = () => new Promise((resolve) => setImmediate(resolve));

class Tracked extends EventTarget {
  count = 0;
  addEventListener(type, listener, options) {
    this.count++;
    super.addEventListener(type, listener, options);
  }
  removeEventListener(type, listener, options) {
    this.count--;
    super.removeEventListener(type, listener, options);
  }
  fire(type) {
    this.dispatchEvent(new Event(type));
  }
}

class FakeVideo extends Tracked {
  currentTime = 0;
  playbackRate = 1;
  duration = 649;
  paused = true;
  ended = false;
  seeking = false;
  readyState = 4;
  isConnected = true;
  plays = 0;
  pauses = 0;
  /** When set, play() is refused as the autoplay policy would. */
  refusePlay = false;

  play() {
    this.plays++;
    if (this.refusePlay) return Promise.reject(Object.assign(new Error('not allowed'), { name: 'NotAllowedError' }));
    if (this.paused) {
      this.paused = false;
      this.fire('play');
      this.fire('playing');
    }
    return Promise.resolve();
  }

  pause() {
    this.pauses++;
    if (!this.paused) {
      this.paused = true;
      this.fire('pause');
    }
  }
}

class FakePlayer {
  isConnected = true;
  classes = new Set();
  classList = { contains: (name) => this.classes.has(name) };
}

class FakeDocument extends Tracked {
  elements = new Map();
  querySelector(selector) {
    const element = this.elements.get(selector);
    return element?.isConnected ? element : null;
  }
}

class FakeWindow extends Tracked {
  location = { href: HOME };
}

/** A MutationObserver double per page: `fire(target)` delivers a mutation to whoever observes it. */
function fakeObservers() {
  const all = [];
  class FakeObserver {
    targets = [];
    constructor(callback) {
      this.callback = callback;
      all.push(this);
    }
    observe(target) {
      this.targets.push(target);
    }
    disconnect() {
      this.targets = [];
    }
  }
  return {
    FakeObserver,
    fire(target) {
      for (const o of all.filter((x) => x.targets.includes(target))) o.callback([], o);
    },
  };
}

class FakeClock {
  now = 0;
  nextId = 1;
  timers = new Map();

  setTimeout = (fn, ms) => {
    const id = this.nextId++;
    this.timers.set(id, { at: this.now + Math.max(0, ms), fn });
    return id;
  };
  clearTimeout = (id) => {
    this.timers.delete(id);
  };
  setInterval = (fn, ms) => {
    const id = this.nextId++;
    this.timers.set(id, { at: this.now + ms, fn, every: ms });
    return id;
  };
  clearInterval = (id) => {
    this.timers.delete(id);
  };

  /** Move the clock on, running every timer that falls due, in order. */
  async advance(ms) {
    const end = this.now + ms;
    for (;;) {
      const due = [...this.timers.entries()].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      const [id, timer] = due;
      this.now = timer.at;
      if (timer.every) timer.at += timer.every;
      else this.timers.delete(id);
      timer.fn();
      await flush();
    }
    this.now = end;
    await flush();
  }
}

class FakeGraph {
  calls = [];
  currentTime = 100;
  state = 'suspended';
  constructor(resumeTo) {
    this.resumeTo = resumeTo;
  }
  apply(plan, method) {
    this.calls.push(['apply', plan, method]);
  }
  muteNow() {
    this.calls.push(['mute']);
  }
  openNow() {
    this.calls.push(['open']);
  }
  async resume() {
    this.calls.push(['resume']);
    this.state = this.resumeTo();
    return this.state;
  }
  get last() {
    return this.calls.filter((c) => c[0] !== 'resume').at(-1);
  }
  get lastPlan() {
    return this.calls.filter((c) => c[0] === 'apply').at(-1)?.[1];
  }
}

const HIT = Object.freeze({ start: 10, end: 11, phrase: 'x' });

/** A WatchView (camelCased) for `videoId`. */
const view = (over = {}) => ({
  key: `youtube-${over.videoId ?? A}`,
  provider: 'youtube',
  videoId: A,
  session: 's1',
  state: 'transcribing',
  reason: null,
  failureKind: null,
  title: null,
  duration: 649,
  revision: 1,
  unchanged: false,
  coverage: [[0, 46]],
  hits: [HIT],
  windowsDone: 2,
  windowsTotal: 30,
  progress: 0.07,
  realtimeFactor: 25,
  keepingUp: true,
  fromCache: false,
  ...over,
});

/**
 * Start the content script on a fake page.
 *
 * @param {object} [options]
 * @param {string} [options.href]
 * @param {(message: object) => object} [options.server]  Answers a heartbeat; default: a covered view.
 * @param {boolean} [options.manual]  Keep heartbeat replies pending until `answer(...)`.
 * @param {object} [options.settings]
 * @param {string} [options.resumeTo]  The context state resume() reaches.
 * @param {object} [options.attachError]  Make attach fail with this error.
 * @param {boolean} [options.playing]  The video is already playing at start.
 */
async function start({
  href = url(A),
  server = (m) => ({ ok: true, view: view({ videoId: m.videoId, key: `youtube-${m.videoId}` }) }),
  manual = false,
  settings = {},
  resumeTo = 'running',
  attachError = null,
  playing = false,
} = {}) {
  const clock = new FakeClock();
  const document = new FakeDocument();
  const window = new FakeWindow();
  window.location.href = href;
  const video = new FakeVideo();
  video.paused = !playing;
  const player = new FakePlayer();
  document.elements.set(VIDEO_SELECTOR, video);
  document.elements.set(PLAYER_SELECTOR, player);

  const sent = [];
  const pending = [];
  const send = (message) => {
    sent.push(message);
    if (message.type !== 'ff/heartbeat') return Promise.resolve({ ok: true });
    if (manual) return new Promise((resolve) => pending.push({ message, resolve }));
    return Promise.resolve(server(message));
  };

  let settingsListener = null;
  let currentSettings = { ...DEFAULT_SETTINGS, ...settings };
  const settingsSource = {
    load: async () => currentSettings,
    subscribe: (listener) => {
      settingsListener = listener;
      return () => (settingsListener = null);
    },
  };

  const graphs = [];
  const attaches = [];
  const audio = {
    attach(element) {
      attaches.push(element);
      if (attachError) return { ok: false, error: attachError };
      let graph = graphs.find((g) => g.element === element);
      if (!graph) {
        graph = new FakeGraph(() => resumeTo);
        graph.element = element;
        graphs.push(graph);
      }
      return { ok: true, graph };
    },
  };

  const renders = [];
  let overlayCallbacks = null;
  const createOverlay = (options) => {
    overlayCallbacks = options;
    return {
      render: (model, where) => renders.push({ model, where }),
      destroy: () => renders.push({ destroyed: true }),
    };
  };

  const observers = fakeObservers();
  const app = startContent({
    document,
    window,
    send,
    settings: settingsSource,
    audio,
    createOverlay,
    MutationObserver: observers.FakeObserver,
    now: () => clock.now,
    setTimeout: clock.setTimeout,
    clearTimeout: clock.clearTimeout,
    setInterval: clock.setInterval,
    clearInterval: clock.clearInterval,
  });
  await flush();

  const h = {
    app,
    clock,
    document,
    window,
    video,
    player,
    sent,
    pending,
    graphs,
    attaches,
    renders,
    get graph() {
      return graphs.at(-1);
    },
    get heartbeats() {
      return sent.filter((m) => m.type === 'ff/heartbeat');
    },
    get badges() {
      return sent.filter((m) => m.type === 'ff/badge');
    },
    get badge() {
      return h.badges.at(-1);
    },
    get overlay() {
      return renders.filter((r) => r.model).at(-1)?.model;
    },
    get overlayCallbacks() {
      return overlayCallbacks;
    },
    /** Resolve the oldest pending heartbeat with `reply`. */
    async answer(reply) {
      pending.shift().resolve(reply);
      await flush();
    },
    /** Change the settings as chrome.storage would. */
    async setSettings(patch) {
      currentSettings = { ...currentSettings, ...patch };
      settingsListener?.(currentSettings);
      await flush();
    },
    /** YouTube navigating to `to`. */
    async navigate(to) {
      document.fire('yt-navigate-start');
      window.location.href = to;
      document.fire('yt-navigate-finish');
      await flush();
    },
    /** The ad class going on or off on the player, as YouTube does. */
    async setAd(on) {
      if (on) player.classes.add('ad-showing');
      else player.classes.delete('ad-showing');
      observers.fire(player);
      await flush();
    },
    /** The user presses play. */
    async play() {
      video.play();
      await flush();
    },
  };
  return h;
}

describe('content app: start', () => {
  it('follows the page watcher', async () => {
    const h = await start();
    assert.equal(h.app.state.videoId, A);
  });

  it('loads the settings', async () => {
    const h = await start();
    assert.equal(h.app.state.settingsLoaded, true);
  });

  it('holds a paused video before the first answer', async () => {
    const h = await start();
    assert.equal(h.app.gate.heldByUs, true);
  });

  it('shows the starting overlay', async () => {
    const h = await start();
    assert.equal(h.overlay.title, 'FoulFilter is starting');
  });

  it('renders the overlay into the player', async () => {
    const h = await start();
    assert.equal(h.renders.at(-1).where, h.player);
  });

  it('sends a badge for the tab', async () => {
    const h = await start();
    assert.equal(h.badge.text, '…');
  });

  it('does not repeat an unchanged badge', async () => {
    const h = await start();
    const count = h.badges.length;
    h.video.fire('timeupdate');
    assert.equal(h.badges.length, count);
  });

  it('sends no heartbeat before its timer runs', async () => {
    const h = await start();
    assert.equal(h.heartbeats.length, 0);
  });

  it('sends the first heartbeat at once', async () => {
    const h = await start();
    await h.clock.advance(0);
    assert.equal(h.heartbeats.length, 1);
  });

  it('sends the video and playhead in the first heartbeat', async () => {
    const h = await start();
    h.video.currentTime = 12;
    await h.clock.advance(0);
    assert.deepEqual([h.heartbeats[0].videoId, h.heartbeats[0].position, h.heartbeats[0].since], [A, 12, null]);
  });

  it('does not attach the audio to a paused video', async () => {
    const h = await start();
    assert.equal(h.attaches.length, 0);
  });

  it('attaches the audio at once to a video already playing', async () => {
    const h = await start({ playing: true });
    assert.equal(h.attaches.length, 1);
  });

  it('pauses a video already playing until covered', async () => {
    const h = await start({ playing: true });
    assert.equal(h.video.paused, true);
  });
});

describe('content app: the heartbeat loop', () => {
  it('beats every second while transcribing', async () => {
    const h = await start();
    await h.clock.advance(3 * HEARTBEAT_MS);
    assert.equal(h.heartbeats.length, 4);
  });

  it('beats every five seconds once complete', async () => {
    const h = await start({ server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(10_000);
    assert.equal(h.heartbeats.length, 1 + 10_000 / SETTLED_HEARTBEAT_MS);
  });

  it('sends since and session after the first view', async () => {
    const h = await start();
    await h.clock.advance(HEARTBEAT_MS);
    assert.deepEqual([h.heartbeats[1].since, h.heartbeats[1].session], [1, 's1']);
  });

  it('sends one heartbeat at a time', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(5000);
    assert.equal(h.heartbeats.length, 1);
  });

  it('gives up a heartbeat with no reply and sends another', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(10_000);
    assert.equal(h.heartbeats.length, 2);
  });

  it('stops beating on a non-watch page', async () => {
    const h = await start();
    await h.clock.advance(0);
    await h.navigate(HOME);
    await h.clock.advance(5000);
    assert.equal(h.heartbeats.length, 1);
  });

  it('never beats when disabled', async () => {
    const h = await start({ settings: { enabled: false } });
    await h.clock.advance(5000);
    assert.equal(h.heartbeats.length, 0);
  });

  it('starts beating when enabled', async () => {
    const h = await start({ settings: { enabled: false } });
    await h.setSettings({ enabled: true });
    await h.clock.advance(0);
    assert.equal(h.heartbeats.length, 1);
  });

  it('stops beating when disabled', async () => {
    const h = await start();
    await h.clock.advance(0);
    await h.setSettings({ enabled: false });
    await h.clock.advance(5000);
    assert.equal(h.heartbeats.length, 1);
  });

  it('starts at once for a new video', async () => {
    const h = await start();
    await h.clock.advance(0);
    await h.navigate(url(B));
    await h.clock.advance(0);
    assert.deepEqual(h.heartbeats.map((m) => m.videoId), [A, B]);
  });

  it('asks the new video with no since', async () => {
    const h = await start();
    await h.clock.advance(0);
    await h.navigate(url(B));
    await h.clock.advance(0);
    assert.equal(h.heartbeats[1].since, null);
  });

  it('discards a reply about the previous video', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.navigate(url(B));
    await h.answer({ ok: true, view: view({ state: 'complete' }) });
    assert.equal(h.app.state.view, null);
  });

  it('keeps holding the new video after the stale reply', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.navigate(url(B));
    await h.answer({ ok: true, view: view({ state: 'complete' }) });
    assert.equal(h.app.derived.gate.reason, 'starting');
  });
});

describe('content app: first view, from hold to release', () => {
  it('holds while the service is fetching', async () => {
    const h = await start({ server: () => ({ ok: true, view: view({ state: 'fetching', coverage: [], hits: [] }) }) });
    await h.clock.advance(0);
    await h.play();
    assert.deepEqual([h.video.paused, h.app.derived.gate.reason], [true, 'still-preparing']);
  });

  it('catches the play press and attaches the audio', async () => {
    const h = await start({ manual: true });
    await h.play();
    assert.equal(h.attaches.length, 1);
  });

  it('resumes the context from the play press', async () => {
    const h = await start({ manual: true });
    await h.play();
    assert.equal(h.graph.calls.filter((c) => c[0] === 'resume').length, 1);
  });

  it('keeps the new graph muted from the start while held', async () => {
    const h = await start({ manual: true });
    await h.play();
    assert.deepEqual(h.graph.calls[0], ['mute']);
  });

  it('records the running context', async () => {
    const h = await start({ manual: true });
    await h.play();
    assert.equal(h.app.state.audio.status, 'running');
  });

  it('mutes the graph while held', async () => {
    const h = await start({ manual: true });
    await h.play();
    assert.equal(h.app.censor.mode, 'mute');
  });

  it('plays once enough is covered, after the user pressed play', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    assert.equal(h.video.paused, false);
  });

  it('filters once released', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    assert.equal(h.app.censor.mode, 'filter');
  });

  it('gives the censor the view hits', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    assert.deepEqual(h.app.derived.hits, [HIT]);
  });

  it('schedules the hit ahead on the audio clock', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    h.video.currentTime = 9;
    await h.clock.advance(100);
    assert.deepEqual(h.graph.lastPlan.events.map((e) => e.gain), [0, 1]);
  });

  it('closes over the hit at its start', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    h.video.currentTime = 9;
    await h.clock.advance(100);
    assert.equal(h.graph.lastPlan.events[0].time, h.graph.currentTime + 1);
  });

  it('bleeps when the settings say so', async () => {
    const h = await start({ manual: true, settings: { censorMethod: 'bleep' } });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    h.video.currentTime = 9;
    await h.clock.advance(100);
    assert.equal(h.graph.calls.filter((c) => c[0] === 'apply').at(-1)[2], 'bleep');
  });

  it('hides the overlay once released', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    assert.equal(h.overlay.visible, false);
  });

  it('shows ✓ once released', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    assert.equal(h.badge.text, '✓');
  });

  it('does not start a video the user never played', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.answer({ ok: true, view: view() });
    assert.equal(h.video.paused, true);
  });

  it('keeps the hits across an unchanged reply', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    await h.clock.advance(HEARTBEAT_MS);
    await h.answer({ ok: true, view: view({ unchanged: true, hits: null, coverage: [[0, 90]] }) });
    assert.deepEqual(h.app.derived.hits, [HIT]);
  });

  it('tells the graph nothing new on a timeupdate that changes nothing', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    const before = h.app.censor.applyCount;
    h.video.fire('timeupdate');
    assert.equal(h.app.censor.applyCount, before);
  });

  it('plays a cached video straight away', async () => {
    const h = await start({ server: () => ({ ok: true, view: view({ state: 'complete', fromCache: true }) }) });
    await h.clock.advance(0);
    await h.play();
    assert.equal(h.video.paused, false);
  });
});

describe('content app: failures', () => {
  it('holds with the error when the service is down from the start', async () => {
    const h = await start({ server: () => ({ ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    assert.equal(h.overlay.title, 'FoulFilterNet is not reachable');
  });

  it('keeps playing through two failures while covered', async () => {
    let up = true;
    const h = await start({ server: () => (up ? { ok: true, view: view() } : { ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    await h.play();
    up = false;
    await h.clock.advance(2 * HEARTBEAT_MS);
    assert.equal(h.video.paused, false);
  });

  it('holds once the service has been down for three heartbeats', async () => {
    let up = true;
    const h = await start({ server: () => (up ? { ok: true, view: view() } : { ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    await h.play();
    up = false;
    await h.clock.advance(3 * HEARTBEAT_MS);
    assert.deepEqual([h.video.paused, h.app.derived.gate.reason], [true, 'error']);
  });

  it('mutes while holding for the error', async () => {
    let up = true;
    const h = await start({ server: () => (up ? { ok: true, view: view() } : { ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    await h.play();
    up = false;
    await h.clock.advance(3 * HEARTBEAT_MS);
    assert.equal(h.app.censor.mode, 'mute');
  });

  it('plays again when the service comes back', async () => {
    let up = true;
    const h = await start({ server: () => (up ? { ok: true, view: view() } : { ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    await h.play();
    up = false;
    await h.clock.advance(3 * HEARTBEAT_MS);
    up = true;
    await h.clock.advance(HEARTBEAT_MS);
    assert.equal(h.video.paused, false);
  });

  it('keeps playing a complete video with the service down', async () => {
    let up = true;
    const complete = view({ state: 'complete' });
    const h = await start({ server: () => (up ? { ok: true, view: complete } : { ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    await h.play();
    up = false;
    await h.clock.advance(30_000);
    assert.equal(h.video.paused, false);
  });

  it('holds with "reload the page" when the audio cannot be attached', async () => {
    const error = { kind: 'audio', reason: 'already-connected', detail: 'Reload the page to filter it.' };
    const h = await start({ attachError: error });
    await h.play();
    assert.match(h.overlay.detail, /Reload the page/);
  });

  it('keeps the video paused when the audio cannot be attached', async () => {
    const error = { kind: 'audio', reason: 'already-connected', detail: 'Reload the page to filter it.' };
    const h = await start({ attachError: error, server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    assert.equal(h.video.paused, true);
  });

  it('stops heartbeating when the audio cannot be attached', async () => {
    const error = { kind: 'audio', reason: 'already-connected', detail: 'x' };
    const h = await start({ attachError: error });
    await h.play();
    await h.clock.advance(5000);
    assert.equal(h.heartbeats.length, 0);
  });
});

describe('content app: a suspended context', () => {
  it('asks for a click', async () => {
    const h = await start({ resumeTo: 'suspended' });
    await h.play();
    assert.equal(h.overlay.title, 'Click to enable FoulFilter');
  });

  it('offers the Enable sound button', async () => {
    const h = await start({ resumeTo: 'suspended' });
    await h.play();
    assert.equal(h.overlay.showEnable, true);
  });

  it('holds the video', async () => {
    const h = await start({ resumeTo: 'suspended', server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    assert.equal(h.video.paused, true);
  });

  it('resumes after the click on a suspended context', async () => {
    const h = await start({ resumeTo: 'suspended', server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    h.graph.resumeTo = () => 'running';
    h.overlayCallbacks.onEnable();
    await flush();
    assert.deepEqual([h.app.state.audio.status, h.video.paused], ['running', false]);
  });

  it('hides the overlay once running', async () => {
    const h = await start({ resumeTo: 'suspended', server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    h.graph.resumeTo = () => 'running';
    h.overlayCallbacks.onEnable();
    await flush();
    assert.equal(h.overlay.visible, false);
  });
});

describe('content app: Watch unfiltered', () => {
  it('opens the graph', async () => {
    const h = await start({ server: () => ({ ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    await h.play();
    h.overlayCallbacks.onUnfiltered();
    await flush();
    assert.equal(h.app.censor.mode, 'open');
  });

  it('plays the video inside the click', async () => {
    const h = await start({ server: () => ({ ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    h.overlayCallbacks.onUnfiltered();
    assert.equal(h.video.paused, false);
  });

  it('plays even a video the user had paused', async () => {
    const h = await start({ server: () => ({ ok: false, error: { kind: 'unreachable' } }) });
    await h.clock.advance(0);
    assert.equal(h.app.gate.userPaused, true);
    h.overlayCallbacks.onUnfiltered();
    assert.equal(h.video.paused, false);
  });

  it('stops heartbeating', async () => {
    const h = await start();
    await h.clock.advance(0);
    h.overlayCallbacks.onUnfiltered();
    await h.clock.advance(5000);
    assert.equal(h.heartbeats.length, 1);
  });

  it('says OFF on the badge', async () => {
    const h = await start();
    h.overlayCallbacks.onUnfiltered();
    await flush();
    assert.equal(h.badge.text, 'OFF');
  });

  it('lasts only until the next video', async () => {
    const h = await start();
    h.overlayCallbacks.onUnfiltered();
    await h.navigate(url(B));
    assert.equal(h.app.state.unfiltered, false);
  });
});

describe('content app: settings', () => {
  it('opens the graph when disabled', async () => {
    const h = await start({ server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    await h.setSettings({ enabled: false });
    assert.equal(h.app.censor.mode, 'open');
  });

  it('hides the overlay when disabled', async () => {
    const h = await start();
    await h.setSettings({ enabled: false });
    assert.equal(h.overlay.visible, false);
  });

  it('lets a held video go when disabled', async () => {
    const h = await start();
    await h.setSettings({ enabled: false });
    assert.equal(h.app.gate.heldByUs, false);
  });

  it('never attaches the audio when disabled', async () => {
    const h = await start({ settings: { enabled: false } });
    await h.play();
    assert.equal(h.attaches.length, 0);
  });

  it('says OFF when disabled', async () => {
    const h = await start({ settings: { enabled: false } });
    assert.equal(h.badge.text, 'OFF');
  });

  it('switches to bleep live', async () => {
    const h = await start({ server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    await h.setSettings({ censorMethod: 'bleep' });
    h.video.currentTime = 9;
    await h.clock.advance(100);
    assert.equal(h.graph.calls.filter((c) => c[0] === 'apply').at(-1)[2], 'bleep');
  });

  it('applies a new offset live', async () => {
    const h = await start({ server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    h.video.currentTime = 9;
    await h.setSettings({ offsetMs: 200 });
    assert.ok(Math.abs(h.graph.lastPlan.events[0].time - (h.graph.currentTime + 1.2)) < 1e-9);
  });
});

describe('content app: ads', () => {
  async function playingThenAd() {
    const h = await start({ server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    await h.setAd(true);
    return h;
  }

  it('opens the graph during an ad', async () => {
    const h = await playingThenAd();
    assert.equal(h.app.censor.mode, 'open');
  });

  it('does not hold during an ad', async () => {
    const h = await playingThenAd();
    assert.equal(h.app.gate.heldByUs, false);
  });

  it('says AD on the badge', async () => {
    const h = await playingThenAd();
    assert.equal(h.badge.text, 'AD');
  });

  it('keeps heartbeating during an ad, with the playhead from before it', async () => {
    const h = await start({ server: () => ({ ok: true, view: view() }) });
    h.video.currentTime = 30;
    await h.clock.advance(0);
    await h.setAd(true);
    h.video.currentTime = 2;
    await h.clock.advance(HEARTBEAT_MS);
    assert.equal(h.heartbeats.at(-1).position, 30);
  });

  it('filters again after the ad', async () => {
    const h = await playingThenAd();
    await h.setAd(false);
    assert.equal(h.app.censor.mode, 'filter');
  });
});

describe('content app: navigating away (the whole scenario)', () => {
  /** Watch A, get past the hold, play, then go to the home page with the miniplayer playing. */
  async function watchThenLeave() {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    await h.play();
    await h.answer({ ok: true, view: view() });
    h.document.fire('yt-navigate-start');
    await flush();
    return h;
  }

  it('mutes at navigate-start', async () => {
    const h = await watchThenLeave();
    assert.equal(h.app.censor.mode, 'mute');
  });

  it('drops the view at navigate-start', async () => {
    const h = await watchThenLeave();
    assert.equal(h.app.state.view, null);
  });

  it('drops the hits at navigate-start', async () => {
    const h = await watchThenLeave();
    assert.deepEqual(h.app.derived.hits, []);
  });

  it('lets go of the video at navigate-start (no hold)', async () => {
    const h = await watchThenLeave();
    assert.equal(h.app.gate.heldByUs, false);
  });

  it('keeps the miniplayer muted on the home page', async () => {
    const h = await watchThenLeave();
    h.window.location.href = HOME;
    h.document.fire('yt-navigate-finish');
    await flush();
    assert.deepEqual([h.app.state.page.phase, h.video.paused, h.app.censor.mode], ['idle', false, 'mute']);
  });

  it('says the miniplayer is muted', async () => {
    const h = await watchThenLeave();
    h.window.location.href = HOME;
    h.document.fire('yt-navigate-finish');
    await flush();
    assert.equal(h.badge.text, 'MUTE');
  });

  it('shows the miniplayer note', async () => {
    const h = await watchThenLeave();
    h.window.location.href = HOME;
    h.document.fire('yt-navigate-finish');
    await flush();
    assert.equal(h.overlay.title, 'FoulFilter muted the miniplayer');
  });

  it('stops heartbeating on the home page', async () => {
    const h = await watchThenLeave();
    h.window.location.href = HOME;
    h.document.fire('yt-navigate-finish');
    await h.clock.advance(5000);
    assert.equal(h.heartbeats.length, 1);
  });

  it('holds the next video until it is covered', async () => {
    const h = await watchThenLeave();
    h.window.location.href = url(B);
    h.document.fire('yt-navigate-finish');
    await flush();
    assert.equal(h.app.derived.gate.reason, 'starting');
  });

  it('keeps the same graph for the next video (the element is reused)', async () => {
    const h = await watchThenLeave();
    h.window.location.href = url(B);
    h.document.fire('yt-navigate-finish');
    await flush();
    await h.play();
    assert.equal(h.graphs.length, 1);
  });
});

describe('content app: a replaced element', () => {
  async function replaced() {
    const h = await start({ server: () => ({ ok: true, view: view({ state: 'complete' }) }) });
    await h.clock.advance(0);
    await h.play();
    const old = h.video;
    old.isConnected = false;
    const next = new FakeVideo();
    h.document.elements.set(VIDEO_SELECTOR, next);
    h.document.fire('yt-navigate-finish');
    await flush();
    return { h, old, next };
  }

  it('mutes the old element', async () => {
    const { h } = await replaced();
    assert.deepEqual(h.graphs[0].last, ['mute']);
  });

  it('stops listening to the old element', async () => {
    const { old } = await replaced();
    assert.equal(old.count, 0);
  });

  it('puts the audio back to detached', async () => {
    const { h } = await replaced();
    assert.equal(h.app.state.audio.status, 'detached');
  });

  it('attaches the new element on its first play', async () => {
    const { h, next } = await replaced();
    next.play();
    await flush();
    assert.equal(h.attaches.at(-1), next);
  });
});

describe('content app: stop', () => {
  it('stops listening to the element', async () => {
    const h = await start();
    h.app.stop();
    assert.equal(h.video.count, 0);
  });

  it('stops the page watcher', async () => {
    const h = await start();
    h.app.stop();
    assert.equal(h.document.count, 0);
  });

  it('cancels the heartbeat timer', async () => {
    const h = await start();
    h.app.stop();
    await h.clock.advance(5000);
    assert.equal(h.heartbeats.length, 0);
  });

  it('destroys the overlay', async () => {
    const h = await start();
    h.app.stop();
    assert.equal(h.renders.at(-1).destroyed, true);
  });

  it('is idempotent', async () => {
    const h = await start();
    h.app.stop();
    assert.doesNotThrow(() => h.app.stop());
  });

  it('ignores a reply that arrives afterwards', async () => {
    const h = await start({ manual: true });
    await h.clock.advance(0);
    h.app.stop();
    await h.answer({ ok: true, view: view() });
    assert.equal(h.app.state.view, null);
  });
});
