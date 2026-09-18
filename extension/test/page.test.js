// page.js against a tiny fake page: EventTargets for the document and window,
// elements that are plain objects, and a MutationObserver the test fires by
// hand. Just enough to drive the adapter; not a DOM.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { AD_CLASS, PLAYER_SELECTOR, VIDEO_SELECTOR, watchPage } from '../src/page.js';

const A = 'dQw4w9WgXcQ';
const B = 'jNQXAC9IVRw';
const url = (id) => `https://www.youtube.com/watch?v=${id}`;
const HOME = 'https://www.youtube.com/';

/** An EventTarget that knows which listeners are attached. */
class TrackedTarget extends EventTarget {
  listeners = new Set();

  addEventListener(type, listener, options) {
    this.listeners.add(`${type}`);
    this.#all.push([type, listener]);
    super.addEventListener(type, listener, options);
  }

  removeEventListener(type, listener, options) {
    this.#all = this.#all.filter(([t, l]) => !(t === type && l === listener));
    super.removeEventListener(type, listener, options);
  }

  #all = [];

  get attached() {
    return this.#all.length;
  }
}

class FakeElement {
  isConnected = true;
  #classes;

  constructor(...classes) {
    this.#classes = new Set(classes);
    this.classList = { contains: (name) => this.#classes.has(name) };
  }

  setClass(name, on) {
    if (on) this.#classes.add(name);
    else this.#classes.delete(name);
  }
}

class FakeDocument extends TrackedTarget {
  /** @type {Map<string, FakeElement>} */
  elements = new Map();
  queries = 0;

  querySelector(selector) {
    this.queries++;
    const element = this.elements.get(selector);
    return element?.isConnected ? element : null;
  }
}

class FakeWindow extends TrackedTarget {
  location = { href: HOME };
}

/** A MutationObserver double: records what it observes, fires on demand. */
function fakeObservers() {
  /** @type {FakeObserver[]} */
  const all = [];
  class FakeObserver {
    targets = [];

    constructor(callback) {
      this.callback = callback;
      all.push(this);
    }

    observe(target, options) {
      this.targets.push({ target, options });
    }

    disconnect() {
      this.targets = [];
    }
  }
  return {
    MutationObserver: FakeObserver,
    all,
    /** Observers currently observing anything. */
    active: () => all.filter((o) => o.targets.length > 0),
    /** Deliver a mutation on `target` to every observer watching it. */
    fire(target) {
      for (const observer of all.filter((o) => o.targets.some((t) => t.target === target))) {
        observer.callback([], observer);
      }
    },
  };
}

/** A fake page, and a watcher started on it. */
function setup({ href = HOME, video = true, player = true, adShowing = false, start = true } = {}) {
  const document = new FakeDocument();
  const window = new FakeWindow();
  window.location.href = href;
  const observers = fakeObservers();
  const videoElement = new FakeElement('html5-main-video');
  const playerElement = new FakeElement(...(adShowing ? [AD_CLASS] : []));
  if (video) document.elements.set(VIDEO_SELECTOR, videoElement);
  if (player) document.elements.set(PLAYER_SELECTOR, playerElement);
  const changes = [];
  const page = {
    document,
    window,
    observers,
    videoElement,
    playerElement,
    changes,
    watcher: null,
    start() {
      page.watcher = watchPage({
        document,
        window,
        MutationObserver: observers.MutationObserver,
        onChange: (state, previous, event) => changes.push({ state, previous, event }),
      });
      return page.watcher;
    },
    /** YouTube navigating to `to`: start, location changes, finish. */
    navigate(to) {
      document.dispatchEvent(new Event('yt-navigate-start'));
      window.location.href = to;
      document.dispatchEvent(new Event('yt-navigate-finish'));
    },
    /** The ad class going on or off, as YouTube would. */
    setAd(on) {
      playerElement.setClass(AD_CLASS, on);
      observers.fire(playerElement);
    },
    /** An element being added to the document. */
    add(selector, element) {
      document.elements.set(selector, element);
      observers.fire(document);
    },
  };
  if (start) page.start();
  return page;
}

describe('selectors', () => {
  it('look for the main player element', () => {
    assert.equal(VIDEO_SELECTOR, 'video.html5-main-video');
  });

  it('look for the movie player', () => {
    assert.equal(PLAYER_SELECTOR, '#movie_player');
  });

  it('use the ad-showing class', () => {
    assert.equal(AD_CLASS, 'ad-showing');
  });
});

describe('watchPage: starting', () => {
  it('on a watch page is watching its video', () => {
    const { watcher } = setup({ href: url(A) });
    assert.equal(watcher.state.phase, 'watching');
  });

  it('on a watch page names the video', () => {
    assert.equal(setup({ href: url(A) }).watcher.state.videoId, A);
  });

  it('on a watch page is generation 1', () => {
    assert.equal(setup({ href: url(A) }).watcher.state.generation, 1);
  });

  it('on a watch page reports the change', () => {
    const { changes } = setup({ href: url(A) });
    assert.equal(changes.at(-1).state.videoId, A);
  });

  it('on the home page is idle', () => {
    assert.equal(setup({ href: HOME }).watcher.state.phase, 'idle');
  });

  it('finds an element that is already there', () => {
    assert.equal(setup({ href: url(A) }).watcher.state.hasVideoElement, true);
  });

  it('exposes the element it found', () => {
    const page = setup({ href: url(A) });
    assert.equal(page.watcher.video, page.videoElement);
  });

  it('reads an ad that is already showing', () => {
    assert.equal(setup({ href: url(A), adShowing: true }).watcher.state.adShowing, true);
  });

  it('reads no ad when none is showing', () => {
    assert.equal(setup({ href: url(A) }).watcher.state.adShowing, false);
  });

  it('is filterable straight away on a normal watch page', () => {
    const { watcher } = setup({ href: url(A) });
    assert.deepEqual(
      [watcher.state.phase, watcher.state.hasVideoElement, watcher.state.adShowing],
      ['watching', true, false],
    );
  });

  it('reports the element before the video, so watching begins with it found', () => {
    const { changes } = setup({ href: url(A) });
    assert.deepEqual(
      changes.map((c) => c.event.type),
      ['video-element', 'initial'],
    );
  });

  it('listens for YouTube\'s navigation events on the document', () => {
    const { document } = setup();
    assert.deepEqual([...document.listeners].sort(), ['yt-navigate-finish', 'yt-navigate-start']);
  });

  it('listens for pagehide and pageshow on the window', () => {
    const { window } = setup();
    assert.deepEqual([...window.listeners].sort(), ['pagehide', 'pageshow']);
  });

  it('observes no document mutations when both elements are there', () => {
    const { observers } = setup({ href: url(A) });
    assert.equal(
      observers.active().filter((o) => o.targets.some((t) => t.options.subtree)).length,
      0,
    );
  });

  it('watches only the player\'s class attribute', () => {
    const page = setup({ href: url(A) });
    const watching = page.observers.active().flatMap((o) => o.targets);
    assert.deepEqual(watching, [
      { target: page.playerElement, options: { attributes: true, attributeFilter: ['class'] } },
    ]);
  });

  it('uses window.MutationObserver when none is given', () => {
    const document = new FakeDocument();
    const window = new FakeWindow();
    const observers = fakeObservers();
    window.MutationObserver = observers.MutationObserver;
    watchPage({ document, window });
    assert.equal(observers.active().length, 1);
  });

  it('works without onChange', () => {
    const document = new FakeDocument();
    const window = new FakeWindow();
    window.location.href = url(A);
    const { MutationObserver } = fakeObservers();
    assert.equal(watchPage({ document, window, MutationObserver }).state.videoId, A);
  });
});

describe('watchPage: the element turning up late', () => {
  it('is not found at first', () => {
    assert.equal(setup({ href: url(A), video: false }).watcher.state.hasVideoElement, false);
  });

  it('has no element to expose at first', () => {
    assert.equal(setup({ href: url(A), video: false }).watcher.video, null);
  });

  it('watches the document for it', () => {
    const page = setup({ href: url(A), video: false });
    const finder = page.observers.active().find((o) => o.targets.some((t) => t.target === page.document));
    assert.deepEqual(finder.targets[0].options, { childList: true, subtree: true });
  });

  it('is found when a mutation adds it', () => {
    const page = setup({ href: url(A), video: false });
    page.add(VIDEO_SELECTOR, page.videoElement);
    assert.equal(page.watcher.state.hasVideoElement, true);
  });

  it('is exposed once found', () => {
    const page = setup({ href: url(A), video: false });
    page.add(VIDEO_SELECTOR, page.videoElement);
    assert.equal(page.watcher.video, page.videoElement);
  });

  it('stops watching the document once found', () => {
    const page = setup({ href: url(A), video: false });
    page.add(VIDEO_SELECTOR, page.videoElement);
    assert.equal(page.observers.active().some((o) => o.targets.some((t) => t.target === page.document)), false);
  });

  it('keeps watching through mutations that do not add it', () => {
    const page = setup({ href: url(A), video: false });
    page.observers.fire(page.document);
    page.observers.fire(page.document);
    assert.equal(page.observers.active().some((o) => o.targets.some((t) => t.target === page.document)), true);
  });

  it('reports nothing for mutations that do not add it', () => {
    const page = setup({ href: url(A), video: false });
    const before = page.changes.length;
    page.observers.fire(page.document);
    assert.equal(page.changes.length, before);
  });

  it('keeps the same watching period when found', () => {
    const page = setup({ href: url(A), video: false });
    page.add(VIDEO_SELECTOR, page.videoElement);
    assert.equal(page.watcher.state.generation, 1);
  });

  it('reports the element arriving as its own change', () => {
    const page = setup({ href: url(A), video: false });
    page.add(VIDEO_SELECTOR, page.videoElement);
    assert.equal(page.changes.at(-1).event.type, 'video-element');
  });

  it('uses a single document observer, not one per mutation', () => {
    const page = setup({ href: url(A), video: false });
    page.observers.fire(page.document);
    page.observers.fire(page.document);
    page.observers.fire(page.document);
    assert.equal(page.observers.all.filter((o) => o.targets.some((t) => t.target === page.document)).length, 1);
  });

  it('keeps watching the document for a late player after the element is found', () => {
    const page = setup({ href: url(A), video: false, player: false });
    page.add(VIDEO_SELECTOR, page.videoElement);
    assert.equal(page.observers.active().some((o) => o.targets.some((t) => t.target === page.document)), true);
  });

  it('starts watching ads when the player turns up late', () => {
    const page = setup({ href: url(A), player: false });
    page.add(PLAYER_SELECTOR, page.playerElement);
    page.setAd(true);
    assert.equal(page.watcher.state.adShowing, true);
  });

  it('reads an ad already showing on a late player', () => {
    const page = setup({ href: url(A), player: false });
    page.playerElement.setClass(AD_CLASS, true);
    page.add(PLAYER_SELECTOR, page.playerElement);
    assert.equal(page.watcher.state.adShowing, true);
  });

  it('is picked up by a navigation even with no mutation', () => {
    const page = setup({ href: url(A), video: false });
    page.document.elements.set(VIDEO_SELECTOR, page.videoElement);
    page.navigate(url(B));
    assert.equal(page.watcher.state.hasVideoElement, true);
  });
});

describe('watchPage: navigation', () => {
  it('navigate-start makes it leaving', () => {
    const page = setup({ href: url(A) });
    page.document.dispatchEvent(new Event('yt-navigate-start'));
    assert.equal(page.watcher.state.phase, 'leaving');
  });

  it('does not read location on navigate-start (it already shows the next video)', () => {
    const page = setup({ href: url(A) });
    page.window.location.href = url(B);
    page.document.dispatchEvent(new Event('yt-navigate-start'));
    assert.equal(page.watcher.state.videoId, null);
  });

  it('navigate-finish on a watch page watches the new video', () => {
    const page = setup({ href: url(A) });
    page.navigate(url(B));
    assert.equal(page.watcher.state.videoId, B);
  });

  it('navigate-finish starts a new generation', () => {
    const page = setup({ href: url(A) });
    page.navigate(url(B));
    assert.equal(page.watcher.state.generation, 2);
  });

  it('reports leaving then watching', () => {
    const page = setup({ href: url(A) });
    page.changes.length = 0;
    page.navigate(url(B));
    assert.deepEqual(
      page.changes.map((c) => c.state.phase),
      ['leaving', 'watching'],
    );
  });

  it('navigate-finish on the home page goes idle', () => {
    const page = setup({ href: url(A) });
    page.navigate(HOME);
    assert.equal(page.watcher.state.phase, 'idle');
  });

  it('from the home page to a watch page watches it', () => {
    const page = setup({ href: HOME });
    page.navigate(url(A));
    assert.equal(page.watcher.state.videoId, A);
  });

  it('keeps the same element across navigations (YouTube reuses it)', () => {
    const page = setup({ href: url(A) });
    page.navigate(url(B));
    assert.equal(page.watcher.video, page.videoElement);
  });

  it('does not look the element up again while it is still in the document', () => {
    const page = setup({ href: url(A) });
    const before = page.document.queries;
    page.navigate(url(B));
    page.navigate(url(A));
    assert.equal(page.document.queries, before);
  });

  it('does not report the element again on navigation', () => {
    const page = setup({ href: url(A) });
    page.changes.length = 0;
    page.navigate(url(B));
    assert.equal(page.changes.some((c) => c.event.type === 'video-element'), false);
  });

  it('notices an element that was replaced, at the next navigation', () => {
    const page = setup({ href: url(A) });
    const replacement = new FakeElement('html5-main-video');
    page.videoElement.isConnected = false;
    page.document.elements.set(VIDEO_SELECTOR, replacement);
    page.navigate(url(B));
    assert.equal(page.watcher.video, replacement);
  });

  it('reports a replaced element as gone then present', () => {
    const page = setup({ href: url(A) });
    page.changes.length = 0;
    page.videoElement.isConnected = false;
    page.document.elements.set(VIDEO_SELECTOR, new FakeElement('html5-main-video'));
    page.navigate(url(B));
    assert.deepEqual(
      page.changes.filter((c) => c.event.type === 'video-element').map((c) => c.event.present),
      [false, true],
    );
  });

  it('reports an element that left and was not replaced as gone', () => {
    const page = setup({ href: url(A) });
    page.videoElement.isConnected = false;
    page.navigate(url(B));
    assert.equal(page.watcher.state.hasVideoElement, false);
  });

  it('looks for a replacement with the observer once the element has gone', () => {
    const page = setup({ href: url(A) });
    page.videoElement.isConnected = false;
    page.navigate(url(B));
    const replacement = new FakeElement('html5-main-video');
    page.add(VIDEO_SELECTOR, replacement);
    assert.equal(page.watcher.video, replacement);
  });

  it('follows ads on a replaced player', () => {
    const page = setup({ href: url(A) });
    const replacement = new FakeElement();
    page.playerElement.isConnected = false;
    page.document.elements.set(PLAYER_SELECTOR, replacement);
    page.navigate(url(B));
    replacement.setClass(AD_CLASS, true);
    page.observers.fire(replacement);
    assert.equal(page.watcher.state.adShowing, true);
  });

  it('stops watching a player that left the document', () => {
    const page = setup({ href: url(A) });
    page.playerElement.isConnected = false;
    page.navigate(url(B));
    assert.equal(page.observers.active().some((o) => o.targets.some((t) => t.target === page.playerElement)), false);
  });

  it('clears the ad when the player showing it leaves the document', () => {
    const page = setup({ href: url(A), adShowing: true });
    page.playerElement.isConnected = false;
    page.navigate(url(B));
    assert.equal(page.watcher.state.adShowing, false);
  });
});

describe('watchPage: ads', () => {
  it('the class going on shows an ad', () => {
    const page = setup({ href: url(A) });
    page.setAd(true);
    assert.equal(page.watcher.state.adShowing, true);
  });

  it('the class coming off ends the ad', () => {
    const page = setup({ href: url(A) });
    page.setAd(true);
    page.setAd(false);
    assert.equal(page.watcher.state.adShowing, false);
  });

  it('an unrelated class change reports nothing', () => {
    const page = setup({ href: url(A) });
    const before = page.changes.length;
    page.playerElement.setClass('playing-mode', true);
    page.observers.fire(page.playerElement);
    assert.equal(page.changes.length, before);
  });

  it('an ad keeps the video being watched', () => {
    const page = setup({ href: url(A) });
    page.setAd(true);
    assert.equal(page.watcher.state.videoId, A);
  });

  it('an ad during leaving is recorded', () => {
    const page = setup({ href: url(A) });
    page.document.dispatchEvent(new Event('yt-navigate-start'));
    page.setAd(true);
    assert.deepEqual([page.watcher.state.phase, page.watcher.state.adShowing], ['leaving', true]);
  });

  it('several toggles are each reported', () => {
    const page = setup({ href: url(A) });
    page.changes.length = 0;
    page.setAd(true);
    page.setAd(false);
    page.setAd(true);
    assert.deepEqual(
      page.changes.map((c) => c.state.adShowing),
      [true, false, true],
    );
  });
});

describe('watchPage: pagehide and pageshow', () => {
  it('pagehide goes idle', () => {
    const page = setup({ href: url(A) });
    page.window.dispatchEvent(new Event('pagehide'));
    assert.equal(page.watcher.state.phase, 'idle');
  });

  it('pageshow from the back/forward cache watches again', () => {
    const page = setup({ href: url(A) });
    page.window.dispatchEvent(new Event('pagehide'));
    const show = new Event('pageshow');
    show.persisted = true;
    page.window.dispatchEvent(show);
    assert.deepEqual([page.watcher.state.phase, page.watcher.state.videoId], ['watching', A]);
  });

  it('pageshow from the back/forward cache starts a new generation', () => {
    const page = setup({ href: url(A) });
    page.window.dispatchEvent(new Event('pagehide'));
    const show = new Event('pageshow');
    show.persisted = true;
    page.window.dispatchEvent(show);
    assert.equal(page.watcher.state.generation, 2);
  });

  it('a first-load pageshow changes nothing', () => {
    const page = setup({ href: url(A) });
    const before = page.changes.length;
    page.window.dispatchEvent(new Event('pageshow'));
    assert.equal(page.changes.length, before);
  });
});

describe('watchPage: stop', () => {
  it('removes every document listener', () => {
    const page = setup({ href: url(A) });
    page.watcher.stop();
    assert.equal(page.document.attached, 0);
  });

  it('removes every window listener', () => {
    const page = setup({ href: url(A) });
    page.watcher.stop();
    assert.equal(page.window.attached, 0);
  });

  it('disconnects the ad observer', () => {
    const page = setup({ href: url(A) });
    page.watcher.stop();
    assert.equal(page.observers.active().length, 0);
  });

  it('disconnects the document observer while still looking', () => {
    const page = setup({ href: url(A), video: false, player: false });
    page.watcher.stop();
    assert.equal(page.observers.active().length, 0);
  });

  it('reports nothing afterwards', () => {
    const page = setup({ href: url(A) });
    page.watcher.stop();
    const before = page.changes.length;
    page.navigate(url(B));
    page.window.dispatchEvent(new Event('pagehide'));
    assert.equal(page.changes.length, before);
  });

  it('ignores a mutation already queued when it stopped', () => {
    const page = setup({ href: url(A), video: false });
    const finder = page.observers.active()[0].callback;
    page.watcher.stop();
    page.document.elements.set(VIDEO_SELECTOR, page.videoElement);
    finder([], null);
    assert.equal(page.watcher.state.hasVideoElement, false);
  });

  it('ignores an ad change already queued when it stopped', () => {
    const page = setup({ href: url(A) });
    const adCallback = page.observers.active()[0].callback;
    page.watcher.stop();
    page.playerElement.setClass(AD_CLASS, true);
    adCallback([], null);
    assert.equal(page.watcher.state.adShowing, false);
  });

  it('keeps the last state readable', () => {
    const page = setup({ href: url(A) });
    page.watcher.stop();
    assert.equal(page.watcher.state.videoId, A);
  });

  it('can be called twice', () => {
    const page = setup({ href: url(A) });
    page.watcher.stop();
    assert.doesNotThrow(() => page.watcher.stop());
  });

  it('leaves no observer behind after a busy life', () => {
    const page = setup({ href: url(A), video: false, player: false });
    page.add(VIDEO_SELECTOR, page.videoElement);
    page.add(PLAYER_SELECTOR, page.playerElement);
    page.setAd(true);
    page.navigate(url(B));
    page.playerElement.isConnected = false;
    page.navigate(url(A));
    page.watcher.stop();
    assert.deepEqual(
      [page.observers.active().length, page.document.attached, page.window.attached],
      [0, 0, 0],
    );
  });
});

describe('watchPage: the ADR-0007 sequence end to end', () => {
  it('mutes while the old video plays under the new URL, then watches the new one', async () => {
    const { shouldMute, isFilterable } = await import('../src/page-state.js');
    const page = setup({ href: url(A) });
    const seen = [];
    const record = () => seen.push([shouldMute(page.watcher.state), isFilterable(page.watcher.state)]);
    record();
    page.document.dispatchEvent(new Event('yt-navigate-start'));
    record();
    page.window.location.href = url(B);
    record();
    page.document.dispatchEvent(new Event('yt-navigate-finish'));
    record();
    assert.deepEqual(seen, [
      [false, true],
      [true, false],
      [true, false],
      [false, true],
    ]);
  });
});
